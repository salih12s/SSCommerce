using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using SSCommerce.Application.Orders;
using SSCommerce.Domain.Inventory;
using SSCommerce.Domain.Ordering;
using SSCommerce.Infrastructure.Common;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Orders;

/// <summary>
/// Durum geçişi ve iptal (F7, D-062/D-063). TEK READ COMMITTED transaction; sıra <c>docs/CONTRACT_PHASE_7.md</c>'de sabittir:
/// <list type="number">
/// <item><c>SET LOCAL lock_timeout = 5s</c>; <c>SELECT … FROM orders WHERE id = @id [AND user_id = @owner] FOR UPDATE</c>: SIRALAMA NOKTASI.
/// İptal-iptal ve iptal-sevkiyat bu satır kilidinde sıraya girer; kaybeden güncel durumu görür (READ COMMITTED yeniden okur). Yok ya da başkasının: 404.</item>
/// <item>Hedef durum zaten mevcut durumsa: replay (yazma yok). Aksi halde izin yalnız <see cref="OrderStateMachine.Evaluate"/>'dedir
/// (geçiş tablosu + rol): geçersiz => 409, rol yetmez => 403.</item>
/// <item>İptalde dağıtımlar (varyant, depo kodu) sırasıyla — sipariş vermeyle AYNI bakiye kilit sırası — özgün depoya iade edilir:
/// <c>INSERT … ON CONFLICT (warehouse_id, product_variant_id) DO UPDATE … WHERE on_hand + q &lt;= max RETURNING</c>. 0 satır => hepsi geri alınır,
/// <see cref="OrderOperationStatus.StockLimitExceeded"/>, sipariş değişmez. Pasif depo/varyant yine de iade alır (mal fiziksel olarak oradadır).</item>
/// <item>Dağıtım başına iade hareketi (<c>order-cancel-&lt;allocationId&gt;</c>): UNIQUE indeks çift iadeyi veritabanında imkânsız kılar.</item>
/// <item>Durum + geçmiş satırı; commit. Hareketler hiçbir zaman güncellenmez/silinmez (trigger).</item>
/// </list>
/// Müşteri yalnız kendi <c>placed</c> siparişini iptal eder; Warehouse iptal edemez (iptal stok yazar, D-045); yanıt kişisel veri sızdırmaz
/// (personel görünümünde adres yoktur).
/// </summary>
internal sealed class OrderStatusService(
    SSCommerceDbContext db,
    OrderReader reader,
    TimeProvider clock,
    ILogger<OrderStatusService> logger) : IOrderStatusService
{
    private sealed record Outcome(OrderOperationStatus Status);

    private sealed record AllocationRow(Guid Id, Guid WarehouseId, Guid VariantId, int Quantity, string Sku);

    public async Task<OrderOperationResult> ChangeStatusAsync(OrderStatusCommand command, CancellationToken cancellationToken)
    {
        Outcome outcome;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            outcome = await RunAsync(transaction, command, cancellationToken);
            if (outcome.Status == OrderOperationStatus.Changed)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (TransactionalSql.IsLockTimeout(exception))
        {
            return new OrderOperationResult(OrderOperationStatus.Busy);
        }

        if (outcome.Status is not (OrderOperationStatus.Changed or OrderOperationStatus.Replayed))
        {
            return new OrderOperationResult(outcome.Status);
        }

        var replayed = outcome.Status == OrderOperationStatus.Replayed;
        if (command.Actor == OrderActor.Customer)
        {
            var customerView = await reader.LoadForCustomerAsync(command.OrderId, command.OwnerUserId!.Value, replayed, cancellationToken)
                ?? throw new InvalidOperationException("Kalıcı sipariş okunamadı.");
            LogChange(command, replayed);
            return new OrderOperationResult(outcome.Status, Order: customerView);
        }

        var staffView = await reader.LoadForStaffAsync(command.OrderId, replayed, cancellationToken)
            ?? throw new InvalidOperationException("Kalıcı sipariş okunamadı.");
        LogChange(command, replayed);
        return new OrderOperationResult(outcome.Status, StaffOrder: staffView);
    }

    private void LogChange(OrderStatusCommand command, bool replayed)
    {
        if (!replayed)
        {
            // Yalnız sipariş kimliği ve hedef durum: teslimat bilgisi ve kullanıcı kimliği loga girmez.
            logger.LogInformation("Sipariş durumu değişti: {OrderId} -> {Target}", command.OrderId, OrderStatuses.ToWire(command.Target));
        }
    }

    private async Task<Outcome> RunAsync(IDbContextTransaction transaction, OrderStatusCommand command, CancellationToken cancellationToken)
    {
        await TransactionalSql.ExecuteAsync(db, transaction, TransactionalSql.LockTimeoutSql, cancellationToken);

        // Sıralama noktası. İki sabit SQL: sahiplik kapsamı varsa user_id koşulu SORGUDA (başkasının siparişi "yok" ile aynı).
        var locked = command.OwnerUserId is { } owner
            ? await TransactionalSql.ScalarAsync(
                db,
                transaction,
                "SELECT id FROM orders WHERE id = @id AND user_id = @owner FOR UPDATE",
                cancellationToken,
                ("id", command.OrderId),
                ("owner", owner))
            : await TransactionalSql.ScalarAsync(
                db, transaction, "SELECT id FROM orders WHERE id = @id FOR UPDATE", cancellationToken, ("id", command.OrderId));
        if (locked is not Guid)
        {
            return new Outcome(OrderOperationStatus.NotFound);
        }

        // Kilidi aldıktan SONRA okunur: bekleyen istek rakibin commit'ini görür.
        var order = await db.Orders.FirstAsync(o => o.Id == command.OrderId, cancellationToken);
        switch (OrderStateMachine.Evaluate(order.Status, command.Target, command.Actor))
        {
            case OrderTransitionDecision.SameStatus:
                return new Outcome(OrderOperationStatus.Replayed);
            case OrderTransitionDecision.InvalidTransition:
                return new Outcome(OrderOperationStatus.TransitionInvalid);
            case OrderTransitionDecision.Forbidden:
                return new Outcome(OrderOperationStatus.Forbidden);
        }

        var now = clock.GetUtcNow();
        if (OrderStateMachine.RequiresRestock(command.Target) && !await RestockAsync(transaction, order, command.ActorUserId, now, cancellationToken))
        {
            return new Outcome(OrderOperationStatus.StockLimitExceeded);
        }

        db.OrderStatusHistory.Add(order.ChangeStatus(command.Target, command.Actor, command.ActorUserId, command.Reason, now));
        await db.SaveChangesAsync(cancellationToken);
        return new Outcome(OrderOperationStatus.Changed);
    }

    /// <summary>
    /// Dağıtımları özgün depolara iade eder (sipariş vermeyle aynı kilit sırası). Üst sınır aşılırsa false: çağıran rollback yapar,
    /// hareketler henüz kaydedilmediği ve bakiye güncellemeleri transaction içinde olduğu için hiçbir şey kalıcı olmaz.
    /// </summary>
    private async Task<bool> RestockAsync(IDbContextTransaction transaction, Order order, Guid actorUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var allocations = new List<AllocationRow>();
        await using (var read = TransactionalSql.Create(
                         db,
                         transaction,
                         """
                         SELECT a.id, a.warehouse_id, a.product_variant_id, a.quantity, i.sku
                         FROM order_inventory_allocations a
                         JOIN order_items i ON i.id = a.order_item_id
                         JOIN warehouses w ON w.id = a.warehouse_id
                         WHERE i.order_id = @order
                         ORDER BY a.product_variant_id, w.code COLLATE "C"
                         """))
        {
            read.Parameters.AddWithValue("order", order.Id);
            await using var rows = await read.ExecuteReaderAsync(cancellationToken);
            while (await rows.ReadAsync(cancellationToken))
            {
                allocations.Add(new AllocationRow(rows.GetGuid(0), rows.GetGuid(1), rows.GetGuid(2), rows.GetInt32(3), rows.GetString(4)));
            }
        }

        foreach (var allocation in allocations)
        {
            var after = await TransactionalSql.ScalarAsync(
                db,
                transaction,
                """
                INSERT INTO inventory_balances (id, warehouse_id, product_variant_id, on_hand_quantity, version, created_at, updated_at)
                VALUES (@id, @warehouse, @variant, @quantity, 1, @now, @now)
                ON CONFLICT (warehouse_id, product_variant_id) DO UPDATE
                SET on_hand_quantity = inventory_balances.on_hand_quantity + EXCLUDED.on_hand_quantity,
                    version = inventory_balances.version + 1,
                    updated_at = EXCLUDED.updated_at
                WHERE inventory_balances.on_hand_quantity + EXCLUDED.on_hand_quantity <= @max
                RETURNING on_hand_quantity
                """,
                cancellationToken,
                ("id", Guid.CreateVersion7()),
                ("warehouse", allocation.WarehouseId),
                ("variant", allocation.VariantId),
                ("quantity", allocation.Quantity),
                ("now", now),
                ("max", InventoryRules.MaxOnHandQuantity));
            if (after is not int onHandAfter)
            {
                return false;
            }

            db.StockMovements.Add(new StockMovement(
                allocation.WarehouseId,
                allocation.VariantId,
                allocation.Sku,
                allocation.Quantity,
                StockOperationType.OrderCancellation,
                actorUserId,
                OrderLedger.CancellationReason,
                order.DisplayNumber,
                OrderLedger.CancellationKey(allocation.Id),
                OrderLedger.CancellationFingerprint(order.Id, allocation.Id, allocation.Quantity),
                onHandAfter,
                now));
        }

        return true;
    }
}

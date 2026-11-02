using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using SSCommerce.Application.Carts;
using SSCommerce.Application.Orders;
using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Inventory;
using SSCommerce.Domain.Ordering;
using SSCommerce.Infrastructure.Common;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Orders;

/// <summary>
/// Sipariş verme (F7, D-059..D-062). TEK READ COMMITTED transaction'dır ve sırası <c>docs/CONTRACT_PHASE_7.md</c>'de sabittir:
/// <list type="number">
/// <item><c>SET LOCAL lock_timeout = 5s</c>: kilit beklemesi sonsuz sürmez; 55P03 => <see cref="OrderOperationStatus.Busy"/>.</item>
/// <item><c>pg_advisory_xact_lock(hashtextextended('order:' || kullanıcı || ':' || anahtar, 0))</c>: AYNI (kullanıcı, anahtar) istekleri sıraya girer.
/// Kilit commit/rollback'te kendiliğinden düşer. Anahtar HİÇBİR ZAMAN <c>user_id</c> olmadan aranmaz: başka kullanıcının siparişi sızmaz.</item>
/// <item>Anahtar daha önce işlendiyse parmak izi karşılaştırılır: aynı => ilk siparişin şimdiki hali (replay, yazma yok), farklı => <see cref="OrderOperationStatus.KeyReused"/>.</item>
/// <item><c>carts … FOR UPDATE</c> (KULLANICI KİLİDİ): aynı kullanıcının sepet yazmaları ve diğer siparişleri commit'e kadar bekler.</item>
/// <item>Adres <c>(id, user_id)</c> ile okunur: başkasının adresi "yok" ile aynıdır.</item>
/// <item>Sepet satırları varyant/ürün/kategori ile <c>ORDER BY v.id FOR SHARE OF v, p, c</c>: eşzamanlı fiyat ya da aktiflik güncellemesi bu transaction
/// bitene kadar BEKLER; doğrulanan fiyat commit anındaki fiyattır.</item>
/// <item>Ortak <see cref="CartCalculator"/> hesabı; taşma, pasif satır, quote farkı sırasıyla reddedilir (<see cref="OrderQuote"/> önizlemeyle aynı fonksiyon).</item>
/// <item>BAKİYE KİLİT SIRASI (tüm çok satırlı akışların TEK sırası): <c>ORDER BY varyant, depo kodu (COLLATE "C") FOR UPDATE OF b FOR SHARE OF w</c>.
/// Siparişler, iptaller ve (tek satır kilitleyen) admin stok işlemi bu sırayla çakışsa da döngü oluşturamaz => deadlock yok.</item>
/// <item>Aç gözlü dağıtım (varyant, depo kodu) sırasıyla; bir satır birden çok depoya bölünebilir (D-062); eksik her satır listelenir.</item>
/// <item>Her dağıtım için koşullu <c>UPDATE … WHERE on_hand_quantity &gt;= q RETURNING</c> (kilit altında ulaşılamaz; koruma durur).</item>
/// <item>Sipariş (kimlik sütunu sıra numarasını verir), kalemler, düşüm hareketleri, dağıtımlar, ilk geçmiş satırı; sonra sepet satırları silinir.</item>
/// <item>Commit; DTO commit'ten SONRA kalıcı durumdan okunur.</item>
/// </list>
/// Reddedilen her yol rollback'tir: bakiye, hareket, sepet ve anahtar değişmez. <c>ux_orders_user_id_idempotency_key</c> son savunma hattıdır.
/// Veritabanının zorlamadığı iki değişmez (ara toplam = kalem toplamı, dağıtım varyantı = kalem varyantı) yazmadan önce
/// <see cref="OrderInvariants"/> ile doğrulanır. Teslimat bilgisi (telefon/adres/e-posta) loga YAZILMAZ; yalnız sipariş kimliği loglanır.
/// </summary>
internal sealed class OrderPlacementService(
    SSCommerceDbContext db,
    OrderReader reader,
    TimeProvider clock,
    ILogger<OrderPlacementService> logger) : IOrderPlacementService
{
    private sealed record Outcome(OrderOperationStatus Status, Guid? OrderId = null, IReadOnlyList<Guid>? ItemIds = null);

    private sealed record LineRow(
        Guid ItemId,
        Guid VariantId,
        string Sku,
        string ProductSlug,
        string ProductName,
        string VariantName,
        string? ImageUrl,
        string? ImageAlt,
        int Quantity,
        decimal Price,
        bool Purchasable);

    private sealed record BalanceRow(Guid Id, Guid VariantId, Guid WarehouseId, int OnHand);

    private sealed record Allocation(LineRow Line, BalanceRow Balance, int Quantity);

    public async Task<OrderOperationResult> PlaceAsync(PlaceOrderCommand command, CancellationToken cancellationToken)
    {
        var fingerprint = OrderFingerprint.Compute(command.AddressId, command.Quote);
        Outcome outcome;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            outcome = await RunAsync(transaction, command, fingerprint, cancellationToken);
            if (outcome.Status == OrderOperationStatus.Placed)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                // Her ret rollback'tir (replay dahil: okuma dışında yazılan bir şey yok).
                await transaction.RollbackAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (TransactionalSql.IsLockTimeout(exception) || IsKeyRace(exception))
        {
            // Dispose rollback yapar; hiçbir şey yazılmadı. Anahtar yarışı (kilit varken beklenmez) da aynı güvenli yanıtı alır: aynı anahtarla tekrar replay olur.
            return new OrderOperationResult(OrderOperationStatus.Busy);
        }

        switch (outcome.Status)
        {
            case OrderOperationStatus.Placed:
            case OrderOperationStatus.Replayed:
                var replayed = outcome.Status == OrderOperationStatus.Replayed;
                var order = await reader.LoadForCustomerAsync(outcome.OrderId!.Value, command.UserId, replayed, cancellationToken)
                    ?? throw new InvalidOperationException("Kalıcı sipariş okunamadı.");
                if (!replayed)
                {
                    logger.LogInformation("Sipariş oluşturuldu: {OrderId}", order.Id);
                }

                return new OrderOperationResult(outcome.Status, order);
            default:
                return new OrderOperationResult(outcome.Status, ItemIds: outcome.ItemIds);
        }
    }

    private async Task<Outcome> RunAsync(IDbContextTransaction transaction, PlaceOrderCommand command, string fingerprint, CancellationToken cancellationToken)
    {
        // 1-2) Kilit zaman aşımı + (kullanıcı, anahtar) advisory kilidi.
        await TransactionalSql.ExecuteAsync(db, transaction, TransactionalSql.LockTimeoutSql, cancellationToken);
        await TransactionalSql.ExecuteAsync(
            db,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))",
            cancellationToken,
            ("key", $"order:{command.UserId:D}:{command.IdempotencyKey}"));

        // 3) Anahtar daha önce işlendi mi? Kilidi aldıktan SONRA okunur (READ COMMITTED: bekleyen kopya ilk kopyanın commit'ini görür). HER ZAMAN user_id ile.
        await using (var existing = TransactionalSql.Create(
                         db, transaction, "SELECT id, request_fingerprint FROM orders WHERE user_id = @user AND idempotency_key = @key"))
        {
            existing.Parameters.AddWithValue("user", command.UserId);
            existing.Parameters.AddWithValue("key", command.IdempotencyKey);
            await using var reader1 = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader1.ReadAsync(cancellationToken))
            {
                var existingId = reader1.GetGuid(0);
                var existingFingerprint = reader1.GetString(1).Trim();
                return string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal)
                    ? new Outcome(OrderOperationStatus.Replayed, existingId)
                    : new Outcome(OrderOperationStatus.KeyReused);
            }
        }

        // 4) Kullanıcı kilidi: sepet satırı FOR UPDATE. Sepet yoksa sipariş de yoktur.
        if (await TransactionalSql.ScalarAsync(
                db, transaction, "SELECT id FROM carts WHERE user_id = @user FOR UPDATE", cancellationToken, ("user", command.UserId)) is not Guid cartId)
        {
            return new Outcome(OrderOperationStatus.CartEmpty);
        }

        // 5) Adres, sahibiyle birlikte. Başkasının adresi ile olmayan adres aynı sonucu verir.
        var address = await db.CustomerAddresses.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == command.AddressId && a.UserId == command.UserId, cancellationToken);
        if (address is null)
        {
            return new Outcome(OrderOperationStatus.AddressNotFound);
        }

        // 6) Sepet satırları + varyant/ürün/kategori satır kilitleri (fiyat/aktiflik güncellemeleri bu transaction bitene kadar bekler).
        var lines = await ReadLinesAsync(transaction, cartId, cancellationToken);
        if (lines.Count == 0)
        {
            return new Outcome(OrderOperationStatus.CartEmpty);
        }

        // 7) Ortak hesap. Stok burada ölçülmez (AvailableOnHand = sınırsız): stok 8-9. adımlarda KİLİTLİ bakiyelerle denetlenir.
        var calculation = CartCalculator.Calculate(lines.Select(ToCartLine).ToList());
        if (calculation.TooLarge)
        {
            return new Outcome(OrderOperationStatus.CartTotalTooLarge);
        }

        var cart = calculation.Cart!;
        var inactive = cart.Items.Where(i => i.Status == CartLineStatus.Unavailable).Select(i => i.Id).ToList();
        if (inactive.Count > 0)
        {
            return new Outcome(OrderOperationStatus.ItemInactive, ItemIds: inactive);
        }

        if (cart.Items.Any(i => i.Status != CartLineStatus.Available))
        {
            // Stok sınırsız verildiği için yalnız "available" ya da "unavailable" üretilebilir; başka bir durum veri bütünlüğü hatasıdır.
            throw new InvalidOperationException("Beklenmeyen sepet satırı durumu.");
        }

        if (!string.Equals(OrderQuote.Compute(cart), command.Quote, StringComparison.Ordinal))
        {
            return new Outcome(OrderOperationStatus.QuoteChanged);
        }

        // 8-9) Bakiyeleri TEK kilit sırasıyla kilitle, aç gözlü dağıt.
        var balances = await LockBalancesAsync(transaction, lines.Select(l => l.VariantId).ToArray(), cancellationToken);
        var allocations = new List<Allocation>();
        var shortItems = new List<Guid>();
        foreach (var line in lines)
        {
            var remaining = line.Quantity;
            foreach (var balance in balances.GetValueOrDefault(line.VariantId) ?? [])
            {
                if (remaining == 0)
                {
                    break;
                }

                var take = Math.Min(remaining, balance.OnHand);
                if (take > 0)
                {
                    allocations.Add(new Allocation(line, balance, take));
                    remaining -= take;
                }
            }

            if (remaining > 0)
            {
                shortItems.Add(line.ItemId);
            }
        }

        if (shortItems.Count > 0)
        {
            return new Outcome(OrderOperationStatus.InsufficientStock, ItemIds: shortItems);
        }

        var now = clock.GetUtcNow();

        // 10) Koşullu düşüm. Kilit altında ulaşılamaz; yine de 0 satır => hiçbir şey yazılmasın.
        var onHandAfter = new Dictionary<Allocation, int>();
        foreach (var allocation in allocations)
        {
            var after = await TransactionalSql.ScalarAsync(
                db,
                transaction,
                """
                UPDATE inventory_balances
                SET on_hand_quantity = on_hand_quantity - @quantity, version = version + 1, updated_at = @now
                WHERE id = @id AND on_hand_quantity >= @quantity
                RETURNING on_hand_quantity
                """,
                cancellationToken,
                ("quantity", allocation.Quantity),
                ("now", now),
                ("id", allocation.Balance.Id));
            if (after is not int remainingOnHand)
            {
                return new Outcome(OrderOperationStatus.Busy);
            }

            onHandAfter[allocation] = remainingOnHand;
        }

        // 11) Sipariş başlığı önce: sıra numarası (kimlik sütunu) ekleme ile gelir ve hareket referansında kullanılır.
        var itemSubtotal = OrderRules.SumLineSubtotals(lines.Select(l => OrderRules.ComputeLineSubtotal(l.Price, l.Quantity)));
        if (Money.ToMinor(itemSubtotal) != cart.SubtotalMinor)
        {
            throw new InvalidOperationException("Sipariş ara toplamı sepet hesabıyla uyuşmuyor.");
        }

        var order = new Order(command.UserId, command.IdempotencyKey, fingerprint, address, itemSubtotal);
        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);

        var items = lines.ToDictionary(
            l => l.VariantId,
            l => new OrderItem(order.Id, l.VariantId, l.Sku, l.ProductName, l.VariantName, l.ProductSlug, l.ImageUrl, l.ImageAlt, l.Price, l.Quantity));
        db.OrderItems.AddRange(items.Values);

        var allocationFacts = new List<OrderInvariants.AllocationFacts>();
        foreach (var allocation in allocations)
        {
            var item = items[allocation.Line.VariantId];
            var allocationId = Guid.CreateVersion7();
            var delta = -allocation.Quantity;
            var movement = new StockMovement(
                allocation.Balance.WarehouseId,
                allocation.Line.VariantId,
                allocation.Line.Sku,
                delta,
                StockOperationType.OrderPlacement,
                command.UserId,
                OrderLedger.PlacementReason,
                order.DisplayNumber,
                OrderLedger.PlacementKey(allocationId),
                OrderLedger.PlacementFingerprint(order.Id, allocationId, delta),
                onHandAfter[allocation],
                now);
            db.StockMovements.Add(movement);

            var row = new OrderInventoryAllocation(item.Id, allocation.Balance.WarehouseId, allocation.Line.VariantId, allocation.Quantity, movement.Id);
            db.OrderInventoryAllocations.Add(row);
            // Domain kurucusu kimliği kendisi üretir; sözleşme düşüm anahtarının AYNI kimlikten türemesini ister (order-place-<allocationId>).
            // Hareket, dağıtımdan önce kurulmak zorunda olduğundan (dağıtım hareket kimliğini taşır) kimlik EF üzerinden, Added durumunda atanır.
            db.Entry(row).Property(a => a.Id).CurrentValue = allocationId;
            allocationFacts.Add(new OrderInvariants.AllocationFacts(item.Id, allocation.Line.VariantId, allocation.Quantity));
        }

        // Veritabanının zorlamadığı değişmezler: ara toplam = kalem toplamı, dağıtım varyantı = kalem varyantı, parça toplamı = kalem adedi.
        OrderInvariants.Verify(
            order.ItemSubtotal,
            items.Values.Select(i => new OrderInvariants.ItemFacts(i.Id, i.ProductVariantId, i.Quantity, i.LineSubtotal)).ToList(),
            allocationFacts);

        db.OrderStatusHistory.Add(order.CreateInitialHistory(now));
        await db.SaveChangesAsync(cancellationToken);

        // 12) Sepet temizliği: satırlar silinir, sepet satırı kalır.
        await TransactionalSql.ExecuteAsync(db, transaction, "DELETE FROM cart_items WHERE cart_id = @cart", cancellationToken, ("cart", cartId));
        await TransactionalSql.ExecuteAsync(
            db, transaction, "UPDATE carts SET updated_at = @now WHERE id = @id", cancellationToken, ("now", now), ("id", cartId));

        return new Outcome(OrderOperationStatus.Placed, order.Id);
    }

    private async Task<List<LineRow>> ReadLinesAsync(IDbContextTransaction transaction, Guid cartId, CancellationToken cancellationToken)
    {
        await using var sql = TransactionalSql.Create(
            db,
            transaction,
            """
            SELECT i.id, v.id, v.sku, p.slug, p.name, v.display_name,
                   (SELECT pi.storage_key FROM product_images pi WHERE pi.product_id = p.id AND pi.is_primary LIMIT 1),
                   (SELECT pi.alt_text FROM product_images pi WHERE pi.product_id = p.id AND pi.is_primary LIMIT 1),
                   i.quantity, v.price, (v.is_active AND p.is_active AND c.is_active)
            FROM cart_items i
            JOIN product_variants v ON v.id = i.product_variant_id
            JOIN products p ON p.id = v.product_id
            JOIN categories c ON c.id = p.category_id
            WHERE i.cart_id = @cart
            ORDER BY v.id
            FOR SHARE OF v, p, c
            """);
        sql.Parameters.AddWithValue("cart", cartId);
        var lines = new List<LineRow>();
        await using var reader1 = await sql.ExecuteReaderAsync(cancellationToken);
        while (await reader1.ReadAsync(cancellationToken))
        {
            lines.Add(new LineRow(
                reader1.GetGuid(0),
                reader1.GetGuid(1),
                reader1.GetString(2),
                reader1.GetString(3),
                reader1.GetString(4),
                reader1.GetString(5),
                reader1.IsDBNull(6) ? null : reader1.GetString(6),
                reader1.IsDBNull(7) ? null : reader1.GetString(7),
                reader1.GetInt32(8),
                reader1.GetDecimal(9),
                reader1.GetBoolean(10)));
        }

        return lines;
    }

    /// <summary>Tek kilit sırası: varyant, sonra depo kodu (bayt sıralı). Boş bakiyeler de kilitlenir (sırayı bozmamak için), dağıtımda atlanır.</summary>
    private async Task<Dictionary<Guid, List<BalanceRow>>> LockBalancesAsync(
        IDbContextTransaction transaction,
        Guid[] variantIds,
        CancellationToken cancellationToken)
    {
        await using var sql = TransactionalSql.Create(
            db,
            transaction,
            """
            SELECT b.id, b.product_variant_id, b.warehouse_id, b.on_hand_quantity
            FROM inventory_balances b
            JOIN warehouses w ON w.id = b.warehouse_id
            WHERE b.product_variant_id = ANY(@variants) AND w.is_active
            ORDER BY b.product_variant_id, w.code COLLATE "C"
            FOR UPDATE OF b FOR SHARE OF w
            """);
        sql.Parameters.AddWithValue("variants", variantIds);
        var balances = new Dictionary<Guid, List<BalanceRow>>();
        await using var reader1 = await sql.ExecuteReaderAsync(cancellationToken);
        while (await reader1.ReadAsync(cancellationToken))
        {
            var row = new BalanceRow(reader1.GetGuid(0), reader1.GetGuid(1), reader1.GetGuid(2), reader1.GetInt32(3));
            if (!balances.TryGetValue(row.VariantId, out var list))
            {
                balances[row.VariantId] = list = [];
            }

            list.Add(row);
        }

        return balances;
    }

    private static CartLineData ToCartLine(LineRow line) => new(
        line.ItemId,
        line.VariantId,
        line.Sku,
        line.ProductSlug,
        line.ProductName,
        line.VariantName,
        line.ImageUrl,
        line.ImageAlt,
        line.Quantity,
        HintUnitPrice: line.Price,
        CurrentUnitPrice: line.Price,
        IsPurchasable: line.Purchasable,
        AvailableOnHand: int.MaxValue);

    /// <summary>Yalnız kullanıcı başına anahtar tekilliği ihlali (advisory kilit varken ulaşılamaz): son savunma hattı, güvenli yeniden deneme yanıtı.</summary>
    private static bool IsKeyRace(Exception exception) =>
        exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_orders_user_id_idempotency_key" } };
}

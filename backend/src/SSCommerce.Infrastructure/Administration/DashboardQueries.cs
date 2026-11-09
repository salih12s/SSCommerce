using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SSCommerce.Application.Administration;
using SSCommerce.Application.Inventory;
using SSCommerce.Application.Orders;
using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Ordering;
using SSCommerce.Infrastructure.Inventory;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Administration;

/// <summary>
/// Pano ve raporlar (Faz 8). SALT OKUNUR; hiçbir GET bir şey yazmaz. Sorgular TEK bağlantıda ARDIŞIKTIR (paralel değil: havuzu tüketmez); listeler en çok 10 satırdır.
/// Sorgu şekilleri EXPLAIN kanıtlıdır (<c>p8-explain.sql</c>): açık sipariş durum listeleri SQL SABİTİDİR (parametre değil) ki kısmi indeksler
/// (<c>ix_orders_open_created_at</c>) seçilsin; son hareketler/olaylar <c>(occurred_at DESC, id DESC) LIMIT 10</c> indekslerinden okunur.
/// "Satılabilir" tanımı sipariş vermedekiyle aynıdır: varyant + ürün + kategori aktif; stok yalnız AKTİF depolardan sayılır.
/// Para yalnız <c>unpaidSubtotalMinor</c> (ödenmemiş ara toplam) olarak çıkar; gelir/ciro alanı üretilmez.
/// </summary>
internal sealed class DashboardQueries(SSCommerceDbContext db, TimeProvider clock) : IDashboardQueries
{
    private const int ListLimit = 10;

    // Satılabilir varyantların toplam (aktif depolar) stoğu. Bakiye satırı olmayan varyant 0'dır.
    private const string SellableOnHandCte = """
        WITH sellable AS (
            SELECT v.id, v.sku, v.display_name, p.name AS product_name
            FROM product_variants v
            JOIN products p ON p.id = v.product_id
            JOIN categories c ON c.id = p.category_id
            WHERE v.is_active AND p.is_active AND c.is_active
        ),
        on_hand AS (
            SELECT s.id, s.sku, s.display_name, s.product_name, coalesce(sum(b.on_hand_quantity), 0)::bigint AS qty
            FROM sellable s
            LEFT JOIN (inventory_balances b JOIN warehouses w ON w.id = b.warehouse_id AND w.is_active) ON b.product_variant_id = s.id
            GROUP BY s.id, s.sku, s.display_name, s.product_name
        )
        """;

    public async Task<DashboardDto> GetDashboardAsync(DashboardQuery query, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var agedBefore = now - TimeSpan.FromHours(query.AgedHours);

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();

            // 1) Durum sayıları.
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            await using (var command = Command(connection, "SELECT status, count(*) FROM orders GROUP BY status"))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    counts[reader.GetString(0)] = reader.GetInt64(1);
                }
            }

            // 2) Açık siparişler: sayı, ödenmemiş ara toplam, yaşlı sayı, en eski zaman (kısmi indeks).
            long openCount;
            decimal unpaid;
            long agedCount;
            DateTimeOffset? oldest;
            await using (var command = Command(
                             connection,
                             """
                             SELECT count(*), coalesce(sum(item_subtotal), 0), count(*) FILTER (WHERE created_at < @aged), min(created_at)
                             FROM orders WHERE status IN ('placed', 'processing')
                             """))
            {
                command.Parameters.AddWithValue("aged", agedBefore);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                openCount = reader.GetInt64(0);
                unpaid = reader.GetDecimal(1);
                agedCount = reader.GetInt64(2);
                oldest = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3);
            }

            // 3) Katalog sayıları.
            DashboardCatalogDto catalog;
            await using (var command = Command(
                             connection,
                             """
                             SELECT (SELECT count(*) FROM products),
                                    (SELECT count(*) FROM products WHERE is_active),
                                    (SELECT count(*) FROM product_variants),
                                    (SELECT count(*) FROM product_variants WHERE is_active)
                             """))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                await reader.ReadAsync(cancellationToken);
                catalog = new DashboardCatalogDto(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
            }

            // 4) Stok: satılabilir varyantlar, aktif depolar. Az: 0 < stok <= eşik. Tükenen: stok = 0.
            long low;
            long outOfStock;
            await using (var command = Command(
                             connection,
                             SellableOnHandCte + """

                             SELECT count(*) FILTER (WHERE qty > 0 AND qty <= @threshold), count(*) FILTER (WHERE qty = 0) FROM on_hand
                             """))
            {
                command.Parameters.AddWithValue("threshold", query.LowStockThreshold);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                low = reader.GetInt64(0);
                outOfStock = reader.GetInt64(1);
            }

            // 5) En eski, eşikten yaşlı açık siparişler (en çok 10, en eski önce).
            var aged = new List<AdminOrderSummaryDto>();
            await using (var command = Command(
                             connection,
                             $"""
                             SELECT o.id, o.order_number, o.status, o.created_at, o.updated_at, o.currency, o.item_subtotal, o.shipping_status,
                                    (SELECT coalesce(sum(i.quantity), 0) FROM order_items i WHERE i.order_id = o.id)
                             FROM orders o
                             WHERE o.status IN ('placed', 'processing') AND o.created_at < @aged
                             ORDER BY o.created_at, o.id
                             LIMIT {ListLimit}
                             """))
            {
                command.Parameters.AddWithValue("aged", agedBefore);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    aged.Add(new AdminOrderSummaryDto(
                        reader.GetGuid(0),
                        OrderRules.FormatOrderNumber(reader.GetInt64(1)),
                        reader.GetString(2),
                        reader.GetFieldValue<DateTimeOffset>(3),
                        reader.GetFieldValue<DateTimeOffset>(4),
                        reader.GetString(5).Trim(),
                        (int)reader.GetInt64(8),
                        Money.ToMinor(reader.GetDecimal(6)),
                        reader.GetString(7)));
                }
            }

            // 6) Son stok hareketleri (en yeni önce) + aktör e-postaları (tek toplu sorgu).
            var movements = await db.StockMovements.AsNoTracking()
                .OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.Id)
                .Take(ListLimit)
                .ToListAsync(cancellationToken);
            var emails = await StaffDirectory.ResolveEmailsAsync(db, movements.Select(m => m.ActorUserId), cancellationToken);
            var recentMovements = movements
                .Select(m => InventoryService.ToDto(m, replayed: false) with { ActorEmail = emails.GetValueOrDefault(m.ActorUserId) })
                .ToList();

            // 7) Son sipariş olayları (durum geçmişi, en yeni önce).
            var events = new List<DashboardOrderEventDto>();
            await using (var command = Command(
                             connection,
                             $"""
                             SELECT h.order_id, o.order_number, h.from_status, h.to_status, h.actor_kind, h.occurred_at
                             FROM order_status_history h
                             JOIN orders o ON o.id = h.order_id
                             ORDER BY h.occurred_at DESC, h.id DESC
                             LIMIT {ListLimit}
                             """))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    events.Add(new DashboardOrderEventDto(
                        reader.GetGuid(0),
                        OrderRules.FormatOrderNumber(reader.GetInt64(1)),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetFieldValue<DateTimeOffset>(5)));
                }
            }

            return new DashboardDto(
                now,
                new DashboardOrdersDto(
                    counts.GetValueOrDefault(OrderStatuses.Placed),
                    counts.GetValueOrDefault(OrderStatuses.Processing),
                    counts.GetValueOrDefault(OrderStatuses.Shipped),
                    counts.GetValueOrDefault(OrderStatuses.Completed),
                    counts.GetValueOrDefault(OrderStatuses.Cancelled)),
                new DashboardOpenOrdersDto(openCount, decimal.ToInt64(unpaid * 100m), agedCount, query.AgedHours, oldest),
                catalog,
                new DashboardStockDto(query.LowStockThreshold, low, outOfStock),
                aged,
                recentMovements,
                events);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<PagedDto<LowStockRowDto>> GetLowStockAsync(LowStockQuery query, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();

            long total;
            await using (var command = Command(connection, SellableOnHandCte + "\nSELECT count(*) FROM on_hand WHERE qty <= @threshold"))
            {
                command.Parameters.AddWithValue("threshold", query.Threshold);
                total = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            }

            var items = new List<LowStockRowDto>();
            var skip = (long)(query.Page - 1) * query.PageSize;
            if (skip < total)
            {
                await using var command = Command(
                    connection,
                    SellableOnHandCte + """

                    SELECT id, sku, product_name, display_name, qty FROM on_hand WHERE qty <= @threshold
                    ORDER BY qty, sku, id
                    OFFSET @skip LIMIT @take
                    """);
                command.Parameters.AddWithValue("threshold", query.Threshold);
                command.Parameters.AddWithValue("skip", skip);
                command.Parameters.AddWithValue("take", query.PageSize);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    items.Add(new LowStockRowDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
                }
            }

            return new PagedDto<LowStockRowDto>(items, query.Page, query.PageSize, total, TotalPages(total, query.PageSize));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<PagedDto<AuditRowDto>> GetAuditAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        var rows = db.AdminAuditLog.AsNoTracking();
        if (query.TargetType is { } targetType)
        {
            rows = rows.Where(e => e.TargetType == targetType);
        }

        if (query.TargetId is { } targetId)
        {
            rows = rows.Where(e => e.TargetId == targetId);
        }

        var total = await rows.LongCountAsync(cancellationToken);
        var skip = (long)(query.Page - 1) * query.PageSize;
        var page = skip >= total
            ? new List<SSCommerce.Domain.Administration.AdminAuditEntry>()
            : await rows.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Skip((int)skip).Take(query.PageSize).ToListAsync(cancellationToken);

        var emails = await StaffDirectory.ResolveEmailsAsync(db, page.Select(e => e.ActorUserId), cancellationToken);
        var items = page.Select(e =>
        {
            using var document = JsonDocument.Parse(e.Changes);
            return new AuditRowDto(
                e.Id,
                e.ActorUserId,
                emails.GetValueOrDefault(e.ActorUserId),
                e.Action,
                e.TargetType,
                e.TargetId,
                document.RootElement.Clone(),
                e.OccurredAt);
        }).ToList();
        return new PagedDto<AuditRowDto>(items, query.Page, query.PageSize, total, TotalPages(total, query.PageSize));
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = DependencyInjection.CommandTimeoutSeconds;
        return command;
    }

    private static int TotalPages(long total, int pageSize) => (int)((total + pageSize - 1) / pageSize);
}

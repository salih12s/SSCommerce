using Microsoft.EntityFrameworkCore;
using SSCommerce.Application.Inventory;
using SSCommerce.Application.Orders;
using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Ordering;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Orders;

/// <summary>
/// Sipariş okuma sorguları (AsNoTracking). Müşteri sorguları HER ZAMAN <c>user_id</c> koşulunu taşır. Sayfalı uçlar sayım + sayfa + sayfadaki
/// kalemler = 3 sorgudur (sipariş sayısından bağımsız; N+1 yok). Kararlı sıralama: (created_at, id) azalan.
/// </summary>
internal sealed class OrderQueries(SSCommerceDbContext db, OrderReader reader) : IOrderQueries
{
    private const int PreviewItemLimit = 3;

    public Task<OrderDto?> GetAsync(Guid userId, Guid orderId, CancellationToken cancellationToken) =>
        reader.LoadForCustomerAsync(orderId, userId, replayed: false, cancellationToken);

    public async Task<PagedDto<OrderSummaryDto>> ListAsync(Guid userId, OrderListQuery query, CancellationToken cancellationToken)
    {
        var own = db.Orders.AsNoTracking().Where(o => o.UserId == userId);
        var total = await own.LongCountAsync(cancellationToken);
        var rows = await own
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(o => new { o.Id, o.OrderNumber, o.Status, o.CreatedAt, o.Currency, o.ItemSubtotal, o.ShippingStatus })
            .ToListAsync(cancellationToken);

        var items = await LoadItemsAsync(rows.Select(r => r.Id).ToList(), cancellationToken);
        var summaries = rows.Select(r =>
        {
            var lines = items.GetValueOrDefault(r.Id) ?? [];
            return new OrderSummaryDto(
                r.Id,
                OrderRules.FormatOrderNumber(r.OrderNumber),
                OrderStatuses.ToWire(r.Status),
                r.CreatedAt,
                r.Currency,
                lines.Sum(i => i.Quantity),
                Money.ToMinor(r.ItemSubtotal),
                r.ShippingStatus,
                OrderStateMachine.CanCustomerCancel(r.Status),
                lines.Take(PreviewItemLimit)
                    .Select(i => new OrderPreviewItemDto(i.ProductName, i.VariantName, i.ImageUrl, i.ImageAlt, i.Quantity))
                    .ToList());
        }).ToList();

        return Page(summaries, query.Page, query.PageSize, total);
    }

    public Task<AdminOrderDto?> GetForStaffAsync(Guid orderId, CancellationToken cancellationToken) =>
        reader.LoadForStaffAsync(orderId, replayed: false, cancellationToken, includeAllocations: true);

    public async Task<PagedDto<AdminOrderSummaryDto>> ListForStaffAsync(AdminOrderListQuery query, CancellationToken cancellationToken)
    {
        // AÇIK siparişler (placed + processing, en eski önce): durum listesi SQL SABİTİDİR (parametre değil), böylece kısmi indeks ix_orders_open_created_at seçilir
        // (docs/CONTRACT_PHASE_8.md; EXPLAIN kanıtı p8-explain.sql). Diğer durumlar mevcut ix_orders_status_created_at_id ile yeni önce sıralanır.
        var all = query.OpenOnly
            ? db.Orders.FromSqlRaw("SELECT * FROM orders WHERE status IN ('placed', 'processing')").AsNoTracking()
            : db.Orders.AsNoTracking();
        if (query.Status is { } status)
        {
            all = all.Where(o => o.Status == status);
        }

        var total = await all.LongCountAsync(cancellationToken);
        var ordered = query.OpenOnly
            ? all.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            : all.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id);
        var rows = await ordered
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(o => new { o.Id, o.OrderNumber, o.Status, o.CreatedAt, o.UpdatedAt, o.Currency, o.ItemSubtotal, o.ShippingStatus })
            .ToListAsync(cancellationToken);

        var items = await LoadItemsAsync(rows.Select(r => r.Id).ToList(), cancellationToken);
        var summaries = rows.Select(r => new AdminOrderSummaryDto(
            r.Id,
            OrderRules.FormatOrderNumber(r.OrderNumber),
            OrderStatuses.ToWire(r.Status),
            r.CreatedAt,
            r.UpdatedAt,
            r.Currency,
            (items.GetValueOrDefault(r.Id) ?? []).Sum(i => i.Quantity),
            Money.ToMinor(r.ItemSubtotal),
            r.ShippingStatus)).ToList();

        return Page(summaries, query.Page, query.PageSize, total);
    }

    /// <summary>Sayfadaki siparişlerin kalemleri TEK sorguyla; sipariş başına kararlı sıra (ürün adı, SKU).</summary>
    private async Task<Dictionary<Guid, List<OrderItem>>> LoadItemsAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0)
        {
            return [];
        }

        var items = await db.OrderItems.AsNoTracking()
            .Where(i => orderIds.Contains(i.OrderId))
            .OrderBy(i => i.ProductName).ThenBy(i => i.Sku)
            .ToListAsync(cancellationToken);
        return items.GroupBy(i => i.OrderId).ToDictionary(g => g.Key, g => g.ToList());
    }

    private static PagedDto<T> Page<T>(IReadOnlyList<T> items, int page, int pageSize, long total) =>
        new(items, page, pageSize, total, (int)((total + pageSize - 1) / pageSize));
}

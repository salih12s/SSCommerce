using Microsoft.EntityFrameworkCore;
using SSCommerce.Application.Orders;
using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Ordering;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Orders;

/// <summary>
/// Tek siparişi DTO'ya çevirir (AsNoTracking; üç sorgu: başlık, kalemler, geçmiş). Sahiplik SORGUDA: müşteri yüklemesi her zaman
/// <c>user_id</c> ile sınırlıdır, başkasının siparişi "yok" ile aynıdır. Dağıtımlar ve depolar müşteri yanıtına ASLA girmez.
/// Yazma servisleri commit'ten SONRA bunu çağırır, yani yanıt her zaman kalıcı durumu gösterir (replay'de ilk sonucun şimdiki hali).
/// </summary>
internal sealed class OrderReader(SSCommerceDbContext db)
{
    public async Task<OrderDto?> LoadForCustomerAsync(Guid orderId, Guid ownerUserId, bool replayed, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == ownerUserId, cancellationToken);
        if (order is null)
        {
            return null;
        }

        var items = await LoadItemsAsync(orderId, cancellationToken);
        var history = await LoadHistoryAsync(orderId, cancellationToken);
        return new OrderDto(
            order.Id,
            order.DisplayNumber,
            OrderStatuses.ToWire(order.Status),
            order.CreatedAt,
            order.UpdatedAt,
            order.Currency,
            items.Sum(i => i.Quantity),
            Money.ToMinor(order.ItemSubtotal),
            order.ShippingAmount is { } shipping ? Money.ToMinor(shipping) : null,
            order.ShippingStatus,
            order.GrandTotal is { } grand ? Money.ToMinor(grand) : null,
            items.Select(ToDto).ToList(),
            new OrderAddressDto(
                order.DeliveryLabel,
                order.DeliveryRecipientName,
                order.DeliveryPhone,
                order.DeliveryProvince,
                order.DeliveryDistrict,
                order.DeliveryStreetAddress,
                order.DeliveryPostalCode),
            history.Select(h => new OrderStatusHistoryDto(
                h.FromStatus is { } from ? OrderStatuses.ToWire(from) : null,
                OrderStatuses.ToWire(h.ToStatus),
                h.OccurredAt)).ToList(),
            order.CanCustomerCancel,
            replayed);
    }

    /// <summary>Personel görünümü: teslimat adresi ve kullanıcı bilgisi YOK.</summary>
    public async Task<AdminOrderDto?> LoadForStaffAsync(
        Guid orderId,
        bool replayed,
        CancellationToken cancellationToken,
        bool includeAllocations = false)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null)
        {
            return null;
        }

        var items = await LoadItemsAsync(orderId, cancellationToken);
        var history = await LoadHistoryAsync(orderId, cancellationToken);
        var allocations = includeAllocations ? await LoadAllocationsAsync(orderId, cancellationToken) : null;
        return new AdminOrderDto(
            order.Id,
            order.DisplayNumber,
            OrderStatuses.ToWire(order.Status),
            order.CreatedAt,
            order.UpdatedAt,
            order.Currency,
            items.Sum(i => i.Quantity),
            Money.ToMinor(order.ItemSubtotal),
            order.ShippingAmount is { } shipping ? Money.ToMinor(shipping) : null,
            order.ShippingStatus,
            order.GrandTotal is { } grand ? Money.ToMinor(grand) : null,
            items.Select(ToDto).ToList(),
            history.Select(h => new AdminOrderHistoryDto(
                h.FromStatus is { } from ? OrderStatuses.ToWire(from) : null,
                OrderStatuses.ToWire(h.ToStatus),
                h.OccurredAt,
                OrderActorKinds.ToWire(h.ActorKind),
                h.Reason)).ToList(),
            replayed,
            allocations);
    }

    /// <summary>Stok dağıtımları: SKU (kalem anlık görüntüsü) + depo kodu + adet. Tek sorgu, kararlı sıra (SKU, depo kodu bayt sıralı). Kişisel veri yok.</summary>
    private async Task<List<AdminOrderAllocationDto>> LoadAllocationsAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var rows = await (
            from allocation in db.OrderInventoryAllocations.AsNoTracking()
            join item in db.OrderItems on allocation.OrderItemId equals item.Id
            join warehouse in db.Warehouses on allocation.WarehouseId equals warehouse.Id
            where item.OrderId == orderId
            select new { item.Sku, WarehouseCode = warehouse.Code, allocation.Quantity })
            .ToListAsync(cancellationToken);
        return rows
            .OrderBy(r => r.Sku, StringComparer.Ordinal).ThenBy(r => r.WarehouseCode, StringComparer.Ordinal)
            .Select(r => new AdminOrderAllocationDto(r.Sku, r.WarehouseCode, r.Quantity))
            .ToList();
    }

    private Task<List<OrderItem>> LoadItemsAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.OrderItems.AsNoTracking()
            .Where(i => i.OrderId == orderId)
            .OrderBy(i => i.ProductName).ThenBy(i => i.Sku)
            .ToListAsync(cancellationToken);

    private Task<List<OrderStatusHistoryEntry>> LoadHistoryAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.OrderStatusHistory.AsNoTracking()
            .Where(h => h.OrderId == orderId)
            .OrderBy(h => h.OccurredAt).ThenBy(h => h.Id)
            .ToListAsync(cancellationToken);

    private static OrderItemDto ToDto(OrderItem item) => new(
        item.Id,
        item.ProductVariantId,
        item.Sku,
        item.ProductName,
        item.VariantName,
        item.ProductSlug,
        item.ImageUrl,
        item.ImageAlt,
        Money.ToMinor(item.UnitPrice),
        item.Quantity,
        Money.ToMinor(item.LineSubtotal));
}

namespace SSCommerce.Domain.Ordering;

/// <summary>
/// Bir sipariş kaleminin hangi depodan kaç adet düşüldüğü (D-062). Bir kalem birden çok depoya bölünebilir; her parça ayrı satırdır
/// ve <c>(order_item_id, warehouse_id)</c> benzersizdir. İptalde iade bu satırlara göre özgün depoya yapılır. DEĞİŞMEZ (trigger, D-064):
/// <see cref="PlacementMovementId"/> düşümü kanıtlayan <c>order_placement</c> stok hareketine bağlar. Müşteri yanıtlarında ve loglarda yer almaz.
/// </summary>
public sealed class OrderInventoryAllocation
{
    private OrderInventoryAllocation()
    {
    }

    public OrderInventoryAllocation(Guid orderItemId, Guid warehouseId, Guid productVariantId, int quantity, Guid placementMovementId)
    {
        if (orderItemId == Guid.Empty || warehouseId == Guid.Empty || productVariantId == Guid.Empty || placementMovementId == Guid.Empty)
        {
            throw new OrderRuleException("Kalem, depo, varyant ve stok hareketi kimliği zorunlu.");
        }

        // Bir dağıtım parçası kalemin kendisinden büyük olamaz; kalem adedi üst sınırı burada da geçerlidir.
        if (quantity is < 1 or > OrderRules.MaxLineQuantity)
        {
            throw new OrderRuleException($"Dağıtım adedi 1 ile {OrderRules.MaxLineQuantity} arasında olmalı.");
        }

        Id = Guid.CreateVersion7();
        OrderItemId = orderItemId;
        WarehouseId = warehouseId;
        ProductVariantId = productVariantId;
        Quantity = quantity;
        PlacementMovementId = placementMovementId;
    }

    public Guid Id { get; private set; }

    public Guid OrderItemId { get; private set; }

    public Guid WarehouseId { get; private set; }

    public Guid ProductVariantId { get; private set; }

    public int Quantity { get; private set; }

    /// <summary>Bu parçanın stoğunu düşen <c>order_placement</c> hareketi (FK <c>stock_movements</c>).</summary>
    public Guid PlacementMovementId { get; private set; }
}

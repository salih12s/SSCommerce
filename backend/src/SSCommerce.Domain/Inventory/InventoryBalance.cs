using SSCommerce.Domain.Catalog;

namespace SSCommerce.Domain.Inventory;

/// <summary>
/// Bir deponun bir varyant için eldeki miktarı. Değişiklikler uygulama nesnesi üzerinden DEĞİL, tek koşullu SQL ile yapılır
/// (D-046: <c>UPDATE … WHERE on_hand_quantity + delta &gt;= 0 RETURNING</c>, artışta <c>INSERT … ON CONFLICT DO UPDATE</c>);
/// bu yüzden mutasyon metodu yoktur. <see cref="Version"/> her başarılı yazmada SQL ile artar; iyimser kilit DEĞİL, tanı alanıdır.
/// </summary>
public sealed class InventoryBalance : AuditedEntity
{
    private InventoryBalance()
    {
    }

    public InventoryBalance(Guid warehouseId, Guid productVariantId, int onHandQuantity)
    {
        if (warehouseId == Guid.Empty || productVariantId == Guid.Empty)
        {
            throw new InventoryRuleException("Depo ve varyant kimliği zorunlu.");
        }

        WarehouseId = warehouseId;
        ProductVariantId = productVariantId;
        OnHandQuantity = InventoryRules.RequireOnHand(onHandQuantity);
        Version = 1;
    }

    public Guid WarehouseId { get; private set; }

    public Guid ProductVariantId { get; private set; }

    public int OnHandQuantity { get; private set; }

    public long Version { get; private set; }
}

using SSCommerce.Domain.Catalog;

namespace SSCommerce.Domain.Inventory;

/// <summary>Stok tutulan depo. Kod değişmez (stok hareketleri ve raporlar onu anar); ad ve aktiflik değişebilir.</summary>
public sealed class Warehouse : AuditedEntity
{
    private Warehouse()
    {
        Code = string.Empty;
        Name = string.Empty;
    }

    public Warehouse(string code, string name)
    {
        Code = InventoryRules.RequireWarehouseCode(code);
        Name = InventoryRules.RequireWarehouseName(name);
        IsActive = true;
    }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    public void Rename(string name) => Name = InventoryRules.RequireWarehouseName(name);

    public void SetActive(bool isActive) => IsActive = isActive;
}

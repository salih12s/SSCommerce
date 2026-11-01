namespace SSCommerce.Infrastructure.Identity;

/// <summary>
/// Rol adları ve sabit kimlikleri (D-032). Roller <c>AddIdentity</c> migration'ında <c>HasData</c> ile eklenir; kimlikler
/// sabit olduğu için migration deterministiktir ve ortamlar arası aynıdır. Kayıt her zaman <see cref="Customer"/> verir;
/// <see cref="Warehouse"/>/<see cref="Admin"/> atamasının HTTP yolu yoktur. Bu değerleri DEĞİŞTİRME: yayımlanmış migration'la eşleşmelidir.
/// </summary>
public static class AppRoles
{
    public const string Customer = "Customer";
    public const string Warehouse = "Warehouse";
    public const string Admin = "Admin";

    public static readonly Guid CustomerId = new("5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a01");
    public static readonly Guid WarehouseId = new("5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a02");
    public static readonly Guid AdminId = new("5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a03");

    // Migration'ın deterministik kalması için sabit eşzamanlılık damgaları (rastgele üretilirse her model derlemesinde fark çıkar).
    internal const string CustomerConcurrencyStamp = "6c1d2e7f-8b45-4a1f-8d2b-3e9a7f4c1b01";
    internal const string WarehouseConcurrencyStamp = "6c1d2e7f-8b45-4a1f-8d2b-3e9a7f4c1b02";
    internal const string AdminConcurrencyStamp = "6c1d2e7f-8b45-4a1f-8d2b-3e9a7f4c1b03";

    public static IReadOnlyList<string> All { get; } = [Customer, Warehouse, Admin];
}

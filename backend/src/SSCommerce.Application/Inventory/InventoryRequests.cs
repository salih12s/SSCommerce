namespace SSCommerce.Application.Inventory;

// HAM istek gövdeleri. Alanlar nullable: eksik alan JSON bağlamada değil doğrulayıcıda alan bazlı hata olur.
// `userId`, `actor`, `onHand` gibi bilinmeyen alanlar kayıtta yoktur, yani yok sayılır (mass assignment yok).
// Miktarlar `long?`: aşırı büyük bir sayı bağlama hatası (genel 400) yerine alan bazlı sınır hatası versin.

public sealed record CreateWarehouseRequest(string? Code, string? Name);

public sealed record UpdateWarehouseRequest(string? Name, bool? IsActive);

public sealed record ReceiveStockRequest(Guid? WarehouseId, Guid? VariantId, long? Quantity, string? Reason, string? Reference);

public sealed record AdjustStockRequest(Guid? WarehouseId, Guid? VariantId, long? QuantityDelta, string? Type, string? Reason, string? Reference);

/// <summary>Ham sayfalı sorgu dizeleri (tip hatası alan bazlı 400 olsun diye string; bkz. ProductListRequest).</summary>
public sealed record BalanceListRequest(string? WarehouseId, string? Sku, string? Page, string? PageSize, string? VariantId = null);

public sealed record MovementListRequest(string? WarehouseId, string? VariantId, string? Page, string? PageSize);

// Doğrulanmış, normalize edilmiş biçimler (yalnız InventoryRequestValidator üretir).

public sealed record NewWarehouse(string Code, string Name);

public sealed record WarehouseChange(string? Name, bool? IsActive);

public sealed record BalanceListQuery(Guid? WarehouseId, string? Sku, int Page, int PageSize, Guid? VariantId = null);

public sealed record MovementListQuery(Guid? WarehouseId, Guid? VariantId, int Page, int PageSize);

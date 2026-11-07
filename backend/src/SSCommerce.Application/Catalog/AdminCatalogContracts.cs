using SSCommerce.Application.Administration;
using SSCommerce.Application.Inventory;

namespace SSCommerce.Application.Catalog;

// Admin katalog sözleşmesi (docs/CONTRACT_PHASE_8.md "Catalog", JSON şekilleri apps/web/src/lib/admin/types.ts ile bağlayıcı).
// Para kuruştur (long, "...Minor"); sürüm (version) her yazmada tam 1 artar ve PATCH/PUT gövdesinde ZORUNLUDUR.

// ---- Yanıtlar ----

public sealed record AdminCategoryDto(
    Guid Id,
    string Name,
    string Slug,
    Guid? ParentId,
    int SortOrder,
    bool IsActive,
    int Version,
    int ProductCount,
    DateTimeOffset UpdatedAt);

public sealed record AdminProductSummaryDto(
    Guid Id,
    string Name,
    string Slug,
    Guid CategoryId,
    string CategoryName,
    bool IsActive,
    bool IsDemo,
    int VariantCount,
    int ActiveVariantCount,
    long? MinPriceMinor,
    string? PrimaryImageUrl,
    int Version,
    DateTimeOffset UpdatedAt);

public sealed record AdminVariantDto(
    Guid Id,
    string Sku,
    string DisplayName,
    IReadOnlyDictionary<string, string> Options,
    long PriceMinor,
    bool IsActive,
    int Version);

/// <summary><c>Url</c> = <c>StorageKey</c> (web'in public yolu). Sıra dizi sırasıdır.</summary>
public sealed record AdminImageDto(string StorageKey, string AltText, bool IsPrimary, int SortOrder, string Url);

public sealed record AdminProductDetailDto(
    Guid Id,
    string Name,
    string Slug,
    string? Brand,
    string? Description,
    IReadOnlyDictionary<string, string>? Specifications,
    Guid CategoryId,
    string CategoryName,
    bool IsActive,
    bool IsDemo,
    int Version,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AdminVariantDto> Variants,
    IReadOnlyList<AdminImageDto> Images);

public sealed record ImageAssetDto(string StorageKey, string CreditKey, string Author);

/// <summary>Stok ekranları için SKU/ürün arama satırı. <c>OnHandTotal</c> TÜM depoların (aktif ve pasif) toplamıdır.</summary>
public sealed record VariantHitDto(
    Guid Id,
    string Sku,
    string ProductName,
    string DisplayName,
    bool IsActive,
    bool ProductIsActive,
    long OnHandTotal);

// ---- Doğrulanmış komutlar (yalnız AdminCatalogValidator üretir) ----

public sealed record NewCategory(string Name, string Slug, Guid? ParentId, int SortOrder, bool IsActive);

public sealed record CategoryChange(
    int Version,
    Patch<string?> Name,
    Patch<Guid?> ParentId,
    Patch<long> SortOrder,
    Patch<bool> IsActive);

public sealed record NewProduct(
    Guid CategoryId,
    string Name,
    string Slug,
    string? Brand,
    string? Description,
    IReadOnlyDictionary<string, string>? Specifications,
    bool IsActive);

public sealed record ProductChange(
    int Version,
    Patch<Guid?> CategoryId,
    Patch<string?> Name,
    Patch<string?> Brand,
    Patch<string?> Description,
    Patch<Dictionary<string, string>?> Specifications,
    Patch<bool> IsActive);

public sealed record ImageInput(string StorageKey, string AltText, bool IsPrimary);

public sealed record ImageReplacement(int Version, IReadOnlyList<ImageInput> Images);

public sealed record NewVariant(string Sku, string DisplayName, long PriceMinor, IReadOnlyDictionary<string, string>? Options, bool IsActive);

public sealed record VariantChange(int Version, Patch<string?> DisplayName, Patch<long> PriceMinor, Patch<bool> IsActive);

// ---- Liste sorguları ----

public sealed record AdminProductListQuery(string? Search, Guid? CategoryId, string Status, int Page, int PageSize);

public sealed record AdminVariantSearchQuery(string? Search, int Page, int PageSize);

// ---- Yazma sonuçları ----

public enum CatalogWriteStatus
{
    Ok,
    CategoryNotFound,
    ProductNotFound,
    VariantNotFound,

    /// <summary>Gönderilen sürüm KİLİTLİ satırdakiyle aynı değil (409 version_conflict + currentVersion).</summary>
    VersionConflict,

    CategorySlugTaken,
    ProductSlugTaken,
    SkuTaken,
    VariantOptionsTaken,
    CategoryCycle,
    CategoryTooDeep,
    ImageNotApproved,

    /// <summary>Kilit zaman aşımı ya da deadlock: hiçbir şey yazılmadı (503 busy).</summary>
    Busy,
}

public sealed record CatalogWriteResult<T>(CatalogWriteStatus Status, T? Value = default, int? CurrentVersion = null)
    where T : class
{
    public bool IsOk => Status == CatalogWriteStatus.Ok;
}

// ---- Servis sözleşmeleri (uygulama Infrastructure'da) ----

/// <summary>
/// Admin katalog YAZMA sözleşmesi. Her yazma tek READ COMMITTED transaction'dır: kilit zaman aşımı → (kategori yazmalarında advisory
/// <c>catalog:category-tree</c>) → hedef satır <c>FOR UPDATE</c> → sürüm KİLİTLİ satırdan karşılaştırılır → değişiklik + <c>version + 1</c> →
/// denetim satırı (aynı transaction) → commit. Hiçbir şeyi değiştirmeyen yazma sürümü artırmaz, denetim yazmaz.
/// </summary>
public interface IAdminCatalogService
{
    Task<CatalogWriteResult<AdminCategoryDto>> CreateCategoryAsync(Guid actorUserId, NewCategory category, CancellationToken cancellationToken);

    Task<CatalogWriteResult<AdminCategoryDto>> UpdateCategoryAsync(Guid actorUserId, Guid id, CategoryChange change, CancellationToken cancellationToken);

    Task<CatalogWriteResult<AdminProductDetailDto>> CreateProductAsync(Guid actorUserId, NewProduct product, CancellationToken cancellationToken);

    Task<CatalogWriteResult<AdminProductDetailDto>> UpdateProductAsync(Guid actorUserId, Guid id, ProductChange change, CancellationToken cancellationToken);

    Task<CatalogWriteResult<AdminProductDetailDto>> ReplaceImagesAsync(Guid actorUserId, Guid id, ImageReplacement replacement, CancellationToken cancellationToken);

    Task<CatalogWriteResult<AdminVariantDto>> CreateVariantAsync(Guid actorUserId, Guid productId, NewVariant variant, CancellationToken cancellationToken);

    Task<CatalogWriteResult<AdminVariantDto>> UpdateVariantAsync(Guid actorUserId, Guid id, VariantChange change, CancellationToken cancellationToken);
}

/// <summary>Admin katalog OKUMA sözleşmesi. AsNoTracking + projection; sayfalı uçlar sayım + sayfa (+ sayfadaki satırlar için tek ek sorgu).</summary>
public interface IAdminCatalogQueries
{
    Task<IReadOnlyList<AdminCategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken);

    Task<PagedDto<AdminProductSummaryDto>> GetProductsAsync(AdminProductListQuery query, CancellationToken cancellationToken);

    Task<AdminProductDetailDto?> GetProductAsync(Guid id, CancellationToken cancellationToken);

    Task<PagedDto<VariantHitDto>> SearchVariantsAsync(AdminVariantSearchQuery query, CancellationToken cancellationToken);
}

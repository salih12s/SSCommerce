namespace SSCommerce.Application.Catalog;

// Public katalog yanıt sözleşmesi (D-028). Stok bilgisi (Faz 5, D-048) yalnız `inStock` + cap'li `availableQuantity`dir:
// yalnız AKTİF depolar toplanır, `availableQuantity` en çok InventoryRules.PublicAvailabilityCap (20) olur; depo kırılımı/kodu/adı
// ve cap üstü gerçek miktar ASLA yanıtta yoktur. Bilinçli olarak YOK: is_active, created_at/updated_at, credit_key, option_signature.
// Para her yerde kuruş (long, "...Minor"), para birimi MVP'de yalnız "TRY".

/// <summary><c>GET /api/v1/categories</c> öğesi. <paramref name="ParentSlug"/> yalnız üst kategori de aktifse dolu olur.</summary>
public sealed record CategoryDto(Guid Id, string Slug, string Name, string? ParentSlug, int SortOrder);

public sealed record CategoryRefDto(string Slug, string Name);

/// <summary>Görsel yolu (<c>/images/...</c>) web uygulamasının public yoludur.</summary>
public sealed record PrimaryImageDto(string Url, string AltText);

public sealed record ProductListItemDto(
    Guid Id,
    string Slug,
    string Name,
    CategoryRefDto Category,
    PrimaryImageDto? PrimaryImage,
    long StartingPriceMinor,
    string Currency,
    bool IsDemo,
    bool InStock);

public sealed record ProductPageDto(
    IReadOnlyList<ProductListItemDto> Items,
    int Page,
    int PageSize,
    long TotalCount,
    int TotalPages);

/// <param name="Id">Sepet için varyant kimliği (D-048).</param>
/// <param name="InStock">Aktif depolardaki toplam stok &gt; 0.</param>
/// <param name="AvailableQuantity">Aktif depolardaki toplam stok, en çok 20 (gerçek miktar açıklanmaz).</param>
public sealed record ProductVariantDto(
    Guid Id,
    string Sku,
    string DisplayName,
    IReadOnlyDictionary<string, string> Options,
    long PriceMinor,
    string Currency,
    bool InStock,
    int AvailableQuantity);

public sealed record ProductImageDto(string Url, string AltText, bool IsPrimary, int SortOrder);

public sealed record ProductDetailDto(
    Guid Id,
    string Slug,
    string Name,
    string? Brand,
    string? Description,
    IReadOnlyDictionary<string, string>? Specifications,
    CategoryRefDto Category,
    bool IsDemo,
    string Currency,
    long StartingPriceMinor,
    bool InStock,
    IReadOnlyList<ProductVariantDto> Variants,
    IReadOnlyList<ProductImageDto> Images);

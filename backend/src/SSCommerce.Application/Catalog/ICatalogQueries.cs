namespace SSCommerce.Application.Catalog;

/// <summary>
/// Public katalog OKUMA sözleşmesi (D-028). Uygulaması Infrastructure'dadır (EF Core, AsNoTracking + projection).
/// Yazma metodu yoktur: katalog yazımı Faz 8'e (admin) aittir.
/// </summary>
public interface ICatalogQueries
{
    /// <summary>Yalnız aktif kategoriler; sıralama sort_order, name, id.</summary>
    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Doğrulanmış sorguyla ürün listesi. <c>null</c> = istenen kategori yok ya da pasif (çağıran 404 <c>category_not_found</c> döner).
    /// Sayfa toplamın ötesindeyse null DEĞİL, boş <c>Items</c> ve doğru <c>TotalCount</c> döner.
    /// </summary>
    Task<ProductPageDto?> GetProductsAsync(ProductListQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Slug ile ürün detayı. <c>null</c> = ürün yok, pasif, kategorisi pasif ya da aktif varyantı yok
    /// (hepsi çağıran için aynı 404 <c>product_not_found</c>; pasif olduğu ifşa edilmez).
    /// </summary>
    Task<ProductDetailDto?> GetProductBySlugAsync(string slug, CancellationToken cancellationToken);
}

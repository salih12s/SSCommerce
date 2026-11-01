using Microsoft.EntityFrameworkCore;
using SSCommerce.Application.Catalog;
using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Inventory;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Catalog;

/// <summary>
/// <see cref="ICatalogQueries"/> EF Core uygulaması. Kurallar: hepsi <c>AsNoTracking</c> + projection (varlık yüklenmez),
/// liste = 1 sayım + 1 sayfa sorgusu, detay = ürün + varyantlar + görseller (split query); N+1 yok.
/// Stok (D-048): `available(varyant)` = AKTİF depolardaki toplam bakiye; bakiye satırı yoksa 0. Hesap aynı sorgunun içinde ilişkili alt
/// sorgudur (ek gidiş-dönüş yok); `availableQuantity` en çok <see cref="InventoryRules.PublicAvailabilityCap"/> olur, gerçek miktar çıkmaz.
/// "Görünür ürün" tanımı tek yerde: aktif ürün + aktif kategori + en az bir AKTİF varyant. Fiyat yalnız aktif varyantlardan hesaplanır.
/// </summary>
internal sealed class CatalogQueries(SSCommerceDbContext db) : ICatalogQueries
{
    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken)
    {
        return await db.Categories
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new CategoryDto(
                c.Id,
                c.Slug,
                c.Name,
                // Üst kategori pasifse slug'ı listede olmayacağı için sızdırılmaz.
                db.Categories.Where(p => p.Id == c.ParentId && p.IsActive).Select(p => p.Slug).FirstOrDefault(),
                c.SortOrder))
            .ToListAsync(cancellationToken);
    }

    public async Task<ProductPageDto?> GetProductsAsync(ProductListQuery query, CancellationToken cancellationToken)
    {
        // Aday satırlar: yalnız listeye gereken sütunlar (açıklama/özellik jsonb'si çekilmez).
        var rows = db.Products
            .AsNoTracking()
            .Where(p => p.IsActive && p.Category!.IsActive)
            .Select(p => new ListRow
            {
                Id = p.Id,
                Slug = p.Slug,
                Name = p.Name,
                Brand = p.Brand,
                CreatedAt = p.CreatedAt,
                IsDemo = p.IsDemo,
                CategorySlug = p.Category!.Slug,
                CategoryName = p.Category.Name,
                CategoryId = p.CategoryId,
                StartingPrice = p.Variants.Where(v => v.IsActive).Min(v => (decimal?)v.Price),
            })
            .Where(r => r.StartingPrice != null);

        if (query.CategorySlug is not null)
        {
            // Bilinmeyen/pasif/bozuk slug => null (404 category_not_found).
            var categoryId = await db.Categories
                .AsNoTracking()
                .Where(c => c.Slug == query.CategorySlug && c.IsActive)
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (categoryId is null)
            {
                return null;
            }

            // Seçilen kategori + DOĞRUDAN alt kategorileri (torunlar dahil değil). Alt sorgu olarak IN (...) olur.
            var id = categoryId.Value;
            var scope = db.Categories.Where(c => c.IsActive && (c.Id == id || c.ParentId == id)).Select(c => c.Id);
            rows = rows.Where(r => scope.Contains(r.CategoryId));
        }

        if (query.Search is not null)
        {
            // Parametrik ILIKE; %, _ ve \ kaçışlı (LikePattern). Büyük/küçük harf duyarsızlığı veritabanı yerel ayarına bağlıdır.
            var pattern = LikePattern.Contains(query.Search);
            rows = rows.Where(r =>
                EF.Functions.ILike(r.Name, pattern, LikePattern.EscapeCharacter) ||
                (r.Brand != null && EF.Functions.ILike(r.Brand, pattern, LikePattern.EscapeCharacter)));
        }

        // Fiyat filtresi başlangıç fiyatı (aktif varyantların en düşüğü) üzerinden, uçlar dahil.
        if (query.MinPriceMinor is { } minMinor)
        {
            var min = Money.FromMinor(minMinor);
            rows = rows.Where(r => r.StartingPrice >= min);
        }

        if (query.MaxPriceMinor is { } maxMinor)
        {
            var max = Money.FromMinor(maxMinor);
            rows = rows.Where(r => r.StartingPrice <= max);
        }

        var totalCount = await rows.LongCountAsync(cancellationToken);
        var totalPages = (int)((totalCount + query.PageSize - 1) / query.PageSize);
        var skip = (query.Page - 1) * query.PageSize;
        if (skip >= totalCount)
        {
            // Toplam sayfanın ötesi: hata değil, boş sayfa.
            return new ProductPageDto([], query.Page, query.PageSize, totalCount, totalPages);
        }

        // Her sıralamanın son anahtarı id: eşit değerlerde bile sayfalar arasında kayma/tekrar olmaz.
        var ordered = query.Sort switch
        {
            ProductSort.PriceAsc => rows.OrderBy(r => r.StartingPrice).ThenBy(r => r.Id),
            ProductSort.PriceDesc => rows.OrderByDescending(r => r.StartingPrice).ThenBy(r => r.Id),
            ProductSort.NameAsc => rows.OrderBy(r => r.Name).ThenBy(r => r.Id),
            _ => rows.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id),
        };

        // Ana görsel yalnız sayfadaki satırlar için, aynı sorgu içinde ilişkili alt sorgu olarak gelir (N+1 yok).
        var page = await ordered
            .Skip(skip)
            .Take(query.PageSize)
            .Select(r => new
            {
                Row = r,
                ImageUrl = db.ProductImages.Where(i => i.ProductId == r.Id && i.IsPrimary).Select(i => i.StorageKey).FirstOrDefault(),
                ImageAlt = db.ProductImages.Where(i => i.ProductId == r.Id && i.IsPrimary).Select(i => i.AltText).FirstOrDefault(),
                // Bakiye >= 0 olduğundan "toplam > 0" = "aktif depoda pozitif bakiyesi olan satır var". Yalnız sayfadaki ürünler için.
                InStock = db.ProductVariants
                    .Any(v => v.ProductId == r.Id && v.IsActive &&
                        StockAvailability.ActiveBalances(db).Any(b => b.ProductVariantId == v.Id && b.OnHandQuantity > 0)),
            })
            .ToListAsync(cancellationToken);

        var items = page
            .Select(x => new ProductListItemDto(
                x.Row.Id,
                x.Row.Slug,
                x.Row.Name,
                new CategoryRefDto(x.Row.CategorySlug, x.Row.CategoryName),
                x.ImageUrl is null ? null : new PrimaryImageDto(x.ImageUrl, x.ImageAlt!),
                Money.ToMinor(x.Row.StartingPrice!.Value),
                Money.Currency,
                x.Row.IsDemo,
                x.InStock))
            .ToList();

        return new ProductPageDto(items, query.Page, query.PageSize, totalCount, totalPages);
    }

    public async Task<ProductDetailDto?> GetProductBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        var product = await db.Products
            .AsNoTracking()
            .Where(p => p.Slug == slug && p.IsActive && p.Category!.IsActive)
            .Select(p => new
            {
                p.Id,
                p.Slug,
                p.Name,
                p.Brand,
                p.Description,
                p.Specifications,
                p.IsDemo,
                CategorySlug = p.Category!.Slug,
                CategoryName = p.Category.Name,
                Variants = p.Variants
                    .Where(v => v.IsActive)
                    .Select(v => new
                    {
                        v.Id,
                        v.Sku,
                        v.DisplayName,
                        v.Options,
                        v.Price,
                        v.Currency,
                        Available = StockAvailability.ActiveBalances(db)
                            .Where(b => b.ProductVariantId == v.Id)
                            .Sum(b => (long?)b.OnHandQuantity) ?? 0L,
                    })
                    .ToList(),
                Images = p.Images
                    .OrderBy(i => i.SortOrder).ThenBy(i => i.Id)
                    .Select(i => new { i.StorageKey, i.AltText, i.IsPrimary, i.SortOrder })
                    .ToList(),
            })
            .AsSplitQuery() // iki koleksiyonun kartezyen çarpımını önler
            .FirstOrDefaultAsync(cancellationToken);

        if (product is null || product.Variants.Count == 0)
        {
            return null;
        }

        // Varyant sırası (fiyat, sku) bellekte ve ORDİNAL: veritabanı collation'ı tireleri yok sayabildiği için sku sırası oraya bırakılmaz.
        var variants = product.Variants
            .OrderBy(v => v.Price).ThenBy(v => v.Sku, StringComparer.Ordinal)
            .Select(v => new ProductVariantDto(
                v.Id,
                v.Sku,
                v.DisplayName,
                v.Options,
                Money.ToMinor(v.Price),
                v.Currency,
                v.Available > 0,
                StockAvailability.Cap(v.Available)))
            .ToList();

        return new ProductDetailDto(
            product.Id,
            product.Slug,
            product.Name,
            product.Brand,
            product.Description,
            product.Specifications,
            new CategoryRefDto(product.CategorySlug, product.CategoryName),
            product.IsDemo,
            Money.Currency,
            variants[0].PriceMinor,
            variants.Any(v => v.InStock),
            variants,
            product.Images.Select(i => new ProductImageDto(i.StorageKey, i.AltText, i.IsPrimary, i.SortOrder)).ToList());
    }

    private sealed class ListRow
    {
        public Guid Id { get; init; }

        public string Slug { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string? Brand { get; init; }

        public DateTimeOffset CreatedAt { get; init; }

        public bool IsDemo { get; init; }

        public string CategorySlug { get; init; } = string.Empty;

        public string CategoryName { get; init; } = string.Empty;

        public Guid CategoryId { get; init; }

        public decimal? StartingPrice { get; init; }
    }
}

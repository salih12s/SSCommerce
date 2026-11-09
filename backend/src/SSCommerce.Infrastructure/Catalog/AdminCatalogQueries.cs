using Microsoft.EntityFrameworkCore;
using SSCommerce.Application.Catalog;
using SSCommerce.Application.Inventory;
using SSCommerce.Domain.Catalog;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Catalog;

/// <summary>
/// Admin katalog okuma sorguları (<see cref="IAdminCatalogQueries"/>): AsNoTracking + projection. Pasif kategori/ürün/varyant DAHİL (public katalog
/// yalnız aktifleri gösterir ve değişmedi). Sayfalı uçlar 1 sayım + 1 sayfa sorgusudur; ilişkili sayımlar aynı sorgunun içinde alt sorgudur (N+1 yok).
/// Sıralamalar deterministiktir (son anahtar id).
/// </summary>
internal sealed class AdminCatalogQueries(SSCommerceDbContext db) : IAdminCatalogQueries
{
    public const int CategoryCap = 500;

    public async Task<IReadOnlyList<AdminCategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Categories
            .AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ThenBy(c => c.Id)
            .Take(CategoryCap)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Slug,
                c.ParentId,
                c.SortOrder,
                c.IsActive,
                c.Version,
                ProductCount = db.Products.Count(p => p.CategoryId == c.Id),
                c.UpdatedAt,
            })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new AdminCategoryDto(r.Id, r.Name, r.Slug, r.ParentId, r.SortOrder, r.IsActive, r.Version, r.ProductCount, r.UpdatedAt)).ToList();
    }

    public async Task<PagedDto<AdminProductSummaryDto>> GetProductsAsync(AdminProductListQuery query, CancellationToken cancellationToken)
    {
        var products = db.Products.AsNoTracking();
        if (query.Status == "active")
        {
            products = products.Where(p => p.IsActive);
        }
        else if (query.Status == "inactive")
        {
            products = products.Where(p => !p.IsActive);
        }

        if (query.CategoryId is { } categoryId)
        {
            products = products.Where(p => p.CategoryId == categoryId);
        }

        if (query.Search is not null)
        {
            // Parametrik ILIKE; %, _ ve \ kaçışlı (LikePattern). Ad, slug, marka ya da herhangi bir varyantın SKU'su.
            var pattern = LikePattern.Contains(query.Search);
            products = products.Where(p =>
                EF.Functions.ILike(p.Name, pattern, LikePattern.EscapeCharacter) ||
                EF.Functions.ILike(p.Slug, pattern, LikePattern.EscapeCharacter) ||
                (p.Brand != null && EF.Functions.ILike(p.Brand, pattern, LikePattern.EscapeCharacter)) ||
                p.Variants.Any(v => EF.Functions.ILike(v.Sku, pattern, LikePattern.EscapeCharacter)));
        }

        var totalCount = await products.LongCountAsync(cancellationToken);
        var totalPages = (int)((totalCount + query.PageSize - 1) / query.PageSize);
        var skip = (long)(query.Page - 1) * query.PageSize;
        if (skip >= totalCount)
        {
            return new PagedDto<AdminProductSummaryDto>([], query.Page, query.PageSize, totalCount, totalPages);
        }

        var rows = await products
            .OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.Id)
            .Skip((int)skip).Take(query.PageSize)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Slug,
                p.CategoryId,
                CategoryName = p.Category!.Name,
                p.IsActive,
                p.IsDemo,
                VariantCount = p.Variants.Count(),
                ActiveVariantCount = p.Variants.Count(v => v.IsActive),
                MinPrice = p.Variants.Min(v => (decimal?)v.Price),
                PrimaryImage = p.Images.Where(i => i.IsPrimary).Select(i => i.StorageKey).FirstOrDefault(),
                p.Version,
                p.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        var items = rows.Select(r => new AdminProductSummaryDto(
            r.Id,
            r.Name,
            r.Slug,
            r.CategoryId,
            r.CategoryName,
            r.IsActive,
            r.IsDemo,
            r.VariantCount,
            r.ActiveVariantCount,
            r.MinPrice is { } min ? Money.ToMinor(min) : null,
            r.PrimaryImage,
            r.Version,
            r.UpdatedAt)).ToList();
        return new PagedDto<AdminProductSummaryDto>(items, query.Page, query.PageSize, totalCount, totalPages);
    }

    public async Task<AdminProductDetailDto?> GetProductAsync(Guid id, CancellationToken cancellationToken)
    {
        var product = await db.Products
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Slug,
                p.Brand,
                p.Description,
                p.Specifications,
                p.CategoryId,
                CategoryName = p.Category!.Name,
                p.IsActive,
                p.IsDemo,
                p.Version,
                p.UpdatedAt,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (product is null)
        {
            return null;
        }

        var variants = await db.ProductVariants.AsNoTracking()
            .Where(v => v.ProductId == id)
            .OrderBy(v => v.Sku).ThenBy(v => v.Id)
            .ToListAsync(cancellationToken);
        var images = await db.ProductImages.AsNoTracking()
            .Where(i => i.ProductId == id)
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Id)
            .ToListAsync(cancellationToken);

        return new AdminProductDetailDto(
            product.Id,
            product.Name,
            product.Slug,
            product.Brand,
            product.Description,
            product.Specifications,
            product.CategoryId,
            product.CategoryName,
            product.IsActive,
            product.IsDemo,
            product.Version,
            product.UpdatedAt,
            variants.Select(ToDto).ToList(),
            images.Select(ToDto).ToList());
    }

    public async Task<PagedDto<VariantHitDto>> SearchVariantsAsync(AdminVariantSearchQuery query, CancellationToken cancellationToken)
    {
        var variants = db.ProductVariants.AsNoTracking();
        if (query.Search is not null)
        {
            var pattern = LikePattern.Contains(query.Search);
            variants = variants.Where(v =>
                EF.Functions.ILike(v.Sku, pattern, LikePattern.EscapeCharacter) ||
                EF.Functions.ILike(v.DisplayName, pattern, LikePattern.EscapeCharacter) ||
                db.Products.Any(p => p.Id == v.ProductId && EF.Functions.ILike(p.Name, pattern, LikePattern.EscapeCharacter)));
        }

        var totalCount = await variants.LongCountAsync(cancellationToken);
        var totalPages = (int)((totalCount + query.PageSize - 1) / query.PageSize);
        var skip = (long)(query.Page - 1) * query.PageSize;
        if (skip >= totalCount)
        {
            return new PagedDto<VariantHitDto>([], query.Page, query.PageSize, totalCount, totalPages);
        }

        var rows = await variants
            .OrderBy(v => v.Sku).ThenBy(v => v.Id)
            .Skip((int)skip).Take(query.PageSize)
            .Select(v => new
            {
                v.Id,
                v.Sku,
                ProductName = db.Products.Where(p => p.Id == v.ProductId).Select(p => p.Name).First(),
                v.DisplayName,
                v.IsActive,
                ProductIsActive = db.Products.Where(p => p.Id == v.ProductId).Select(p => p.IsActive).First(),
                OnHand = db.InventoryBalances.Where(b => b.ProductVariantId == v.Id).Sum(b => (long?)b.OnHandQuantity),
            })
            .ToListAsync(cancellationToken);

        var items = rows.Select(r => new VariantHitDto(r.Id, r.Sku, r.ProductName, r.DisplayName, r.IsActive, r.ProductIsActive, r.OnHand ?? 0)).ToList();
        return new PagedDto<VariantHitDto>(items, query.Page, query.PageSize, totalCount, totalPages);
    }

    internal static AdminVariantDto ToDto(ProductVariant variant) => new(
        variant.Id,
        variant.Sku,
        variant.DisplayName,
        variant.Options,
        Money.ToMinor(variant.Price),
        variant.IsActive,
        variant.Version);

    internal static AdminImageDto ToDto(ProductImage image) =>
        new(image.StorageKey, image.AltText, image.IsPrimary, image.SortOrder, image.StorageKey);
}

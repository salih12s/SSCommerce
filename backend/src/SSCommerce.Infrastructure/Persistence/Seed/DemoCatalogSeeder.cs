using Microsoft.EntityFrameworkCore;
using SSCommerce.Domain.Catalog;

namespace SSCommerce.Infrastructure.Persistence.Seed;

/// <summary>Bir seed çalışmasında YENİ eklenen satır sayıları. İkinci çalışmada hepsi 0 olmalıdır.</summary>
public sealed record DemoCatalogSeedResult(int Categories, int Products, int Variants, int Images)
{
    public int Total => Categories + Products + Variants + Images;
}

/// <summary>
/// Geliştirme için demo katalog (4 kategori, kurgusal ürünler). İdempotenttir: doğal anahtarla (kategori/ürün slug'ı, SKU,
/// ürün başına görsel yolu) "var mı?" bakar, olmayanı ekler; var olan satırı DEĞİŞTİRMEZ (elle yapılan düzenlemeyi ezmez).
/// Rol, kullanıcı ya da parola oluşturmaz. Ortam denetimi burada değil <see cref="DemoCatalogSeedCommand"/> içindedir;
/// HTTP ucu yoktur.
/// </summary>
public sealed class DemoCatalogSeeder(SSCommerceDbContext db)
{
    public async Task<DemoCatalogSeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        int categories = 0, products = 0, variants = 0, images = 0;

        foreach (var demoCategory in DemoCatalogData.Categories)
        {
            var category = await db.Categories.SingleOrDefaultAsync(c => c.Slug == demoCategory.Slug, cancellationToken);
            if (category is null)
            {
                category = new Category(demoCategory.Name, demoCategory.Slug, parentId: null, demoCategory.SortOrder);
                db.Categories.Add(category);
                categories++;
            }

            foreach (var demoProduct in demoCategory.Products)
            {
                var product = await db.Products
                    .Include(p => p.Variants)
                    .Include(p => p.Images)
                    .AsSplitQuery()
                    .SingleOrDefaultAsync(p => p.Slug == demoProduct.Slug, cancellationToken);

                if (product is null)
                {
                    product = new Product(
                        category.Id,
                        demoProduct.Name,
                        demoProduct.Slug,
                        brand: null,
                        demoProduct.Description,
                        demoProduct.Specifications,
                        isDemo: true);
                    db.Products.Add(product);
                    products++;
                }

                foreach (var demoVariant in demoProduct.Variants)
                {
                    if (product.Variants.Any(v => v.Sku == demoVariant.Sku))
                    {
                        continue;
                    }

                    // SKU genel olarak benzersizdir; başka bir üründe varsa sessizce geçme, açık hata ver.
                    if (await db.ProductVariants.AnyAsync(v => v.Sku == demoVariant.Sku, cancellationToken))
                    {
                        throw new InvalidOperationException(
                            $"SKU {demoVariant.Sku} başka bir üründe zaten var; demo seed çakışması.");
                    }

                    product.AddVariant(demoVariant.Sku, demoVariant.DisplayName, demoVariant.Price, demoVariant.Options);
                    variants++;
                }

                if (demoProduct.Image is { } demoImage && product.Images.All(i => i.StorageKey != demoImage.Path))
                {
                    var makePrimary = product.Images.All(i => !i.IsPrimary);
                    product.AddImage(
                        demoImage.Path,
                        demoImage.AltText,
                        sortOrder: product.Images.Count,
                        isPrimary: makePrimary,
                        creditKey: demoImage.Path);
                    images++;
                }
            }
        }

        // Tek SaveChanges = tek işlem: seed ya tamamen yazılır ya hiç.
        await db.SaveChangesAsync(cancellationToken);
        return new DemoCatalogSeedResult(categories, products, variants, images);
    }
}

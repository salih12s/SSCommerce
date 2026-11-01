using Microsoft.EntityFrameworkCore;
using Npgsql;
using SSCommerce.Domain.Catalog;

namespace SSCommerce.Api.Tests.Database;

/// <summary>EF Core eşlemesi gerçek PostgreSQL'e karşı: gidiş-dönüş, jsonb, numeric, UTC damgaları, kısıtın EF üzerinden görünmesi.</summary>
public class CatalogPersistenceTests(MigratedDatabaseFixture fixture) : IClassFixture<MigratedDatabaseFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    private PostgresTestDatabase Db => fixture.Database;

    private static string Unique() => Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task Product_graph_round_trips_with_money_json_and_utc_timestamps()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FixedTimeProvider(T0);
        var categorySlug = $"kat-{Unique()}";
        var productSlug = $"urun-{Unique()}";
        var sku = $"RT-{Unique().ToUpperInvariant()}";

        await using (var write = Db.CreateDbContext(clock))
        {
            var category = new Category("Gidiş dönüş", categorySlug, sortOrder: 7);
            var product = new Product(
                category.Id, "Gidiş dönüş ürünü", productSlug,
                description: "Açıklama",
                specifications: new Dictionary<string, string> { ["Tür"] = "Deneme", ["Ağırlık"] = "1 kg" },
                isDemo: true);
            product.AddVariant(sku, "Varyant", 42999.00m, new Dictionary<string, string> { ["Renk"] = "Siyah", ["Hafiza"] = "256GB" });
            product.AddImage("/images/urun/test.webp", "Test görseli", isPrimary: true, creditKey: "/images/urun/test.webp");
            write.Products.Add(product);
            write.Categories.Add(category);
            await write.SaveChangesAsync(ct);
        }

        await using var read = Db.CreateDbContext();
        var loaded = await read.Products.Include(p => p.Variants).Include(p => p.Images).SingleAsync(p => p.Slug == productSlug, ct);

        Assert.True(loaded.IsDemo);
        Assert.Null(loaded.Brand);
        Assert.Equal("1 kg", loaded.Specifications!["Ağırlık"]);
        Assert.Equal(T0, loaded.CreatedAt);
        Assert.Equal(TimeSpan.Zero, loaded.CreatedAt.Offset);

        var variant = Assert.Single(loaded.Variants);
        Assert.Equal(42999.00m, variant.Price);
        Assert.Equal(4299900L, Money.ToMinor(variant.Price));
        Assert.Equal("TRY", variant.Currency);
        Assert.Equal("hafiza=256gb;renk=siyah", variant.OptionSignature);
        Assert.Equal("siyah", variant.Options["renk"]);
        Assert.Equal("256gb", variant.Options["hafiza"]);

        var image = Assert.Single(loaded.Images);
        Assert.True(image.IsPrimary);
    }

    [Fact]
    public async Task Update_changes_updated_at_but_not_created_at_and_untouched_rows_keep_their_stamps()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FixedTimeProvider(T0);
        var slug = $"upd-{Unique()}";

        await using (var write = Db.CreateDbContext(clock))
        {
            var category = new Category("Güncelleme", slug);
            write.Categories.Add(category);
            await write.SaveChangesAsync(ct);
        }

        clock.Now = T0.AddHours(3);
        await using (var update = Db.CreateDbContext(clock))
        {
            var category = await update.Categories.SingleAsync(c => c.Slug == slug, ct);
            category.SetActive(false);
            await update.SaveChangesAsync(ct);
        }

        clock.Now = T0.AddHours(6);
        await using (var noChange = Db.CreateDbContext(clock))
        {
            // Değişiklik yok: SaveChanges satır yazmaz, damga ilerlemez.
            var category = await noChange.Categories.SingleAsync(c => c.Slug == slug, ct);
            Assert.Equal(0, await noChange.SaveChangesAsync(ct));
            Assert.Equal(T0, category.CreatedAt);
            Assert.Equal(T0.AddHours(3), category.UpdatedAt);
            Assert.False(category.IsActive);
        }
    }

    [Fact]
    public async Task Duplicate_sku_through_ef_surfaces_as_unique_violation()
    {
        var ct = TestContext.Current.CancellationToken;
        var sku = $"EFD-{Unique().ToUpperInvariant()}";

        await using var context = Db.CreateDbContext();
        var category = new Category("EF çift SKU", $"kat-{Unique()}");
        var first = new Product(category.Id, "Bir", $"urun-{Unique()}");
        first.AddVariant(sku, "Bir", 10m);
        var second = new Product(category.Id, "İki", $"urun-{Unique()}");
        second.AddVariant(sku, "İki", 20m);
        context.Categories.Add(category);
        context.Products.AddRange(first, second);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(ct));
        var pg = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal("23505", pg.SqlState);
        Assert.Equal("ux_product_variants_sku", pg.ConstraintName);

        // SaveChanges tek işlemdir: çakışma yüzünden kategori ya da ürünlerden hiçbiri yazılmamalı.
        var written = await Db.ScalarAsync<long>(
            $"SELECT (SELECT count(*) FROM categories WHERE slug = '{category.Slug}') + (SELECT count(*) FROM products WHERE id IN ('{first.Id}', '{second.Id}'))",
            ct);
        Assert.Equal(0, written);
    }

    [Fact]
    public async Task Price_scale_is_exactly_two_digits_in_the_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var sku = $"PRC-{Unique().ToUpperInvariant()}";
        await using (var context = Db.CreateDbContext())
        {
            var category = new Category("Fiyat", $"kat-{Unique()}");
            var product = new Product(category.Id, "Fiyat", $"urun-{Unique()}");
            product.AddVariant(sku, "Fiyat", 349m);
            context.Categories.Add(category);
            context.Products.Add(product);
            await context.SaveChangesAsync(ct);
        }

        var text = await Db.ScalarAsync<string>($"SELECT price::text FROM product_variants WHERE sku = '{sku}'", ct);
        Assert.Equal("349.00", text);
    }
}

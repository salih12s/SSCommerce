using Microsoft.AspNetCore.Mvc.Testing;
using SSCommerce.Api.Tests.Database;
using SSCommerce.Domain.Catalog;

namespace SSCommerce.Api.Tests.Catalog;

[CollectionDefinition(Name)]
public sealed class CatalogApiCollection : ICollectionFixture<CatalogApiFixture>
{
    public const string Name = "catalog-api";
}

/// <summary>
/// Catalog API testleri için tek izole PostgreSQL veritabanı + KENDİ fixture verisi (seed'e bağlı değil, elle kurulur).
/// Veri yalnızca OKUNUR (testler yazmaz), bu yüzden testler arası paylaşım isolation'ı bozmaz. Zaman damgaları
/// <see cref="FixedTimeProvider"/> ile sabit: "newest" sırası saate bağlı değildir (flaky olmaz).
///
/// Görünür ürünler (9): Telefon A/B, Yarım Pasif Ürün (telefon) · Torun Kategori Ürünü (telefon-kilif) · Kılıf A/B (aksesuar) ·
/// Işık Lambası, Pil 100% Şarj, Alt_Cizgi Kablo (ev).
/// Gizli (sızmaması gereken) 5 ürün: pasif ürün, varyantsız ürün, yalnız pasif varyantlı ürün, pasif kategorideki ürün,
/// pasif alt kategorideki ürün. Hepsinin adında "Gizli" geçer: <c>q=gizli</c> sonucu 0 olmalı.
/// </summary>
public sealed class CatalogApiFixture : IAsyncLifetime
{
    public static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private PostgresTestDatabase? _database;
    private WebApplicationFactory<Program>? _development;
    private WebApplicationFactory<Program>? _production;

    public const int VisibleProductCount = 9;

    /// <summary>Development ortamı: OpenAPI açık, minimal API'nin ThrowOnBadRequest davranışı açık (en sıkı koşul).</summary>
    public HttpClient Development => RequireClient(_development);

    public HttpClient Production => RequireClient(_production);

    public PostgresTestDatabase Database => _database ?? throw SkipException();

    private HttpClient RequireClient(WebApplicationFactory<Program>? factory) =>
        factory?.CreateClient() ?? throw SkipException();

    public async ValueTask InitializeAsync()
    {
        _database = await PostgresTestDatabase.TryCreateAsync(migrate: true);
        if (_database is null)
        {
            return;
        }

        await SeedAsync(_database);
        _development = ApiFactory.Create("Development", _database.ConnectionString);
        _production = ApiFactory.Create("Production", _database.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (_development is not null)
        {
            await _development.DisposeAsync();
        }

        if (_production is not null)
        {
            await _production.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private static Exception SkipException()
    {
        Assert.Skip($"{PostgresTestDatabase.EnvironmentVariable} tanımlı değil; gerçek PostgreSQL testi atlandı (geçti sayılmaz).");
        return new InvalidOperationException(); // erişilmez
    }

    private static async Task SeedAsync(PostgresTestDatabase database)
    {
        var clock = new FixedTimeProvider(BaseTime);
        await using var db = database.CreateDbContext(clock);

        // --- Kategoriler ---
        var elektronik = new Category("Elektronik", "fx-elektronik", null, 10);
        var ev = new Category("Ev", "fx-ev", null, 10);                       // sort_order eşit, ad ikinci anahtar
        var aksesuar = new Category("Aksesuar", "fx-aksesuar", elektronik.Id, 20);
        var telefon = new Category("Telefon", "fx-telefon", elektronik.Id, 20);
        var kilif = new Category("Kılıf", "fx-telefon-kilif", telefon.Id, 30); // elektronik'in TORUNU
        var ikiz1 = new Category("Ikiz", "fx-ikiz-1", null, 5);                // sort_order + ad eşit, id ikinci anahtar
        var ikiz2 = new Category("Ikiz", "fx-ikiz-2", null, 5);
        var pasif = new Category("Pasif Kategori", "fx-pasif", null, 1);
        pasif.SetActive(false);
        var eski = new Category("Eski Alt Kategori", "fx-eski", elektronik.Id, 40);
        eski.SetActive(false);
        var yetim = new Category("Yetim", "fx-yetim", pasif.Id, 50);           // aktif ama üstü pasif
        db.Categories.AddRange(elektronik, ev, aksesuar, telefon, kilif, ikiz1, ikiz2, pasif, eski, yetim);
        await db.SaveChangesAsync();

        // --- Görünür ürünler ---
        await AddProductAsync(db, clock, telefon, "fx-telefon-a", "Telefon A", minute: 1,
            description: "Açıklama metni", specifications: new Dictionary<string, string> { ["Ekran"] = "6.1 inç", ["Tür"] = "Telefon" },
            variants: [("FX-TA-2", 12000.00m, "mavi", true), ("FX-TA-1", 10000.00m, "beyaz", true), ("FX-TA-0", 10000.00m, "siyah", true)],
            images: [("/images/test/a-on.webp", "Telefon A önden", 1, true), ("/images/test/a-yan.webp", "Telefon A yandan", 0, false)]);
        await AddProductAsync(db, clock, telefon, "fx-telefon-b", "Telefon B", minute: 2,
            variants: [("FX-TB-1", 42999.00m, null, true)]);
        await AddProductAsync(db, clock, aksesuar, "fx-kilif-a", "Kılıf A", minute: 3,
            variants: [("FX-KA-1", 199.90m, null, true)]);
        await AddProductAsync(db, clock, aksesuar, "fx-kilif-b", "Kılıf B", minute: 3, // Kılıf A ile AYNI created_at ve fiyat: id belirler
            variants: [("FX-KB-1", 199.90m, null, true)]);
        await AddProductAsync(db, clock, ev, "fx-isik-lambasi", "Işık Lambası", minute: 5, brand: "Lumen",
            variants: [("FX-IL-1", 349.00m, null, true)]);
        await AddProductAsync(db, clock, telefon, "fx-yarim-pasif", "Yarım Pasif Ürün", minute: 6,
            variants: [("FX-YP-1", 500.00m, "aktif", true), ("FX-YP-2", 1.00m, "pasif", false)]); // ucuz varyant pasif: fiyatı etkilememeli
        await AddProductAsync(db, clock, kilif, "fx-torun-urun", "Torun Kategori Ürünü", minute: 7,
            variants: [("FX-TR-1", 80.00m, null, true)]);
        await AddProductAsync(db, clock, ev, "fx-pil-yuzde", "Pil 100% Şarj", minute: 8,
            variants: [("FX-PY-1", 25.50m, null, true)]);
        await AddProductAsync(db, clock, ev, "fx-alt-cizgi", "Alt_Cizgi Kablo", minute: 9,
            variants: [("FX-AC-1", 75.00m, null, true)]);

        // --- Gizli ürünler (hiçbir uçta görünmemeli) ---
        await AddProductAsync(db, clock, ev, "fx-gizli-pasif-urun", "Gizli Pasif Ürün", minute: 10, active: false,
            variants: [("FX-G1-1", 10.00m, null, true)]);
        await AddProductAsync(db, clock, ev, "fx-gizli-varyantsiz", "Gizli Varyantsız Ürün", minute: 11, variants: []);
        await AddProductAsync(db, clock, ev, "fx-gizli-pasif-varyant", "Gizli Pasif Varyantlı Ürün", minute: 12,
            variants: [("FX-G3-1", 10.00m, null, false)]);
        await AddProductAsync(db, clock, pasif, "fx-gizli-pasif-kategori", "Gizli Pasif Kategori Ürünü", minute: 13,
            variants: [("FX-G4-1", 10.00m, null, true)]);
        await AddProductAsync(db, clock, eski, "fx-gizli-eski-kategori", "Gizli Eski Kategori Ürünü", minute: 14,
            variants: [("FX-G5-1", 10.00m, null, true)]);
    }

    private static async Task<Product> AddProductAsync(
        SSCommerce.Infrastructure.Persistence.SSCommerceDbContext db,
        FixedTimeProvider clock,
        Category category,
        string slug,
        string name,
        int minute,
        IEnumerable<(string Sku, decimal Price, string? Option, bool Active)> variants,
        string? brand = null,
        string? description = null,
        IReadOnlyDictionary<string, string>? specifications = null,
        IEnumerable<(string Path, string Alt, int Sort, bool Primary)>? images = null,
        bool active = true)
    {
        clock.Now = BaseTime.AddMinutes(minute);
        var product = new Product(category.Id, name, slug, brand, description, specifications, isDemo: true);
        foreach (var (sku, price, option, isActive) in variants)
        {
            var created = product.AddVariant(
                sku,
                $"{name} - {sku}",
                price,
                option is null ? null : new Dictionary<string, string> { ["renk"] = option });
            created.SetActive(isActive);
        }

        foreach (var (path, alt, sort, primary) in images ?? [])
        {
            product.AddImage(path, alt, sort, primary);
        }

        product.SetActive(active);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }
}

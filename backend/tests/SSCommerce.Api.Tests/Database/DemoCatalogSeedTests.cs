using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SSCommerce.Infrastructure;
using SSCommerce.Infrastructure.Persistence.Seed;

namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// Demo katalog seed'i (F3.3). Seed sayıları sınıf içinde sırayla değiştiği için her test KENDİ izole veritabanını açar.
/// </summary>
public class DemoCatalogSeedTests
{
    private static async Task<(long Categories, long Products, long Variants, long Images)> CountsAsync(PostgresTestDatabase database)
    {
        var ct = TestContext.Current.CancellationToken;
        return (
            await database.ScalarAsync<long>("SELECT count(*) FROM categories", ct),
            await database.ScalarAsync<long>("SELECT count(*) FROM products", ct),
            await database.ScalarAsync<long>("SELECT count(*) FROM product_variants", ct),
            await database.ScalarAsync<long>("SELECT count(*) FROM product_images", ct));
    }

    [Fact]
    public async Task Seeding_twice_does_not_duplicate_any_row()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();

        DemoCatalogSeedResult first;
        await using (var context = database.CreateDbContext())
        {
            first = await new DemoCatalogSeeder(context).SeedAsync(TestContext.Current.CancellationToken);
        }

        var afterFirst = await CountsAsync(database);
        Assert.Equal(4, afterFirst.Categories);
        Assert.True(afterFirst.Products >= 8, $"Her kategoride birkaç ürün beklenir (bulunan {afterFirst.Products}).");
        Assert.True(afterFirst.Variants > afterFirst.Products, "Birden fazla SKU'lu ürünler beklenir.");
        Assert.True(afterFirst.Images > 0);
        Assert.Equal(afterFirst.Categories, first.Categories);
        Assert.Equal(afterFirst.Products, first.Products);
        Assert.Equal(afterFirst.Variants, first.Variants);
        Assert.Equal(afterFirst.Images, first.Images);

        DemoCatalogSeedResult second;
        await using (var context = database.CreateDbContext())
        {
            second = await new DemoCatalogSeeder(context).SeedAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, second.Total);
        Assert.Equal(afterFirst, await CountsAsync(database));
    }

    [Fact]
    public async Task Every_category_has_several_products_and_some_products_have_multiple_skus()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using (var context = database.CreateDbContext())
        {
            await new DemoCatalogSeeder(context).SeedAsync(TestContext.Current.CancellationToken);
        }

        var ct = TestContext.Current.CancellationToken;
        var smallestCategory = await database.ScalarAsync<long>(
            "SELECT min(c) FROM (SELECT count(p.id) AS c FROM categories k LEFT JOIN products p ON p.category_id = k.id GROUP BY k.id) t", ct);
        Assert.True(smallestCategory >= 2, $"Her kategoride en az 2 ürün beklenir (en azı {smallestCategory}).");

        var multiSkuProducts = await database.ScalarAsync<long>(
            "SELECT count(*) FROM (SELECT product_id FROM product_variants GROUP BY product_id HAVING count(*) > 1) t", ct);
        Assert.True(multiSkuProducts >= 3);

        var multiOptionVariants = await database.ScalarAsync<long>(
            "SELECT count(*) FROM product_variants WHERE (SELECT count(*) FROM jsonb_object_keys(options)) >= 2", ct);
        Assert.True(multiOptionVariants >= 1, "En az bir varyant birden fazla seçenek (ör. bağlantı + sensör) taşımalı.");

        var optionlessVariants = await database.ScalarAsync<long>(
            "SELECT count(*) FROM product_variants WHERE option_signature = 'default' AND options = '{}'::jsonb", ct);
        Assert.True(optionlessVariants >= 1);
    }

    [Fact]
    public async Task All_seeded_products_are_marked_demo_without_brand_or_unsafe_promises()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using (var context = database.CreateDbContext())
        {
            await new DemoCatalogSeeder(context).SeedAsync(TestContext.Current.CancellationToken);
        }

        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM products WHERE NOT is_demo", ct));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM products WHERE brand IS NOT NULL", ct));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM products WHERE NOT is_active", ct));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM product_variants WHERE currency <> 'TRY' OR price <= 0", ct));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM products WHERE description IS NULL OR description NOT LIKE '%kurgusal%'", ct));

        // İndirim/stok/garanti vaadi yok (sözleşme: sahte vaat üretilmez).
        var promises = await database.ScalarAsync<long>(
            "SELECT count(*) FROM products WHERE description ~* '(indirim|stok|garanti|ücretsiz kargo|%[0-9]+)' OR name ~* '(indirim|stok|garanti)'", ct);
        Assert.Equal(0, promises);
    }

    [Fact]
    public async Task Each_seeded_image_file_exists_has_a_credit_entry_and_each_product_has_at_most_one_primary()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using (var context = database.CreateDbContext())
        {
            await new DemoCatalogSeeder(context).SeedAsync(TestContext.Current.CancellationToken);
        }

        var root = FindRepositoryRoot();
        var publicDir = Path.Combine(root, "apps", "web", "public");
        var credits = await File.ReadAllTextAsync(
            Path.Combine(root, "apps", "web", "src", "content", "image-credits.ts"),
            Encoding.UTF8,
            TestContext.Current.CancellationToken);

        await using var connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand("SELECT storage_key, credit_key FROM product_images", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        var rows = 0;
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows++;
            var storageKey = reader.GetString(0);
            var creditKey = reader.IsDBNull(1) ? null : reader.GetString(1);

            var file = Path.Combine(publicDir, storageKey.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"Görsel dosyası bulunamadı: {storageKey}");
            Assert.NotNull(creditKey);
            Assert.Contains($"\"{creditKey}\"", credits);
        }

        Assert.True(rows > 0);

        await reader.CloseAsync();
        var productsWithManyPrimaries = await database.ScalarAsync<long>(
            "SELECT count(*) FROM (SELECT product_id FROM product_images WHERE is_primary GROUP BY product_id HAVING count(*) > 1) t",
            TestContext.Current.CancellationToken);
        Assert.Equal(0, productsWithManyPrimaries);
    }

    [Fact]
    public async Task Seeder_does_not_overwrite_manual_edits_and_fills_in_missing_children()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using (var context = database.CreateDbContext())
        {
            await new DemoCatalogSeeder(context).SeedAsync(ct);
        }

        var before = await CountsAsync(database);
        await database.ExecuteAsync("UPDATE products SET name = 'Elle değiştirildi' WHERE slug = 'goz-ev-ici-kamera'", ct);
        await database.ExecuteAsync("DELETE FROM product_variants WHERE sku = 'SSC-6001-15'", ct);

        await using (var context = database.CreateDbContext())
        {
            var result = await new DemoCatalogSeeder(context).SeedAsync(ct);
            Assert.Equal(1, result.Variants);
            Assert.Equal(0, result.Products + result.Categories + result.Images);
        }

        Assert.Equal(before, await CountsAsync(database));
        Assert.Equal("Elle değiştirildi", await database.ScalarAsync<string>("SELECT name FROM products WHERE slug = 'goz-ev-ici-kamera'", ct));
    }

    // ---- Komut: ortam ve bekleyen migration korumaları ----

    private static ServiceProvider BuildServices(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString })
            .Build();
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddInfrastructure(configuration)
            .BuildServiceProvider();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public async Task Command_refuses_to_run_outside_development_and_writes_nothing(string environmentName)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var services = BuildServices(database.ConnectionString);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await DemoCatalogSeedCommand.RunAsync(
            services, new FakeEnvironment(environmentName), output, error, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("Development", error.ToString());
        Assert.Equal((0L, 0L, 0L, 0L), await CountsAsync(database));
    }

    [Fact]
    public async Task Command_fails_on_pending_migrations_instead_of_applying_them()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await using var services = BuildServices(database.ConnectionString);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await DemoCatalogSeedCommand.RunAsync(
            services, new FakeEnvironment(Environments.Development), output, error, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("bekleyen migration", error.ToString());
        Assert.Contains("dotnet ef database update", error.ToString());

        // Migration uygulanmadı: tablolar hâlâ yok.
        var tables = await database.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", TestContext.Current.CancellationToken);
        Assert.Equal(0, tables);
    }

    [Fact]
    public async Task Command_succeeds_in_development_and_is_idempotent()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var services = BuildServices(database.ConnectionString);
        var development = new FakeEnvironment(Environments.Development);

        using var firstOutput = new StringWriter();
        using var firstError = new StringWriter();
        Assert.Equal(0, await DemoCatalogSeedCommand.RunAsync(services, development, firstOutput, firstError, TestContext.Current.CancellationToken));
        Assert.Empty(firstError.ToString());
        var afterFirst = await CountsAsync(database);
        Assert.Equal(4, afterFirst.Categories);

        using var secondOutput = new StringWriter();
        using var secondError = new StringWriter();
        Assert.Equal(0, await DemoCatalogSeedCommand.RunAsync(services, development, secondOutput, secondError, TestContext.Current.CancellationToken));
        Assert.Contains("değişiklik yok", secondOutput.ToString());
        Assert.Equal(afterFirst, await CountsAsync(database));
    }

    [Fact]
    public async Task Command_error_output_never_contains_the_connection_string()
    {
        // Ulaşılamayan sunucu: hata iletisi yalnızca tür adı içermeli, host/kullanıcı/parola sızmamalı.
        const string unreachable = "Host=127.0.0.1;Port=1;Timeout=2;Database=gizli_db;Username=gizli_kullanici;Password=gizli_parola";
        await using var services = BuildServices(unreachable);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await DemoCatalogSeedCommand.RunAsync(
            services, new FakeEnvironment(Environments.Development), output, error, TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        var text = error.ToString();
        Assert.DoesNotContain("gizli_parola", text);
        Assert.DoesNotContain("gizli_kullanici", text);
        Assert.DoesNotContain("gizli_db", text);
        Assert.DoesNotContain("127.0.0.1", text);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend", "SSCommerce.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı (backend/SSCommerce.slnx).");
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "SSCommerce.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

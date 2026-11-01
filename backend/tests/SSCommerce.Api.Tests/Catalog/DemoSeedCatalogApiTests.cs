using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SSCommerce.Api.Tests.Database;
using SSCommerce.Infrastructure.Persistence.Seed;

namespace SSCommerce.Api.Tests.Catalog;

/// <summary>
/// Gerçek demo seed verisiyle (F3.3) uçtan uca: seed'in ürettiği katalog public API'den sözleşmeye uygun görünür.
/// Kendi izole veritabanını açar (sınıf fixture'ı). Testler seed içeriğine "ürün adı"yla değil YAPISIYLA bağlıdır
/// (SQL ile hesaplanan değerler API ile karşılaştırılır), seed metni değişince kırılmaz.
/// </summary>
public class DemoSeedCatalogApiTests(DemoSeedCatalogApiTests.SeededApi api) : IClassFixture<DemoSeedCatalogApiTests.SeededApi>
{
    public sealed class SeededApi : IAsyncLifetime
    {
        private PostgresTestDatabase? _database;
        private WebApplicationFactory<Program>? _factory;

        public PostgresTestDatabase Database => _database ?? throw Skip();

        public HttpClient Client => _factory?.CreateClient() ?? throw Skip();

        public async ValueTask InitializeAsync()
        {
            _database = await PostgresTestDatabase.TryCreateAsync(migrate: true);
            if (_database is null)
            {
                return;
            }

            await using (var context = _database.CreateDbContext())
            {
                await new DemoCatalogSeeder(context).SeedAsync();
            }

            _factory = ApiFactory.Create("Development", _database.ConnectionString);
        }

        public async ValueTask DisposeAsync()
        {
            if (_factory is not null)
            {
                await _factory.DisposeAsync();
            }

            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        private static Exception Skip()
        {
            Assert.Skip($"{PostgresTestDatabase.EnvironmentVariable} tanımlı değil; gerçek PostgreSQL testi atlandı (geçti sayılmaz).");
            return new InvalidOperationException();
        }
    }

    [Fact]
    public async Task Seeded_catalog_has_four_categories_and_matches_the_database()
    {
        var (response, json) = await api.Client.GetJsonAsync("/api/v1/categories");
        using var _ = json;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, json.RootElement.GetArrayLength());
        Assert.Equal(
            await api.Database.ScalarAsync<long>("SELECT count(*) FROM categories WHERE is_active", TestContext.Current.CancellationToken),
            json.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Seeded_list_total_equals_visible_product_count_and_prices_equal_sql_minimum_in_kurus()
    {
        var ct = TestContext.Current.CancellationToken;
        var expectedVisible = await api.Database.ScalarAsync<long>(
            """
            SELECT count(*) FROM products p JOIN categories c ON c.id = p.category_id
            WHERE p.is_active AND c.is_active AND EXISTS (SELECT 1 FROM product_variants v WHERE v.product_id = p.id AND v.is_active)
            """, ct);

        var (_, json) = await api.Client.GetJsonAsync("/api/v1/products?pageSize=50");
        using var _j = json;

        Assert.Equal(expectedVisible, json.RootElement.GetProperty("totalCount").GetInt64());
        Assert.True(expectedVisible >= 8);
        Assert.All(json.RootElement.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.True(item.GetProperty("isDemo").GetBoolean()); // seed ürünleri demo işaretli (D-027)
            Assert.Equal("TRY", item.GetProperty("currency").GetString());
        });

        // Her ürünün başlangıç fiyatı: SQL'de numeric * 100 (tam sayı) ile API'nin long kuruşu birebir aynı.
        foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
        {
            var slug = item.GetProperty("slug").GetString()!;
            Assert.Matches("^[a-z0-9-]+$", slug); // SQL'e gömmek güvenli (yalnız test içinde, API'den dönen slug)
            var sqlMinor = await api.Database.ScalarAsync<long>(
                $"SELECT (min(v.price) * 100)::bigint FROM product_variants v JOIN products p ON p.id = v.product_id WHERE p.slug = '{slug}' AND v.is_active", ct);
            Assert.Equal(sqlMinor, item.GetProperty("startingPriceMinor").GetInt64());
        }
    }

    [Fact]
    public async Task Seeded_products_each_resolve_in_detail_with_variants_and_primary_image_rules()
    {
        var (_, list) = await api.Client.GetJsonAsync("/api/v1/products?pageSize=50");
        using var _l = list;

        var multiSku = 0;
        foreach (var item in list.RootElement.GetProperty("items").EnumerateArray())
        {
            var (response, detail) = await api.Client.GetJsonAsync($"/api/v1/products/{item.GetProperty("slug").GetString()}");
            using var _d = detail;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var variants = detail.RootElement.GetProperty("variants").EnumerateArray().ToArray();
            Assert.NotEmpty(variants);
            multiSku += variants.Length > 1 ? 1 : 0;

            // Fiyatlar artan; ilk varyant başlangıç fiyatı.
            var prices = variants.Select(v => v.GetProperty("priceMinor").GetInt64()).ToArray();
            Assert.Equal(prices.Order().ToArray(), prices);
            Assert.Equal(prices[0], detail.RootElement.GetProperty("startingPriceMinor").GetInt64());

            // Liste ile detay ana görsel konusunda tutarlı: listede primaryImage varsa detayda tam bir isPrimary görsel var.
            var images = detail.RootElement.GetProperty("images").EnumerateArray().ToArray();
            var hasPrimaryInList = item.GetProperty("primaryImage").ValueKind == JsonValueKind.Object;
            Assert.Equal(hasPrimaryInList ? 1 : 0, images.Count(i => i.GetProperty("isPrimary").GetBoolean()));
            CatalogJson.AssertNoForbiddenKeys(detail.RootElement);
        }

        Assert.True(multiSku >= 3, "Seed'de birden fazla SKU'lu ürünler beklenir.");
    }

    [Fact]
    public async Task Seeded_category_filter_partitions_the_catalog()
    {
        var (_, categories) = await api.Client.GetJsonAsync("/api/v1/categories");
        var (_, all) = await api.Client.GetJsonAsync("/api/v1/products?pageSize=50");
        using var _c = categories;
        using var _a = all;

        long sum = 0;
        foreach (var category in categories.RootElement.EnumerateArray())
        {
            var (response, part) = await api.Client.GetJsonAsync($"/api/v1/products?category={category.GetProperty("slug").GetString()}&pageSize=50");
            using var _p = part;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            sum += part.RootElement.GetProperty("totalCount").GetInt64();
        }

        // Seed'de alt kategori yok: kategoriler ürünleri ayrık parçalara böler.
        Assert.Equal(all.RootElement.GetProperty("totalCount").GetInt64(), sum);
    }

    /// <summary>
    /// D-047 regresyonu: demo seed ASLA stok üretmez. Seed sonrası her ürün/varyant "Stokta yok" görünür (inStock=false, availableQuantity=0)
    /// ve depo/bakiye/hareket tabloları boştur. Biri seed'e "kolaylık olsun" diye stok eklerse bu test kırmızı olur.
    /// </summary>
    [Fact]
    public async Task Seeded_catalog_has_no_stock_at_all_every_product_and_variant_is_out_of_stock_and_inventory_tables_are_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(0, await api.Database.ScalarAsync<long>("SELECT count(*) FROM warehouses", ct));
        Assert.Equal(0, await api.Database.ScalarAsync<long>("SELECT count(*) FROM inventory_balances", ct));
        Assert.Equal(0, await api.Database.ScalarAsync<long>("SELECT count(*) FROM stock_movements", ct));

        var (_, list) = await api.Client.GetJsonAsync("/api/v1/products?pageSize=50");
        using var _l = list;
        var items = list.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.NotEmpty(items);
        var variantCount = 0;
        foreach (var item in items)
        {
            Assert.False(item.GetProperty("inStock").GetBoolean());
            var (response, detail) = await api.Client.GetJsonAsync($"/api/v1/products/{item.GetProperty("slug").GetString()}");
            using var _d = detail;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(detail.RootElement.GetProperty("inStock").GetBoolean());
            foreach (var variant in detail.RootElement.GetProperty("variants").EnumerateArray())
            {
                variantCount++;
                Assert.False(variant.GetProperty("inStock").GetBoolean());
                Assert.Equal(0, variant.GetProperty("availableQuantity").GetInt32());
            }
        }

        Assert.True(variantCount >= items.Length);
    }
}

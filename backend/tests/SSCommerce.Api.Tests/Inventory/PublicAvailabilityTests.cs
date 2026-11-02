using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Catalog;
using SSCommerce.Domain.Inventory;

namespace SSCommerce.Api.Tests.Inventory;

/// <summary>
/// Public stok görünürlüğü (F5.4, D-048): <c>available</c> = YALNIZ aktif depoların toplamı; <c>availableQuantity</c> = min(available, 20);
/// depo bilgisi ve cap üstü gerçek miktar asla sızmaz. Public uçlar kimlik gerektirmez, bu yüzden istemci anonimdir.
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class PublicAvailabilityTests(InventoryApiFixture fixture)
{
    private async Task<JsonDocument> DetailAsync(string slug, bool production = false)
    {
        var factory = production ? fixture.ProductionFactory : fixture.DevelopmentFactory;
        var response = await factory.CreateClient().GetAsync($"/api/v1/products/{slug}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private async Task<(JsonDocument Document, JsonElement Item)> ListedAsync(string slug, string name)
    {
        var response = await fixture.DevelopmentFactory.CreateClient()
            .GetAsync($"/api/v1/products?q={Uri.EscapeDataString(name)}&pageSize=50", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.JsonAsync();
        var item = document.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("slug").GetString() == slug);
        return (document, item);
    }

    private static JsonElement Variant(JsonDocument detail, int index) => detail.RootElement.GetProperty("variants")[index];

    [Fact]
    public async Task A_variant_without_any_balance_row_is_out_of_stock_with_quantity_zero()
    {
        var product = await fixture.CreateProductAsync([true]);

        using var detail = await DetailAsync(product.Slug);
        var (list, item) = await ListedAsync(product.Slug, product.Name);
        using var _ = list;

        Assert.False(Variant(detail, 0).GetProperty("inStock").GetBoolean());
        Assert.Equal(0, Variant(detail, 0).GetProperty("availableQuantity").GetInt32());
        Assert.False(detail.RootElement.GetProperty("inStock").GetBoolean());
        Assert.False(item.GetProperty("inStock").GetBoolean()); // stoksuz ürün listede KALIR ve inStock=false
        Assert.Equal(product.Variants[0].Id, Variant(detail, 0).GetProperty("id").GetGuid()); // sepet için varyant kimliği
    }

    [Fact]
    public async Task A_zero_balance_row_is_out_of_stock()
    {
        var product = await fixture.CreateProductAsync([true]);
        var warehouse = await fixture.CreateWarehouseAsync();
        await fixture.SeedBalanceAsync(warehouse, product.Variants[0].Id, 0);

        using var detail = await DetailAsync(product.Slug);

        Assert.False(Variant(detail, 0).GetProperty("inStock").GetBoolean());
        Assert.Equal(0, Variant(detail, 0).GetProperty("availableQuantity").GetInt32());
        Assert.False(detail.RootElement.GetProperty("inStock").GetBoolean());
    }

    [Fact]
    public async Task Stock_held_only_in_an_inactive_warehouse_is_not_in_stock()
    {
        var product = await fixture.CreateProductAsync([true]);
        var inactive = await fixture.CreateWarehouseAsync(active: false);
        await fixture.SeedBalanceAsync(inactive, product.Variants[0].Id, 40);

        using var detail = await DetailAsync(product.Slug);
        var (list, item) = await ListedAsync(product.Slug, product.Name);
        using var _ = list;

        Assert.False(Variant(detail, 0).GetProperty("inStock").GetBoolean());
        Assert.Equal(0, Variant(detail, 0).GetProperty("availableQuantity").GetInt32());
        Assert.False(detail.RootElement.GetProperty("inStock").GetBoolean());
        Assert.False(item.GetProperty("inStock").GetBoolean());
    }

    [Fact]
    public async Task Active_warehouses_are_summed_and_an_inactive_one_adds_nothing_and_reactivation_takes_effect_immediately()
    {
        var product = await fixture.CreateProductAsync([true]);
        var variant = product.Variants[0].Id;
        var a = await fixture.CreateWarehouseAsync();
        var b = await fixture.CreateWarehouseAsync();
        var inactive = await fixture.CreateWarehouseAsync(active: false);
        await fixture.SeedBalanceAsync(a, variant, 7);
        await fixture.SeedBalanceAsync(b, variant, 8);
        await fixture.SeedBalanceAsync(inactive, variant, 100);

        using (var detail = await DetailAsync(product.Slug))
        {
            Assert.Equal(15, Variant(detail, 0).GetProperty("availableQuantity").GetInt32());
            Assert.True(Variant(detail, 0).GetProperty("inStock").GetBoolean());
            Assert.True(detail.RootElement.GetProperty("inStock").GetBoolean());
        }

        await fixture.ExecuteAsync($"UPDATE warehouses SET is_active = true WHERE id = '{inactive}'");
        using var after = await DetailAsync(product.Slug);
        Assert.Equal(20, Variant(after, 0).GetProperty("availableQuantity").GetInt32()); // 115 -> cap 20
    }

    [Fact]
    public async Task The_quantity_is_capped_at_20_and_the_real_amount_is_never_leaked()
    {
        var product = await fixture.CreateProductAsync([true]);
        var variant = product.Variants[0].Id;
        var warehouse = await fixture.CreateWarehouseAsync();
        const int real = 54_321; // aranabilir, ayırt edici bir sayı
        await fixture.SeedBalanceAsync(warehouse, variant, real);

        using var detail = await DetailAsync(product.Slug);
        var (list, _) = await ListedAsync(product.Slug, product.Name);
        using var _l = list;

        Assert.Equal(InventoryRules.PublicAvailabilityCap, Variant(detail, 0).GetProperty("availableQuantity").GetInt32());
        Assert.Equal(20, Variant(detail, 0).GetProperty("availableQuantity").GetInt32());
        Assert.DoesNotContain("54321", detail.RootElement.GetRawText());
        Assert.DoesNotContain("54321", list.RootElement.GetRawText());
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(19, 19, true)]
    [InlineData(20, 20, true)]
    [InlineData(21, 20, true)]
    public async Task The_cap_boundary_values_are_exact(int onHand, int expected, bool inStock)
    {
        var product = await fixture.CreateProductAsync([true]);
        var warehouse = await fixture.CreateWarehouseAsync();
        await fixture.SeedBalanceAsync(warehouse, product.Variants[0].Id, onHand);

        using var detail = await DetailAsync(product.Slug);

        Assert.Equal(expected, Variant(detail, 0).GetProperty("availableQuantity").GetInt32());
        Assert.Equal(inStock, Variant(detail, 0).GetProperty("inStock").GetBoolean());
    }

    [Fact]
    public async Task Product_level_inStock_is_true_when_any_ACTIVE_variant_is_in_stock_and_an_inactive_variants_stock_does_not_count()
    {
        // Ürün A: iki aktif varyant, yalnız biri stokta -> ürün stokta.
        var partly = await fixture.CreateProductAsync([true, true]);
        var warehouse = await fixture.CreateWarehouseAsync();
        await fixture.SeedBalanceAsync(warehouse, partly.Variants[1].Id, 2);

        // Ürün B: aktif varyantın stoğu yok, PASİF varyantın stoğu var -> ürün stokta DEĞİL.
        var onlyInactiveHasStock = await fixture.CreateProductAsync([true, false]);
        await fixture.SeedBalanceAsync(warehouse, onlyInactiveHasStock.Variants[1].Id, 50);

        using var detailA = await DetailAsync(partly.Slug);
        var (listA, itemA) = await ListedAsync(partly.Slug, partly.Name);
        using var _a = listA;
        using var detailB = await DetailAsync(onlyInactiveHasStock.Slug);
        var (listB, itemB) = await ListedAsync(onlyInactiveHasStock.Slug, onlyInactiveHasStock.Name);
        using var _b = listB;

        Assert.True(detailA.RootElement.GetProperty("inStock").GetBoolean());
        Assert.True(itemA.GetProperty("inStock").GetBoolean());
        var variantsA = detailA.RootElement.GetProperty("variants").EnumerateArray().ToDictionary(v => v.GetProperty("sku").GetString()!);
        Assert.False(variantsA[partly.Variants[0].Sku].GetProperty("inStock").GetBoolean());
        Assert.True(variantsA[partly.Variants[1].Sku].GetProperty("inStock").GetBoolean());
        Assert.Equal(2, variantsA[partly.Variants[1].Sku].GetProperty("availableQuantity").GetInt32());

        Assert.False(detailB.RootElement.GetProperty("inStock").GetBoolean());
        Assert.False(itemB.GetProperty("inStock").GetBoolean());
        Assert.Equal(1, detailB.RootElement.GetProperty("variants").GetArrayLength()); // pasif varyant hiç görünmez
    }

    [Fact]
    public async Task Stock_changes_through_the_admin_API_show_up_in_the_public_response_without_any_cache()
    {
        var admin = await fixture.AdminAsync();
        var product = await fixture.CreateProductAsync([true]);
        var warehouse = await fixture.CreateWarehouseAsync();
        var variant = product.Variants[0].Id;

        await admin.ReceiveAsync(warehouse, variant, 3);
        using (var detail = await DetailAsync(product.Slug))
        {
            Assert.Equal(3, Variant(detail, 0).GetProperty("availableQuantity").GetInt32());
        }

        await admin.AdjustAsync(warehouse, variant, -3);
        using var empty = await DetailAsync(product.Slug);
        Assert.False(Variant(empty, 0).GetProperty("inStock").GetBoolean());
        Assert.False(empty.RootElement.GetProperty("inStock").GetBoolean());
    }

    [Fact]
    public async Task Public_responses_never_contain_warehouse_code_name_id_or_any_stock_breakdown()
    {
        var product = await fixture.CreateProductAsync([true]);
        var variant = product.Variants[0].Id;
        var marker = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var code = $"GIZLI-{marker}";
        var name = $"Gizli Depo Adi {marker}";
        var warehouse = await fixture.CreateWarehouseAsync(code: code, name: name);
        var second = await fixture.CreateWarehouseAsync();
        await fixture.SeedBalanceAsync(warehouse, variant, 9);
        await fixture.SeedBalanceAsync(second, variant, 4);

        using var detail = await DetailAsync(product.Slug);
        using var prodDetail = await DetailAsync(product.Slug, production: true);
        var (list, _) = await ListedAsync(product.Slug, product.Name);
        using var _l = list;

        foreach (var text in new[] { detail.RootElement.GetRawText(), prodDetail.RootElement.GetRawText(), list.RootElement.GetRawText() })
        {
            Assert.DoesNotContain(code, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(name, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(warehouse.ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(second.ToString(), text, StringComparison.OrdinalIgnoreCase);
        }

        CatalogJson.AssertNoForbiddenKeys(detail.RootElement);
        CatalogJson.AssertNoForbiddenKeys(list.RootElement);
        // 9 + 4 = 13: toplam görünür, kırılım (9 ve 4) ayrı bir alan olarak yok.
        Assert.Equal(13, Variant(detail, 0).GetProperty("availableQuantity").GetInt32());
        Assert.Equal(
            ["availableQuantity", "currency", "displayName", "id", "inStock", "options", "priceMinor", "sku"],
            Variant(detail, 0).EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public async Task The_public_list_and_detail_query_counts_are_unchanged_by_stock_data()
    {
        // Stoklu ürünler varken bile liste 2 (sayım + sayfa), detay 3 (ürün + varyantlar + görseller) sorgudur: stok N+1 üretmez.
        var warehouse = await fixture.CreateWarehouseAsync();
        for (var i = 0; i < 4; i++)
        {
            var product = await fixture.CreateProductAsync([true, true]);
            await fixture.SeedBalanceAsync(warehouse, product.Variants[0].Id, 5);
            await fixture.SeedBalanceAsync(warehouse, product.Variants[1].Id, 6);
        }

        var sample = await fixture.CreateProductAsync([true, true]);

        var many = await CommandsAsync("/api/v1/products?pageSize=50");
        var one = await CommandsAsync("/api/v1/products?pageSize=1");
        var detail = await CommandsAsync($"/api/v1/products/{sample.Slug}");

        Assert.Equal(2, many.Length);
        Assert.Equal(2, one.Length);
        Assert.Equal(3, detail.Length);
        Assert.Contains(many, sql => sql.Contains("inventory_balances", StringComparison.Ordinal)); // stok gerçekten aynı sorguda hesaplanıyor
        Assert.Contains(detail, sql => sql.Contains("inventory_balances", StringComparison.Ordinal));
    }

    private async Task<string[]> CommandsAsync(string url)
    {
        var log = new CatalogQueryShapeTests.CommandLog();
        await using var factory = ApiFactory.Create(
            "Production",
            fixture.Database.ConnectionString,
            services =>
            {
                services.AddSingleton<ILoggerProvider>(log);
                services.AddLogging(logging => logging.AddFilter(CatalogQueryShapeTests.EfCommandCategory, LogLevel.Information));
            });
        // Sunucu başlatma ısınma sorgusunu (ReadinessWarmUp) çalıştırır; istek ölçümünden önce temizlenir.
        using var client = factory.CreateClient();
        log.Commands.Clear();

        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return [.. log.Commands];
    }
}

using System.Net;
using System.Text.Json;
using SSCommerce.Api.Tests.Auth;

namespace SSCommerce.Api.Tests.Inventory;

/// <summary>Depo yönetimi ve okuma uçları (balances, movements): happy path, 404/409/400, sayfalama, sıralama.</summary>
[Collection(InventoryApiCollection.Name)]
public class WarehouseAndReadApiTests(InventoryApiFixture fixture)
{
    private static string NewCode() => $"T{Guid.NewGuid():N}"[..14].ToUpperInvariant();

    // ---------- Depolar ----------

    [Fact]
    public async Task Creating_a_warehouse_returns_201_and_it_appears_in_the_list()
    {
        var admin = await fixture.AdminAsync();
        var code = NewCode();

        var created = await admin.PostAsync("/api/v1/admin/warehouses", new { code, name = "  Ana Depo  ", isActive = false, id = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var json = await created.JsonAsync();
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal("Ana Depo", json.RootElement.GetProperty("name").GetString());       // kırpıldı
        Assert.True(json.RootElement.GetProperty("isActive").GetBoolean());                // gövdedeki isActive/id yok sayıldı
        var id = json.RootElement.GetProperty("id").GetGuid();

        using var list = await (await admin.GetAsync("/api/v1/admin/warehouses")).JsonAsync();
        Assert.Contains(list.RootElement.EnumerateArray(), w => w.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task A_duplicate_warehouse_code_gets_409_warehouse_code_taken()
    {
        var admin = await fixture.AdminAsync();
        var code = NewCode();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/v1/admin/warehouses", new { code, name = "A" })).StatusCode);

        await ApiAssert.ProblemAsync(await admin.PostAsync("/api/v1/admin/warehouses", new { code, name = "B" }), 409, "warehouse_code_taken");
        Assert.Equal(1, await fixture.ScalarAsync<long>($"SELECT count(*) FROM warehouses WHERE code = '{code}'"));
    }

    [Fact]
    public async Task Parallel_creations_with_the_same_code_produce_exactly_one_warehouse()
    {
        var admin = await fixture.AdminAsync();
        var code = NewCode();

        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await gate.Task;
            return await admin.PostAsync("/api/v1/admin/warehouses", new { code, name = "Yarış" });
        }).ToList();
        gate.SetResult();
        var responses = await Task.WhenAll(tasks);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await fixture.ScalarAsync<long>($"SELECT count(*) FROM warehouses WHERE code = '{code}'"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]                       // çok kısa
    [InlineData("ab-cd")]                   // küçük harf
    [InlineData("-AB")]                     // tireyle başlar
    [InlineData("AB CD")]                   // boşluk
    [InlineData("ÇAĞ-1")]                   // ASCII dışı
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456")] // 33 karakter
    public async Task Invalid_warehouse_codes_get_400(string code)
    {
        var admin = await fixture.AdminAsync();

        var response = await admin.PostAsync("/api/v1/admin/warehouses", new { code, name = "Depo" });

        await ApiAssert.ProblemAsync(response, 400, "validation_failed");
        using var json = await response.JsonAsync();
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("code", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_warehouse_name_gets_400(string name)
    {
        var admin = await fixture.AdminAsync();
        await ApiAssert.ProblemAsync(await admin.PostAsync("/api/v1/admin/warehouses", new { code = NewCode(), name }), 400, "validation_failed");
        await ApiAssert.ProblemAsync(await admin.PostAsync("/api/v1/admin/warehouses", new { code = NewCode(), name = new string('n', 101) }), 400, "validation_failed");
    }

    [Theory]
    [InlineData("ab\0cd")]
    [InlineData("iki\nsatir")]
    public async Task Control_characters_in_a_warehouse_name_get_400_not_500_on_create_and_patch(string name)
    {
        var admin = await fixture.AdminAsync();
        var id = await fixture.CreateWarehouseAsync();
        var before = await fixture.ScalarAsync<string>($"SELECT name FROM warehouses WHERE id = '{id}'");

        await ApiAssert.ProblemAsync(await admin.PostAsync("/api/v1/admin/warehouses", new { code = NewCode(), name }), 400, "validation_failed");
        await ApiAssert.ProblemAsync(await admin.PatchAsync($"/api/v1/admin/warehouses/{id}", new { name }), 400, "validation_failed");

        Assert.Equal(before, await fixture.ScalarAsync<string>($"SELECT name FROM warehouses WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Patching_a_warehouse_renames_and_toggles_it_but_never_changes_the_code()
    {
        var admin = await fixture.AdminAsync();
        var id = await fixture.CreateWarehouseAsync();
        var codeBefore = await fixture.ScalarAsync<string>($"SELECT code FROM warehouses WHERE id = '{id}'");

        var response = await admin.PatchAsync($"/api/v1/admin/warehouses/{id}", new { name = "Yeni Ad", isActive = false, code = "DEGISTI-1" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await response.JsonAsync();
        Assert.Equal("Yeni Ad", json.RootElement.GetProperty("name").GetString());
        Assert.False(json.RootElement.GetProperty("isActive").GetBoolean());
        Assert.Equal(codeBefore, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(codeBefore, await fixture.ScalarAsync<string>($"SELECT code FROM warehouses WHERE id = '{id}'"));

        // Yalnız isActive göndermek adı korur.
        using var reactivated = await (await admin.PatchAsync($"/api/v1/admin/warehouses/{id}", new { isActive = true })).JsonAsync();
        Assert.True(reactivated.RootElement.GetProperty("isActive").GetBoolean());
        Assert.Equal("Yeni Ad", reactivated.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Patching_a_missing_warehouse_gets_404_and_an_empty_patch_gets_400()
    {
        var admin = await fixture.AdminAsync();
        var id = await fixture.CreateWarehouseAsync();

        await ApiAssert.ProblemAsync(await admin.PatchAsync($"/api/v1/admin/warehouses/{Guid.NewGuid()}", new { name = "x" }), 404, "warehouse_not_found");
        await ApiAssert.ProblemAsync(await admin.PatchAsync($"/api/v1/admin/warehouses/{id}", new { }), 400, "validation_failed");
        await ApiAssert.ProblemAsync(await admin.PatchAsync($"/api/v1/admin/warehouses/{id}", new { name = "  " }), 400, "validation_failed");
    }

    [Fact]
    public async Task A_deactivated_warehouse_rejects_stock_operations_and_reactivating_allows_them_again()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        await admin.PatchAsync($"/api/v1/admin/warehouses/{warehouse}", new { isActive = false });

        await ApiAssert.ProblemAsync(await admin.ReceiveAsync(warehouse, variant, 2), 409, "warehouse_inactive");
        await admin.PatchAsync($"/api/v1/admin/warehouses/{warehouse}", new { isActive = true });

        Assert.Equal(HttpStatusCode.Created, (await admin.ReceiveAsync(warehouse, variant, 2)).StatusCode);
    }

    // ---------- Bakiyeler ----------

    [Fact]
    public async Task Balances_list_filters_by_warehouse_and_sku_and_shows_code_sku_quantity_and_version()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var otherWarehouse = await fixture.CreateWarehouseAsync();
        var (variantA, skuA) = await fixture.CreateVariantAsync();
        var (variantB, _) = await fixture.CreateVariantAsync();
        await fixture.SeedBalanceAsync(warehouse, variantA, 12);
        await fixture.SeedBalanceAsync(warehouse, variantB, 3);
        await fixture.SeedBalanceAsync(otherWarehouse, variantA, 99);
        var code = await fixture.ScalarAsync<string>($"SELECT code FROM warehouses WHERE id = '{warehouse}'");

        using var byWarehouse = await (await admin.GetAsync($"/api/v1/admin/inventory/balances?warehouseId={warehouse}")).JsonAsync();
        Assert.Equal(2, byWarehouse.RootElement.GetProperty("totalCount").GetInt64());
        var item = byWarehouse.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("variantId").GetGuid() == variantA);
        Assert.Equal(
            new[] { "onHandQuantity", "sku", "updatedAt", "variantId", "version", "warehouseCode", "warehouseId" }.Order().ToArray(),
            item.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(code, item.GetProperty("warehouseCode").GetString());
        Assert.Equal(skuA, item.GetProperty("sku").GetString());
        Assert.Equal(12, item.GetProperty("onHandQuantity").GetInt32());
        Assert.Equal(1, item.GetProperty("version").GetInt64());

        using var bySku = await (await admin.GetAsync($"/api/v1/admin/inventory/balances?sku={skuA}")).JsonAsync();
        Assert.Equal(2, bySku.RootElement.GetProperty("totalCount").GetInt64()); // iki depoda aynı varyant
        using var both = await (await admin.GetAsync($"/api/v1/admin/inventory/balances?warehouseId={otherWarehouse}&sku={skuA}")).JsonAsync();
        Assert.Equal(99, Assert.Single(both.RootElement.GetProperty("items").EnumerateArray()).GetProperty("onHandQuantity").GetInt32());
    }

    [Fact]
    public async Task Balances_are_paged_deterministically_and_a_page_beyond_the_total_is_empty_not_an_error()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        for (var i = 0; i < 5; i++)
        {
            var (variant, _) = await fixture.CreateVariantAsync();
            await fixture.SeedBalanceAsync(warehouse, variant, i + 1);
        }

        async Task<JsonDocument> Page(int page, int size) =>
            await (await admin.GetAsync($"/api/v1/admin/inventory/balances?warehouseId={warehouse}&page={page}&pageSize={size}")).JsonAsync();

        using var first = await Page(1, 2);
        using var second = await Page(2, 2);
        using var third = await Page(3, 2);
        using var beyond = await Page(9, 2);
        var ids = new[] { first, second, third }.SelectMany(p => p.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("variantId").GetGuid())).ToList();

        Assert.Equal(5, first.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(3, first.RootElement.GetProperty("totalPages").GetInt32());
        Assert.Equal([2, 2, 1], new[] { first, second, third }.Select(p => p.RootElement.GetProperty("items").GetArrayLength()).ToArray());
        Assert.Equal(5, ids.Distinct().Count()); // sayfalar arasında tekrar/kayıp yok
        Assert.Equal(0, beyond.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(5, beyond.RootElement.GetProperty("totalCount").GetInt64());
    }

    [Theory]
    [InlineData("warehouseId=not-a-guid")]
    [InlineData("page=0")]
    [InlineData("page=abc")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("sku=%00bad")]
    public async Task Invalid_balance_queries_get_400_validation_failed(string query)
    {
        var admin = await fixture.AdminAsync();

        await ApiAssert.ProblemAsync(await admin.GetAsync($"/api/v1/admin/inventory/balances?{query}"), 400, "validation_failed");
    }

    // ---------- Hareketler ----------

    [Fact]
    public async Task Movements_are_listed_newest_first_and_filtered_by_variant_and_warehouse()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();
        var (otherVariant, _) = await fixture.CreateVariantAsync();
        await admin.ReceiveAsync(warehouse, variant, 10, reference: "ILK");
        await Task.Delay(15, TestContext.Current.CancellationToken); // occurred_at sırası net olsun
        await admin.AdjustAsync(warehouse, variant, -4, "correction");
        await Task.Delay(15, TestContext.Current.CancellationToken);
        await admin.ReceiveAsync(warehouse, variant, 1, reference: "SON");
        await admin.ReceiveAsync(warehouse, otherVariant, 7);

        using var list = await (await admin.GetAsync($"/api/v1/admin/inventory/movements?warehouseId={warehouse}&variantId={variant}")).JsonAsync();

        Assert.Equal(3, list.RootElement.GetProperty("totalCount").GetInt64());
        var items = list.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new string?[] { "SON", null, "ILK" }, items.Select(i => i.GetProperty("reference").GetString()).ToArray());
        Assert.Equal([1, -4, 10], items.Select(i => i.GetProperty("quantityDelta").GetInt32()).ToArray());
        Assert.Equal([7, 6, 10], items.Select(i => i.GetProperty("onHandAfter").GetInt32()).ToArray());
        Assert.Equal("correction", items[1].GetProperty("operationType").GetString());
        Assert.All(items, i => Assert.False(i.GetProperty("replayed").GetBoolean()));

        using var byWarehouse = await (await admin.GetAsync($"/api/v1/admin/inventory/movements?warehouseId={warehouse}&pageSize=2&page=2")).JsonAsync();
        Assert.Equal(4, byWarehouse.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(2, byWarehouse.RootElement.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("variantId=nope")]
    [InlineData("warehouseId=00000000-0000-0000-0000-000000000000")]
    [InlineData("pageSize=1000")]
    public async Task Invalid_movement_queries_get_400_validation_failed(string query)
    {
        var admin = await fixture.AdminAsync();

        await ApiAssert.ProblemAsync(await admin.GetAsync($"/api/v1/admin/inventory/movements?{query}"), 400, "validation_failed");
    }
}

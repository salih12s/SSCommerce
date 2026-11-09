using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.Api.Tests.Orders;

namespace SSCommerce.Api.Tests.Admin;

/// <summary>
/// INTEGRATION testleri: pano ve raporlar. Neden gerçek DB: sayılar SQL'dir; test, panonun verdiği her sayıyı BAĞIMSIZ yazılmış ikinci bir SQL ile karşılaştırır
/// (ölçülen ile beklenen aynı kodla üretilmesin). Koleksiyon seri çalıştığı için veri test sırasında değişmez. Ayrıca tüm GET'lerin hiçbir şey yazmadığı
/// satır özetleriyle (md5) kanıtlanır, ve yanıtlarda gelir/ciro/tahsilat gibi var olmayan bir ödeme kaydını ima eden alan olmadığı kontrol edilir.
/// </summary>
[Collection(AdminApiCollection.Name)]
public partial class AdminDashboardTests(InventoryApiFixture fixture)
{
    [GeneratedRegex("revenue|ciro|gelir|tahsilat|paid|payment|refund|grandtotal|collected", RegexOptions.IgnoreCase)]
    private static partial Regex MoneyClaimRegex();

    /// <summary>Panoyu anlamlı kılan veri: farklı durumlarda siparişler, yaşlı bir açık sipariş, az/tükenen/yeterli stoklu satılabilir varyantlar, pasif kayıtlar.</summary>
    private async Task SeedAsync(ApiClient admin)
    {
        await fixture.OrderInStateAsync("placed", admin);
        var (_, processingId, _, _) = await fixture.OrderInStateAsync("processing", admin);
        await fixture.OrderInStateAsync("shipped", admin);
        await fixture.OrderInStateAsync("completed", admin);
        await fixture.OrderInStateAsync("cancelled", admin);

        // Yaşlı açık sipariş: created_at değişmezdir (trigger); test için trigger bu transaction'da kapatılır.
        await fixture.ExecuteAsync(
            $"""
            ALTER TABLE orders DISABLE TRIGGER trg_orders_restrict_update;
            UPDATE orders SET created_at = now() - interval '100 hours' WHERE id = '{processingId}';
            ALTER TABLE orders ENABLE TRIGGER trg_orders_restrict_update;
            """);

        // Stok durumları: tükenen (0), az (3), yeterli (500); pasif varyant, pasif depo, pasif kategori ayrıca.
        var warehouse = await fixture.CreateWarehouseAsync();
        var inactiveWarehouse = await fixture.CreateWarehouseAsync(active: false);
        var (zero, _) = await fixture.CreateVariantAsync();
        var (low, _) = await fixture.CreateVariantAsync();
        var (plenty, _) = await fixture.CreateVariantAsync();
        var (inactiveVariant, _) = await fixture.CreateVariantAsync(variantActive: false);
        var (hiddenByWarehouse, _) = await fixture.CreateVariantAsync();
        var (inactiveProduct, _) = await fixture.CreateVariantAsync(productActive: false);
        await fixture.SeedBalanceAsync(warehouse, low, 3);
        await fixture.SeedBalanceAsync(warehouse, plenty, 500);
        await fixture.SeedBalanceAsync(warehouse, inactiveVariant, 2);
        await fixture.SeedBalanceAsync(inactiveWarehouse, hiddenByWarehouse, 4); // yalnız pasif depoda: satılabilir stok 0
        await fixture.SeedBalanceAsync(warehouse, inactiveProduct, 2);
        _ = zero;
    }

    private async Task<long> ScalarSqlAsync(string sql) => await fixture.ScalarAsync<long>(sql);

    [Fact]
    public async Task Every_dashboard_number_equals_an_independent_sql_count()
    {
        var admin = await fixture.AdminAsync();
        await SeedAsync(admin);

        var response = await admin.GetAsync("/api/v1/admin/dashboard?lowStockThreshold=5&agedHours=48");

        using var json = await response.JsonAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var d = json.RootElement;
        foreach (var status in new[] { "placed", "processing", "shipped", "completed", "cancelled" })
        {
            Assert.Equal(await ScalarSqlAsync($"SELECT count(*) FROM orders WHERE status = '{status}'"), d.GetProperty("orders").GetProperty(status).GetInt64());
        }

        var open = d.GetProperty("openOrders");
        Assert.Equal(await ScalarSqlAsync("SELECT count(*) FROM orders WHERE status = 'placed' OR status = 'processing'"), open.GetProperty("count").GetInt64());
        Assert.Equal(
            await ScalarSqlAsync("SELECT coalesce(sum((item_subtotal * 100)::bigint), 0)::bigint FROM orders WHERE status IN ('placed', 'processing')"),
            open.GetProperty("unpaidSubtotalMinor").GetInt64());
        Assert.Equal(
            await ScalarSqlAsync("SELECT count(*) FROM orders WHERE status IN ('placed', 'processing') AND created_at < now() - interval '48 hours'"),
            open.GetProperty("agedCount").GetInt64());
        Assert.True(open.GetProperty("agedCount").GetInt64() >= 1);
        Assert.Equal(48, open.GetProperty("agedHours").GetInt32());
        var oldest = await fixture.ScalarAsync<DateTime>("SELECT min(created_at) FROM orders WHERE status IN ('placed', 'processing')");
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(oldest, DateTimeKind.Utc)), open.GetProperty("oldestCreatedAt").GetDateTimeOffset());

        var catalog = d.GetProperty("catalog");
        Assert.Equal(await ScalarSqlAsync("SELECT count(*) FROM products"), catalog.GetProperty("products").GetInt64());
        Assert.Equal(await ScalarSqlAsync("SELECT count(*) FROM products WHERE is_active = true"), catalog.GetProperty("activeProducts").GetInt64());
        Assert.Equal(await ScalarSqlAsync("SELECT count(*) FROM product_variants"), catalog.GetProperty("variants").GetInt64());
        Assert.Equal(await ScalarSqlAsync("SELECT count(*) FROM product_variants WHERE is_active = true"), catalog.GetProperty("activeVariants").GetInt64());

        // Bağımsız stok formülü: satılabilir = varyant + ürün + kategori aktif; stok = aktif depolardaki bakiyelerin toplamı.
        const string Sellable = """
            SELECT coalesce((SELECT sum(b.on_hand_quantity) FROM inventory_balances b WHERE b.product_variant_id = v.id
                             AND EXISTS (SELECT 1 FROM warehouses w WHERE w.id = b.warehouse_id AND w.is_active)), 0) AS qty
            FROM product_variants v
            WHERE v.is_active AND EXISTS (SELECT 1 FROM products p WHERE p.id = v.product_id AND p.is_active
                                          AND EXISTS (SELECT 1 FROM categories c WHERE c.id = p.category_id AND c.is_active))
            """;
        var stock = d.GetProperty("stock");
        Assert.Equal(5, stock.GetProperty("threshold").GetInt32());
        Assert.Equal(await ScalarSqlAsync($"SELECT count(*) FROM ({Sellable}) s WHERE qty > 0 AND qty <= 5"), stock.GetProperty("lowStockVariantCount").GetInt64());
        Assert.Equal(await ScalarSqlAsync($"SELECT count(*) FROM ({Sellable}) s WHERE qty = 0"), stock.GetProperty("outOfStockVariantCount").GetInt64());
        Assert.True(stock.GetProperty("lowStockVariantCount").GetInt64() >= 1);
        Assert.True(stock.GetProperty("outOfStockVariantCount").GetInt64() >= 2); // sıfır bakiyeli varyant + yalnız pasif depoda stoklu varyant
    }

    [Fact]
    public async Task The_lists_are_capped_at_ten_ordered_and_the_aged_list_holds_only_orders_older_than_the_threshold()
    {
        var admin = await fixture.AdminAsync();
        await SeedAsync(admin);

        using var json = await (await admin.GetAsync("/api/v1/admin/dashboard?agedHours=48")).JsonAsync();

        var d = json.RootElement;
        var aged = d.GetProperty("agedOpenOrders").EnumerateArray().ToList();
        Assert.InRange(aged.Count, 1, 10);
        Assert.All(aged, o =>
        {
            Assert.Contains(o.GetProperty("status").GetString(), new[] { "placed", "processing" });
            Assert.True(o.GetProperty("createdAt").GetDateTimeOffset() < DateTimeOffset.UtcNow.AddHours(-48));
        });
        var times = aged.Select(o => o.GetProperty("createdAt").GetDateTimeOffset()).ToList();
        Assert.Equal(times.Order().ToList(), times); // en eski önce
        var movements = d.GetProperty("recentMovements").EnumerateArray().ToList();
        Assert.InRange(movements.Count, 1, 10);
        var occurred = movements.Select(m => m.GetProperty("occurredAt").GetDateTimeOffset()).ToList();
        Assert.Equal(occurred.OrderDescending().ToList(), occurred); // en yeni önce
        var events = d.GetProperty("recentOrderEvents").EnumerateArray().ToList();
        Assert.InRange(events.Count, 1, 10);
        Assert.All(events, e =>
        {
            Assert.StartsWith("SS-", e.GetProperty("orderNumber").GetString(), StringComparison.Ordinal);
            Assert.Contains(e.GetProperty("actorKind").GetString(), new[] { "customer", "staff" });
        });
        Assert.Equal(
            await ScalarSqlAsync("SELECT count(*) FROM (SELECT 1 FROM order_status_history ORDER BY occurred_at DESC, id DESC LIMIT 10) x"),
            events.Count);
    }

    [Fact]
    public async Task The_dashboard_has_no_revenue_paid_or_grand_total_fields_only_the_unpaid_subtotal()
    {
        var admin = await fixture.AdminAsync();
        await SeedAsync(admin);

        var text = await (await admin.GetAsync("/api/v1/admin/dashboard")).TextAsync();

        var names = CatalogJsonNames(text);
        Assert.Contains("unpaidSubtotalMinor", names);
        Assert.DoesNotContain(names.Where(n => n != "unpaidSubtotalMinor"), n => MoneyClaimRegex().IsMatch(n)); // "unpaid..." ödenmemişi söyler; "paid" ödemeyi ima eden bir alan olurdu
    }

    private static List<string> CatalogJsonNames(string text)
    {
        using var json = JsonDocument.Parse(text);
        return Catalog.CatalogJson.AllPropertyNames(json.RootElement).Distinct().ToList();
    }

    [Theory]
    [InlineData("lowStockThreshold=0")]
    [InlineData("lowStockThreshold=1001")]
    [InlineData("lowStockThreshold=abc")]
    [InlineData("agedHours=0")]
    [InlineData("agedHours=721")]
    public async Task Dashboard_parameters_outside_their_ranges_are_400(string query)
    {
        var admin = await fixture.AdminAsync();

        var response = await admin.GetAsync($"/api/v1/admin/dashboard?{query}");

        await ApiAssert.ProblemAsync(response, 400, "validation_failed");
    }

    [Fact]
    public async Task The_dashboard_works_on_the_minimum_and_maximum_parameter_values()
    {
        var admin = await fixture.AdminAsync();

        var low = await admin.GetAsync("/api/v1/admin/dashboard?lowStockThreshold=1&agedHours=1");
        var high = await admin.GetAsync("/api/v1/admin/dashboard?lowStockThreshold=1000&agedHours=720");

        Assert.Equal(HttpStatusCode.OK, low.StatusCode);
        Assert.Equal(HttpStatusCode.OK, high.StatusCode);
    }

    // ---------- Düşük stok raporu ----------

    [Fact]
    public async Task The_low_stock_report_lists_sellable_variants_at_or_below_the_threshold_least_stock_first_and_is_paged()
    {
        var admin = await fixture.AdminAsync();
        await SeedAsync(admin);

        using var json = await (await admin.GetAsync("/api/v1/admin/reports/low-stock?threshold=5&pageSize=50")).JsonAsync();

        var rows = json.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.All(rows, r => Assert.True(r.GetProperty("onHandTotal").GetInt64() <= 5));
        var keys = rows.Select(r => (r.GetProperty("onHandTotal").GetInt64(), r.GetProperty("sku").GetString()!)).ToList();
        Assert.Equal(keys.OrderBy(k => k.Item1).ThenBy(k => k.Item2, StringComparer.Ordinal).ToList(), keys);
        Assert.Equal(
            json.RootElement.GetProperty("totalCount").GetInt64(),
            await ScalarSqlAsync(
                """
                SELECT count(*) FROM product_variants v
                WHERE v.is_active AND EXISTS (SELECT 1 FROM products p WHERE p.id = v.product_id AND p.is_active
                                              AND EXISTS (SELECT 1 FROM categories c WHERE c.id = p.category_id AND c.is_active))
                  AND coalesce((SELECT sum(b.on_hand_quantity) FROM inventory_balances b WHERE b.product_variant_id = v.id
                                AND EXISTS (SELECT 1 FROM warehouses w WHERE w.id = b.warehouse_id AND w.is_active)), 0) <= 5
                """));
        Assert.Contains(rows, r => r.GetProperty("onHandTotal").GetInt64() == 0); // tükenenler de listelenir
        Assert.Equal(
            new[] { "displayName", "onHandTotal", "productName", "sku", "variantId" },
            rows[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());

        using var secondPage = await (await admin.GetAsync("/api/v1/admin/reports/low-stock?threshold=5&pageSize=1&page=2")).JsonAsync();
        Assert.Equal(1, secondPage.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(2, secondPage.RootElement.GetProperty("page").GetInt32());
    }

    [Theory]
    [InlineData("threshold=0")]
    [InlineData("threshold=1001")]
    [InlineData("page=0")]
    [InlineData("page=1001")]
    [InlineData("pageSize=51")]
    public async Task Low_stock_report_parameters_are_validated(string query)
    {
        var admin = await fixture.AdminAsync();

        var response = await admin.GetAsync($"/api/v1/admin/reports/low-stock?{query}");

        await ApiAssert.ProblemAsync(response, 400, "validation_failed");
    }

    // ---------- Denetim kaydı ----------

    [Fact]
    public async Task The_audit_endpoint_returns_rows_newest_first_filters_by_target_and_shows_actor_email_only_for_staff()
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();
        const int version = 1;
        await admin.PatchAsync(AdminKit.Url($"/categories/{category}"), new { version, name = "Denetlenen" });

        using var json = await (await admin.GetAsync($"/api/v1/admin/audit?targetType=category&targetId={category}")).JsonAsync();

        var rows = json.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["category.update", "category.create"], rows.Select(r => r.GetProperty("action").GetString()));
        Assert.All(rows, r =>
        {
            Assert.Equal(admin.Email, r.GetProperty("actorEmail").GetString());
            Assert.Equal(admin.UserId, r.GetProperty("actorUserId").GetGuid());
            Assert.Equal("category", r.GetProperty("targetType").GetString());
            Assert.Equal(JsonValueKind.Object, r.GetProperty("changes").ValueKind);
        });
        // Aktör sonradan personel olmaktan çıkarsa e-posta gösterilmez.
        await fixture.ExecuteAsync($"DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\" = '{admin.UserId}'");
        var reader = await fixture.AdminAsync();
        using var after = await (await reader.GetAsync($"/api/v1/admin/audit?targetId={category}")).JsonAsync();
        Assert.All(after.RootElement.GetProperty("items").EnumerateArray(), r => Assert.Equal(JsonValueKind.Null, r.GetProperty("actorEmail").ValueKind));
    }

    [Theory]
    [InlineData("targetType=payment")]
    [InlineData("targetId=nope")]
    [InlineData("page=1001")]
    [InlineData("pageSize=51")]
    public async Task Audit_filters_are_validated(string query)
    {
        var admin = await fixture.AdminAsync();

        var response = await admin.GetAsync($"/api/v1/admin/audit?{query}");

        await ApiAssert.ProblemAsync(response, 400, "validation_failed");
    }

    [Fact]
    public async Task No_audit_row_ever_contains_a_forbidden_top_level_key_or_exceeds_8_kb()
    {
        var admin = await fixture.AdminAsync();
        await SeedAsync(admin);
        var (category, _, _) = await admin.NewCategoryAsync();
        await admin.NewProductAsync(category);

        Assert.Equal(0, await ScalarSqlAsync("SELECT count(*) FROM admin_audit_log WHERE changes ?| ARRAY['password','password_hash','security_stamp','token','email','phone','address','street_address']"));
        Assert.Equal(0, await ScalarSqlAsync("SELECT count(*) FROM admin_audit_log WHERE octet_length(changes::text) > 8192"));
        Assert.Equal(0, await ScalarSqlAsync("SELECT count(*) FROM admin_audit_log WHERE changes::text ~* '@|passwordhash|securitystamp'"));
    }

    // ---------- GET hiçbir şey yazmaz ----------

    [Fact]
    public async Task No_get_request_writes_anything_audit_movements_orders_history_and_catalog_rows_are_byte_identical_afterwards()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.WarehouseAsync();
        await SeedAsync(admin);
        var (category, _, _) = await admin.NewCategoryAsync();
        var (product, _, _) = await admin.NewProductAsync(category);
        var orderId = (await fixture.OrderInStateAsync("placed", admin)).OrderId;

        async Task<string> Snapshot() => string.Join(
            "|",
            await fixture.Md5Async("admin_audit_log", "id"),
            await fixture.Md5Async("stock_movements", "id"),
            await fixture.Md5Async("orders", "id"),
            await fixture.Md5Async("order_status_history", "id"),
            await fixture.Md5Async("inventory_balances", "id"),
            await fixture.Md5Async("categories", "id"),
            await fixture.Md5Async("products", "id"),
            await fixture.Md5Async("product_variants", "id"),
            await fixture.Md5Async("product_images", "id"),
            await fixture.Md5Async("warehouses", "id"),
            await fixture.Md5Async("\"AspNetUsers\"", "\"Id\""),
            await fixture.Md5Async("\"AspNetUserRoles\"", "\"UserId\", \"RoleId\""));
        var before = await Snapshot();

        string[] urls =
        [
            "/api/v1/admin/me", "/api/v1/admin/categories", "/api/v1/admin/products", $"/api/v1/admin/products/{product}", "/api/v1/admin/catalog/image-assets",
            "/api/v1/admin/variants", "/api/v1/admin/warehouses", "/api/v1/admin/inventory/balances", "/api/v1/admin/inventory/movements",
            "/api/v1/admin/orders", "/api/v1/admin/orders?open=true", $"/api/v1/admin/orders/{orderId}",
            "/api/v1/admin/dashboard", "/api/v1/admin/reports/low-stock", "/api/v1/admin/audit", $"/api/v1/admin/users/{admin.UserId}", "/api/v1/admin/staff",
        ];
        foreach (var url in urls)
        {
            var response = await admin.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)response.StatusCode}");
        }

        Assert.Equal(HttpStatusCode.OK, (await warehouse.GetAsync($"/api/v1/admin/orders/{orderId}")).StatusCode);
        Assert.Equal(before, await Snapshot());
    }
}

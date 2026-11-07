using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.Api.Tests.Orders;

namespace SSCommerce.Api.Tests.Admin;

[CollectionDefinition(Name)]
public sealed class AdminAtomicityCollection : ICollectionFixture<InventoryApiFixture>
{
    public const string Name = "admin-atomicity-api";
}

/// <summary>
/// INTEGRATION testleri: denetim satırı ile değişiklik AYNI transaction'dadır. Zorlanmış hata: test veritabanına, <c>admin_audit_log</c> INSERT'ini reddeden bir
/// trigger kurulur (denetim yazılamıyor gibi). Beklenen: değişiklik de kalmaz (satır, sürüm, rol, damga, görseller aynı), yanıt güvenli bir 500'dür ve
/// teslimat görünümünde kişisel veri DÖNMEZ (iz bırakılamıyorsa bilgi verilmez). Trigger kaldırılınca aynı istek başarılı olur: hatanın nedeni gerçekten denetimdi.
/// Kendi izole veritabanında koşar; trigger her test sonunda kaldırılır.
/// </summary>
[Collection(AdminAtomicityCollection.Name)]
public class AdminAtomicityTests(InventoryApiFixture fixture)
{
    private async Task WithFailingAuditAsync(Func<Task> body)
    {
        await fixture.ExecuteAsync(
            """
            CREATE OR REPLACE FUNCTION test_fail_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'forced audit failure'; END $$;
            CREATE TRIGGER trg_test_fail_audit BEFORE INSERT ON admin_audit_log FOR EACH ROW EXECUTE FUNCTION test_fail_audit();
            """);
        try
        {
            await body();
        }
        finally
        {
            await fixture.ExecuteAsync("DROP TRIGGER IF EXISTS trg_test_fail_audit ON admin_audit_log;");
        }
    }

    private static async Task AssertInternalErrorAsync(HttpResponseMessage response)
    {
        await ApiAssert.ProblemAsync(response, 500, "internal_error");
        var text = await response.TextAsync();
        Assert.DoesNotContain("forced audit failure", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_audit_row_rolls_back_a_category_change_and_its_version_bump()
    {
        var admin = await fixture.AdminAsync();
        var (id, version, _) = await admin.NewCategoryAsync(name: "Özgün Ad");
        var rowBefore = await fixture.Md5Async("categories", "id", $"id = '{id}'");

        await WithFailingAuditAsync(async () =>
        {
            var response = await admin.PatchAsync(AdminKit.Url($"/categories/{id}"), new { version, name = "Yazılamayan Ad", isActive = false });
            await AssertInternalErrorAsync(response);
            Assert.Equal(rowBefore, await fixture.Md5Async("categories", "id", $"id = '{id}'"));
            Assert.Equal(version, await fixture.VersionOfAsync("categories", id));
        });

        var retry = await admin.PatchAsync(AdminKit.Url($"/categories/{id}"), new { version, name = "Yazılan Ad" }); // aynı sürümle şimdi başarılı
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task A_failing_audit_row_leaves_no_new_category_product_or_variant()
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();
        var (product, _, _) = await admin.NewProductAsync(category);
        var categorySlug = $"kat-{AdminKit.Uniq()}";
        var productSlug = $"urun-{AdminKit.Uniq()}";
        var sku = AdminKit.NewSku();

        await WithFailingAuditAsync(async () =>
        {
            await AssertInternalErrorAsync(await admin.PostAsync(AdminKit.Url("/categories"), new { name = "Y", slug = categorySlug }));
            await AssertInternalErrorAsync(await admin.PostAsync(AdminKit.Url("/products"), new { categoryId = category, name = "Y", slug = productSlug }));
            await AssertInternalErrorAsync(await admin.PostAsync(AdminKit.Url($"/products/{product}/variants"), new { sku, displayName = "Y", priceMinor = 100 }));
        });

        Assert.Equal(0, await fixture.CountAsync($"SELECT count(*) FROM categories WHERE slug = '{categorySlug}'"));
        Assert.Equal(0, await fixture.CountAsync($"SELECT count(*) FROM products WHERE slug = '{productSlug}'"));
        Assert.Equal(0, await fixture.CountAsync($"SELECT count(*) FROM product_variants WHERE sku = '{sku}'"));
        // Slug ve SKU hâlâ boş: aynı değerlerle şimdi oluşturulabilir.
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync(AdminKit.Url("/categories"), new { name = "Y", slug = categorySlug })).StatusCode);
    }

    [Fact]
    public async Task A_failing_audit_row_keeps_the_old_price_and_the_old_images()
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();
        var (product, productVersion, _) = await admin.NewProductAsync(category);
        var (variant, _, variantVersion) = await admin.NewVariantAsync(product, 5_000);
        var imaged = await admin.PutAsync(
            AdminKit.Url($"/products/{product}/images"),
            new { version = productVersion, images = new[] { new { storageKey = "/images/urun/klavye.webp", altText = "Klavye", isPrimary = true } } });
        Assert.Equal(HttpStatusCode.OK, imaged.StatusCode);

        await WithFailingAuditAsync(async () =>
        {
            await AssertInternalErrorAsync(await admin.PatchAsync(AdminKit.Url($"/variants/{variant}"), new { version = variantVersion, priceMinor = 9_999 }));
            await AssertInternalErrorAsync(await admin.PutAsync(
                AdminKit.Url($"/products/{product}/images"),
                new { version = productVersion + 1, images = new[] { new { storageKey = "/images/urun/monitor.webp", altText = "Monitör", isPrimary = true } } }));
        });

        Assert.Equal(50.00m, await fixture.ScalarAsync<decimal>($"SELECT price FROM product_variants WHERE id = '{variant}'"));
        Assert.Equal(variantVersion, await fixture.VersionOfAsync("product_variants", variant));
        Assert.Equal("/images/urun/klavye.webp", await fixture.ScalarAsync<string>($"SELECT storage_key FROM product_images WHERE product_id = '{product}'"));
        Assert.Equal(productVersion + 1, await fixture.VersionOfAsync("products", product));
    }

    [Fact]
    public async Task A_failing_audit_row_rolls_back_warehouse_changes()
    {
        var admin = await fixture.AdminAsync();
        var code = $"AT{AdminKit.Uniq()}"[..12].ToUpperInvariant();
        var existing = await fixture.CreateWarehouseAsync(name: "Özgün Depo");

        await WithFailingAuditAsync(async () =>
        {
            await AssertInternalErrorAsync(await admin.PostAsync("/api/v1/admin/warehouses", new { code, name = "Yazılamayan" }));
            await AssertInternalErrorAsync(await admin.PatchAsync($"/api/v1/admin/warehouses/{existing}", new { name = "Değişmemeli", isActive = false }));
        });

        Assert.Equal(0, await fixture.CountAsync($"SELECT count(*) FROM warehouses WHERE code = '{code}'"));
        Assert.Equal("Özgün Depo", await fixture.ScalarAsync<string>($"SELECT name FROM warehouses WHERE id = '{existing}'"));
        Assert.True(await fixture.ScalarAsync<bool>($"SELECT is_active FROM warehouses WHERE id = '{existing}'"));
    }

    [Fact]
    public async Task A_failing_audit_row_rolls_back_a_role_grant_and_the_session_revocation_with_it()
    {
        var admin = await fixture.AdminAsync();
        var target = await fixture.CustomerAsync();
        var stamp = await fixture.ScalarAsync<string>($"SELECT \"SecurityStamp\" FROM \"AspNetUsers\" WHERE \"Id\" = '{target.UserId}'");

        await WithFailingAuditAsync(async () =>
        {
            await AssertInternalErrorAsync(await admin.SendAsync(HttpMethod.Put, $"/api/v1/admin/users/{target.UserId}/roles/Warehouse"));
        });

        Assert.Equal(0, await fixture.CountAsync($"SELECT count(*) FROM \"AspNetUserRoles\" WHERE \"UserId\" = '{target.UserId}' AND \"RoleId\" = '{SSCommerce.Infrastructure.Identity.AppRoles.WarehouseId}'"));
        Assert.Equal(stamp, await fixture.ScalarAsync<string>($"SELECT \"SecurityStamp\" FROM \"AspNetUsers\" WHERE \"Id\" = '{target.UserId}'"));
        Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/api/v1/auth/me")).StatusCode); // oturum hâlâ geçerli
    }

    [Fact]
    public async Task When_the_audit_row_cannot_be_written_the_delivery_view_returns_a_500_without_any_personal_data()
    {
        var admin = await fixture.AdminAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 1);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();

        await WithFailingAuditAsync(async () =>
        {
            var response = await admin.PostAsync($"/api/v1/admin/orders/{orderId}/delivery-view", null);
            await AssertInternalErrorAsync(response);
            var text = await response.TextAsync();
            foreach (var personal in new[] { "Ayşe Yılmaz", "5321234567", "Moda Caddesi", "Kadıköy" })
            {
                Assert.DoesNotContain(personal, text, StringComparison.Ordinal);
            }
        });

        Assert.Equal(0, await fixture.AuditCountAsync("order.delivery_view", orderId));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/admin/orders/{orderId}/delivery-view", null)).StatusCode);
        Assert.Equal(1, await fixture.AuditCountAsync("order.delivery_view", orderId));
    }
}

[CollectionDefinition(Name)]
public sealed class OrderLimitRaceCollection : ICollectionFixture<InventoryApiFixture>
{
    public const string Name = "order-limit-race-api";
}

/// <summary>
/// INTEGRATION testleri: sınır yarışı. Sepet tek satırdır ve sipariş sepeti boşaltır; kontrol edilen kural "açık sipariş sınırı sepet kilidi altında sayılır"dır, bu
/// yüzden bu veritabanında sepet silinir silinmez AYNI satırı geri ekleyen bir test trigger'ı vardır (sepet hep dolu: bekleyen her istek yalnız SINIRA takılabilir, boş
/// sepete değil). Doğru kodda tam sınır kadar sipariş oluşur. Sayımı sepet kilidinden önce yapan ya da kilidi kaldıran bir hata burada 3'ten fazla sipariş üretir
/// (mutasyon kanıtı raporda). Kendi izole veritabanında koşar.
/// </summary>
[Collection(OrderLimitRaceCollection.Name)]
public class OrderLimitRaceTests(InventoryApiFixture fixture)
{
    private async Task InstallRefillTriggerAsync() => await fixture.ExecuteAsync(
        """
        CREATE OR REPLACE FUNCTION test_refill_cart() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            INSERT INTO cart_items (id, cart_id, product_variant_id, quantity, unit_price_at_add, created_at, updated_at)
            VALUES (gen_random_uuid(), OLD.cart_id, OLD.product_variant_id, OLD.quantity, OLD.unit_price_at_add, now(), now());
            RETURN NULL;
        END $$;
        DROP TRIGGER IF EXISTS trg_test_refill ON cart_items;
        CREATE TRIGGER trg_test_refill AFTER DELETE ON cart_items FOR EACH ROW EXECUTE FUNCTION test_refill_cart();
        """);

    [Fact]
    public async Task Twenty_parallel_placements_with_different_keys_for_one_user_never_leave_more_than_three_open_orders()
    {
        for (var round = 0; round < 3; round++)
        {
            var (variant, _, _) = await fixture.StockedVariantAsync(500);
            var shopper = await fixture.ShopperAsync(variant, 1);
            await InstallRefillTriggerAsync();

            var results = await OrderKit.RunParallelAsync(20, _ => shopper.Client.PlaceAsync(shopper.AddressId, shopper.Quote, OrderKit.NewKey()));

            var created = results.Count(r => r.Status == HttpStatusCode.Created);
            var open = await fixture.CountAsync($"SELECT count(*) FROM orders WHERE user_id = '{shopper.UserId}' AND status IN ('placed', 'processing')");
            Assert.Equal(created, open);
            Assert.Equal(3, created); // sepet hep dolu olduğundan tam sınır kadar sipariş oluşur, ne eksik ne fazla
            Assert.All(results.Where(r => r.Status != HttpStatusCode.Created), r =>
            {
                Assert.Equal(HttpStatusCode.Conflict, r.Status);
                Assert.Equal("open_order_limit", r.Body.RootElement.GetProperty("code").GetString());
            });
            Assert.Equal(497, await fixture.OnHandSumAsync(variant));
            Assert.Equal(3, await fixture.CountAsync($"SELECT count(*) FROM order_inventory_allocations a JOIN order_items i ON i.id = a.order_item_id JOIN orders o ON o.id = i.order_id WHERE o.user_id = '{shopper.UserId}'"));
        }
    }

    [Fact]
    public async Task Parallel_placements_never_exceed_the_daily_limit_either()
    {
        await using var factory = ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            extraConfiguration: new Dictionary<string, string?>(InventoryApiFixture.HighLimits)
            {
                ["Ordering:MaxOpenOrdersPerUser"] = "1000",
                ["Ordering:MaxOrdersPerRollingDay"] = "5",
            });
        var (variant, _, _) = await fixture.StockedVariantAsync(500);
        var shopper = await fixture.ShopperAsync(variant, 1, factory);
        await InstallRefillTriggerAsync();

        var results = await OrderKit.RunParallelAsync(20, _ => shopper.Client.PlaceAsync(shopper.AddressId, shopper.Quote, OrderKit.NewKey()));

        Assert.Equal(5, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(5, await fixture.OrderCountAsync(shopper.UserId));
        Assert.All(results.Where(r => r.Status != HttpStatusCode.Created), r =>
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, r.Status);
            Assert.Equal("order_daily_limit", r.Body.RootElement.GetProperty("code").GetString());
        });
    }

    [Fact]
    public async Task Parallel_replays_of_one_key_stay_one_order_and_are_not_blocked_by_the_limit()
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(500);
        var shopper = await fixture.ShopperAsync(variant, 1);
        await InstallRefillTriggerAsync();
        var key = OrderKit.NewKey();
        Assert.Equal(HttpStatusCode.Created, (await shopper.Client.PlaceAsync(shopper.AddressId, shopper.Quote, key)).StatusCode);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await shopper.Client.PlaceAsync(shopper.AddressId, shopper.Quote, OrderKit.NewKey())).StatusCode);
        }

        var results = await OrderKit.RunParallelAsync(10, _ => shopper.Client.PlaceAsync(shopper.AddressId, shopper.Quote, key));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status)); // sınır dolu (3 açık) ama hepsi replay
        Assert.Equal(3, await fixture.OrderCountAsync(shopper.UserId));
    }
}

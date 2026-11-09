using System.Net;
using System.Text.Json;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.Api.Tests.Orders;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Admin;

/// <summary>
/// INTEGRATION testleri: (1) fiyat değişiminin ticari sonuçları; (2) Faz 5 stok uçlarına Faz 8 eklemeleri (depo denetimi, aktör e-postası, variantId süzgeci).
/// Fiyat testi bir REGRESSION kilididir: fiyat güncellenince eski siparişler ve stok defteri ASLA değişmemeli (md5 öncesi/sonrası), yeni önizleme yeni fiyatı göstermeli,
/// eski quote ile sipariş verilememeli (409 order_quote_changed).
/// </summary>
[Collection(AdminApiCollection.Name)]
public class AdminPriceAndInventoryTests(InventoryApiFixture fixture)
{
    // ---------- Fiyat değişimi ----------

    [Fact]
    public async Task A_price_change_reaches_the_public_storefront_invalidates_old_quotes_and_never_touches_existing_orders_or_the_stock_ledger()
    {
        var admin = await fixture.AdminAsync();
        var (slug, _, productId, variants) = await fixture.CreateProductAsync([true]);
        var (variantId, _) = variants[0];
        var warehouse = await fixture.CreateWarehouseAsync();
        await fixture.SeedBalanceAsync(warehouse, variantId, 50);

        // Eski bir sipariş (fiyat 100,00 TL) ve beklemedeki bir müşteri (eski quote).
        var earlier = await fixture.ShopperAsync(variantId, 2);
        var earlierOrder = await (await earlier.PlaceAsync()).IdOfAsync();
        var waiting = await fixture.ShopperAsync(variantId, 1);
        var itemsWhere = $"order_id = '{earlierOrder}'";
        var itemsBefore = await fixture.Md5Async("order_items", "id", itemsWhere);
        var orderBefore = await fixture.Md5Async("orders", "id", $"id = '{earlierOrder}'");
        var oldMovementIds = await fixture.ScalarAsync<string>($"SELECT string_agg('''' || id || '''', ',') FROM stock_movements WHERE product_variant_id = '{variantId}'");
        var movementsWhere = $"id IN ({oldMovementIds})";
        var movementsBefore = await fixture.Md5Async("stock_movements", "id", movementsWhere);
        var cartWhere = $"cart_id = (SELECT id FROM carts WHERE user_id = '{waiting.UserId}')";
        var cartBefore = await fixture.Md5Async("cart_items", "id", cartWhere);

        using var detail = await (await admin.GetAsync($"/api/v1/admin/products/{productId}")).JsonAsync();
        var versionBefore = detail.RootElement.GetProperty("variants")[0].GetProperty("version").GetInt32();
        var patch = await admin.PatchAsync($"/api/v1/admin/variants/{variantId}", new { version = versionBefore, priceMinor = 250_00 });

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using var publicProduct = await (await fixture.Anonymous().GetAsync($"/api/v1/products/{slug}")).JsonAsync();
        Assert.Equal(250_00, publicProduct.RootElement.GetProperty("variants")[0].GetProperty("priceMinor").GetInt64());
        Assert.Equal(250_00, publicProduct.RootElement.GetProperty("startingPriceMinor").GetInt64());
        Assert.Equal(cartBefore, await fixture.Md5Async("cart_items", "id", cartWhere)); // sepetin fiyat ipucu (unit_price_at_add) değişmedi

        var stale = await waiting.PlaceAsync(); // eski quote
        await ApiAssert.ProblemAsync(stale, 409, "order_quote_changed");
        var freshQuote = await waiting.Client.QuoteAsync(waiting.AddressId);
        Assert.NotEqual(waiting.Quote, freshQuote);
        Assert.Equal(HttpStatusCode.Created, (await waiting.Client.PlaceAsync(waiting.AddressId, freshQuote)).StatusCode);

        Assert.Equal(itemsBefore, await fixture.Md5Async("order_items", "id", itemsWhere)); // eski sipariş kalemleri bayt bayt aynı
        Assert.Equal("100.00:200.00", await fixture.ScalarAsync<string>($"SELECT string_agg(unit_price::text || ':' || line_subtotal::text, ',') FROM order_items WHERE {itemsWhere}"));
        Assert.Equal(orderBefore, await fixture.Md5Async("orders", "id", $"id = '{earlierOrder}'"));
        Assert.Equal(movementsBefore, await fixture.Md5Async("stock_movements", "id", movementsWhere)); // eski hareketler değişmedi
        Assert.Equal(1, await fixture.AuditCountAsync("variant.update", variantId));
    }

    [Fact]
    public async Task A_price_change_does_not_rewrite_earlier_stock_movements_or_cart_price_hints_byte_for_byte()
    {
        var admin = await fixture.AdminAsync();
        var (_, _, productId, variants) = await fixture.CreateProductAsync([true]);
        var (variantId, _) = variants[0];
        var warehouse = await fixture.CreateWarehouseAsync();
        await admin.ReceiveAsync(warehouse, variantId, 20);
        var shopper = await fixture.ShopperAsync(variantId, 1);
        var movements = await fixture.Md5Async("stock_movements", "id", $"product_variant_id = '{variantId}'");
        var cartRows = await fixture.Md5Async("cart_items", "id", $"product_variant_id = '{variantId}'");
        using var detail = await (await admin.GetAsync($"/api/v1/admin/products/{productId}")).JsonAsync();

        var patch = await admin.PatchAsync(
            $"/api/v1/admin/variants/{variantId}",
            new { version = detail.RootElement.GetProperty("variants")[0].GetProperty("version").GetInt32(), priceMinor = 1_00 });

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal(movements, await fixture.Md5Async("stock_movements", "id", $"product_variant_id = '{variantId}'"));
        Assert.Equal(cartRows, await fixture.Md5Async("cart_items", "id", $"product_variant_id = '{variantId}'"));
        _ = shopper;
    }

    // ---------- Depo denetimi ----------

    [Fact]
    public async Task Creating_and_renaming_a_warehouse_write_audit_rows_in_the_same_transaction_and_a_no_op_update_writes_none()
    {
        var admin = await fixture.AdminAsync();
        var code = $"AD{AdminKit.Uniq()}"[..12].ToUpperInvariant();

        var created = await admin.PostAsync("/api/v1/admin/warehouses", new { code, name = "Denetimli Depo" });
        var id = await created.Expect(HttpStatusCode.Created, w => w.GetProperty("id").GetGuid());
        var dup = await admin.PostAsync("/api/v1/admin/warehouses", new { code, name = "Kopya" });
        var renamed = await admin.PatchAsync($"/api/v1/admin/warehouses/{id}", new { name = "Yeni Depo Adı" });
        var same = await admin.PatchAsync($"/api/v1/admin/warehouses/{id}", new { name = "Yeni Depo Adı", isActive = true });
        var deactivated = await admin.PatchAsync($"/api/v1/admin/warehouses/{id}", new { isActive = false });

        await ApiAssert.ProblemAsync(dup, 409, "warehouse_code_taken");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.Equal(1, await fixture.AuditCountAsync("warehouse.create", id));
        Assert.Equal(2, await fixture.AuditCountAsync("warehouse.update", id)); // ad + pasifleştirme; no-op yok
        var changes = await fixture.ScalarAsync<string>($"SELECT changes::text FROM admin_audit_log WHERE action = 'warehouse.update' AND target_id = '{id}' ORDER BY occurred_at LIMIT 1");
        using var audit = JsonDocument.Parse(changes!);
        Assert.Equal("Denetimli Depo", audit.RootElement.GetProperty("old").GetProperty("name").GetString());
        Assert.Equal("Yeni Depo Adı", audit.RootElement.GetProperty("new").GetProperty("name").GetString());
        var missing = await admin.PatchAsync($"/api/v1/admin/warehouses/{Guid.NewGuid()}", new { name = "X" });
        await ApiAssert.ProblemAsync(missing, 404, "warehouse_not_found");
    }

    // ---------- Aktör e-postası ve variantId süzgeci ----------

    [Fact]
    public async Task Movement_lists_show_the_actor_email_only_for_actors_who_currently_hold_a_staff_role()
    {
        var admin = await fixture.AdminAsync();
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(0);
        Assert.Equal(HttpStatusCode.Created, (await admin.ReceiveAsync(warehouse, variant, 10)).StatusCode);
        var shopper = await fixture.ShopperAsync(variant, 1);
        Assert.Equal(HttpStatusCode.Created, (await shopper.PlaceAsync()).StatusCode);

        using var list = await (await admin.GetAsync($"/api/v1/admin/inventory/movements?variantId={variant}")).JsonAsync();

        var items = list.RootElement.GetProperty("items").EnumerateArray().ToList();
        var receiving = items.Single(i => i.GetProperty("operationType").GetString() == "receiving");
        var placement = items.Single(i => i.GetProperty("operationType").GetString() == "order_placement");
        Assert.Equal(admin.Email, receiving.GetProperty("actorEmail").GetString());
        Assert.Equal(JsonValueKind.Null, placement.GetProperty("actorEmail").ValueKind);   // müşteri anonim kalır
        Assert.DoesNotContain(shopper.Client.Email, list.RootElement.GetRawText(), StringComparison.Ordinal);

        // Rol sonradan kalkarsa e-posta artık gösterilmez.
        await fixture.ExecuteAsync($"DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\" = '{admin.UserId}'");
        var reader = await fixture.AdminAsync();
        using var after = await (await reader.GetAsync($"/api/v1/admin/inventory/movements?variantId={variant}")).JsonAsync();
        var receivingAfter = after.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("operationType").GetString() == "receiving");
        Assert.Equal(JsonValueKind.Null, receivingAfter.GetProperty("actorEmail").ValueKind);
    }

    [Fact]
    public async Task A_warehouse_staff_member_who_received_stock_also_gets_an_email_in_the_ledger_and_the_list_uses_one_actor_query()
    {
        var admin = await fixture.AdminAsync();
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(0);
        var otherAdmin = await fixture.AdminAsync();
        await admin.ReceiveAsync(warehouse, variant, 3);
        await otherAdmin.ReceiveAsync(warehouse, variant, 4);

        using var list = await (await admin.GetAsync($"/api/v1/admin/inventory/movements?variantId={variant}")).JsonAsync();

        var emails = list.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("actorEmail").GetString()).ToHashSet();
        Assert.Contains(admin.Email, emails);
        Assert.Contains(otherAdmin.Email, emails);
    }

    [Fact]
    public async Task The_balances_list_can_be_filtered_by_variant_id_and_a_malformed_id_is_400()
    {
        var admin = await fixture.AdminAsync();
        var (variantA, warehouse, _) = await fixture.StockedVariantAsync(5);
        var (variantB, _) = await fixture.CreateVariantAsync();
        await fixture.SeedBalanceAsync(warehouse, variantB, 9);

        using var onlyA = await (await admin.GetAsync($"/api/v1/admin/inventory/balances?variantId={variantA}")).JsonAsync();
        using var onlyB = await (await admin.GetAsync($"/api/v1/admin/inventory/balances?variantId={variantB}&warehouseId={warehouse}")).JsonAsync();
        var bad = await admin.GetAsync("/api/v1/admin/inventory/balances?variantId=oops");

        var rowA = Assert.Single(onlyA.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(variantA, rowA.GetProperty("variantId").GetGuid());
        Assert.Equal(5, rowA.GetProperty("onHandQuantity").GetInt32());
        Assert.Equal(variantB, Assert.Single(onlyB.RootElement.GetProperty("items").EnumerateArray()).GetProperty("variantId").GetGuid());
        await ApiAssert.ProblemAsync(bad, 400, "validation_failed");
    }

    [Fact]
    public async Task The_existing_movement_and_balance_shapes_only_gained_actorEmail()
    {
        var admin = await fixture.AdminAsync();
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(0);
        await admin.ReceiveAsync(warehouse, variant, 2);

        using var movements = await (await admin.GetAsync($"/api/v1/admin/inventory/movements?variantId={variant}")).JsonAsync();
        using var balances = await (await admin.GetAsync($"/api/v1/admin/inventory/balances?variantId={variant}")).JsonAsync();

        Assert.Equal(
            new[] { "actorEmail", "actorUserId", "id", "occurredAt", "onHandAfter", "operationType", "quantityDelta", "reason", "reference", "replayed", "sku", "variantId", "warehouseId" },
            movements.RootElement.GetProperty("items")[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { "onHandQuantity", "sku", "updatedAt", "variantId", "version", "warehouseCode", "warehouseId" },
            balances.RootElement.GetProperty("items")[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
    }
}


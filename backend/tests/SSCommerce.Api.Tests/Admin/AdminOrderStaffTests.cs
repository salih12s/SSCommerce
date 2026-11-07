using System.Net;
using System.Text.Json;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.Api.Tests.Orders;

namespace SSCommerce.Api.Tests.Admin;

/// <summary>
/// API testleri: personel sipariş görünümleri (açık sipariş listesi, detay + stok dağıtımları) ve Admin-only teslimat görünümü.
/// Neden integration: "kişisel veri sızmaz" ve "okuma denetim izi bırakır" iddiaları gerçek yanıt gövdesi ve gerçek <c>admin_audit_log</c> satırıyla sınanır.
/// Teslimat bilgisi (alıcı adı, telefon, açık adres) test verisinde bilinen sabit metinlerdir; yanıt gövdelerinde ARANIR (yoksa test kanıtlamaz).
/// </summary>
[Collection(AdminApiCollection.Name)]
public class AdminOrderStaffTests(InventoryApiFixture fixture)
{
    private static readonly string[] PersonalValues = ["Ayşe Yılmaz", "05321234567", "5321234567", "Moda Caddesi", "Kadıköy"];

    private static void AssertNoPersonalData(string text)
    {
        foreach (var value in PersonalValues)
        {
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        }

        foreach (var key in new[] { "recipientName", "phone", "streetAddress", "userId", "email", "deliveryAddress", "district", "province" })
        {
            Assert.DoesNotContain($"\"{key}\"", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------- Açık sipariş listesi ----------

    [Fact]
    public async Task The_open_order_list_returns_only_placed_and_processing_orders_oldest_first_and_carries_no_personal_data()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.WarehouseAsync();
        var (_, placedId, _, _) = await fixture.OrderInStateAsync("placed", admin);
        var (_, processingId, _, _) = await fixture.OrderInStateAsync("processing", admin);
        var (_, completedId, _, _) = await fixture.OrderInStateAsync("completed", admin);
        var (_, cancelledId, _, _) = await fixture.OrderInStateAsync("cancelled", admin);

        var response = await warehouse.GetAsync("/api/v1/admin/orders?open=true&pageSize=50");

        var text = await response.TextAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(text);
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToList();
        var ids = items.Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(placedId, ids);
        Assert.Contains(processingId, ids);
        Assert.DoesNotContain(completedId, ids);
        Assert.DoesNotContain(cancelledId, ids);
        Assert.All(items, i => Assert.Contains(i.GetProperty("status").GetString(), new[] { "placed", "processing" }));
        var created = items.Select(i => i.GetProperty("createdAt").GetDateTimeOffset()).ToList();
        Assert.Equal(created.Order().ToList(), created); // en eski önce
        Assert.True(ids.IndexOf(placedId) < ids.IndexOf(processingId));
        Assert.Equal(await fixture.CountAsync("SELECT count(*) FROM orders WHERE status IN ('placed', 'processing')"), json.RootElement.GetProperty("totalCount").GetInt64());
        AssertNoPersonalData(text);
    }

    [Theory]
    [InlineData("open=maybe")]
    [InlineData("open=1")]
    [InlineData("open=true&status=placed")]
    [InlineData("open=true&pageSize=51")]
    public async Task Invalid_open_order_filters_are_400_validation_failed(string query)
    {
        var admin = await fixture.AdminAsync();

        var response = await admin.GetAsync($"/api/v1/admin/orders?{query}");

        await ApiAssert.ProblemAsync(response, 400, "validation_failed");
    }

    [Fact]
    public async Task Open_false_behaves_like_no_filter_newest_first()
    {
        var admin = await fixture.AdminAsync();
        await fixture.OrderInStateAsync("completed", admin);

        using var json = await (await admin.GetAsync("/api/v1/admin/orders?open=false&pageSize=50")).JsonAsync();

        var created = json.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("createdAt").GetDateTimeOffset()).ToList();
        Assert.Equal(created.OrderDescending().ToList(), created);
    }

    // ---------- Detay ----------

    [Fact]
    public async Task The_staff_order_detail_lists_stock_allocations_per_warehouse_and_has_no_personal_data()
    {
        var admin = await fixture.AdminAsync();
        var warehouseUser = await fixture.WarehouseAsync();
        var (variant, warehouseA, warehouseB, sku) = await fixture.SplitVariantAsync(stockA: 2, stockB: 10);
        var shopper = await fixture.ShopperAsync(variant, 5);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();

        var response = await warehouseUser.GetAsync($"/api/v1/admin/orders/{orderId}");

        var text = await response.TextAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(text);
        var allocations = json.RootElement.GetProperty("allocations").EnumerateArray().ToList();
        Assert.Equal(2, allocations.Count);
        Assert.All(allocations, a => Assert.Equal(sku, a.GetProperty("sku").GetString()));
        Assert.Equal(5, allocations.Sum(a => a.GetProperty("quantity").GetInt32()));
        var byCode = allocations.ToDictionary(a => a.GetProperty("warehouseCode").GetString()!, a => a.GetProperty("quantity").GetInt32());
        Assert.Equal(2, await fixture.CountAsync($"SELECT count(DISTINCT warehouse_id) FROM order_inventory_allocations a JOIN order_items i ON i.id = a.order_item_id WHERE i.order_id = '{orderId}'"));
        Assert.Equal(2, byCode.Values.Min()); // A deposu (kod sırası önde) boşaltıldı, kalan B'den
        Assert.Equal(3, byCode.Values.Max());
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("items").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("statusHistory").ValueKind);
        AssertNoPersonalData(text);
        _ = (warehouseA, warehouseB, admin);
    }

    [Fact]
    public async Task The_staff_order_status_response_has_no_allocations_property_only_the_detail_does()
    {
        var admin = await fixture.AdminAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 1);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();

        using var status = await (await admin.StaffStatusAsync(orderId, "processing")).JsonAsync();

        Assert.False(status.RootElement.TryGetProperty("allocations", out _));
    }

    [Fact]
    public async Task Staff_order_detail_is_404_for_an_unknown_order_and_400_for_a_malformed_id()
    {
        var admin = await fixture.AdminAsync();

        var missing = await admin.GetAsync($"/api/v1/admin/orders/{Guid.NewGuid()}");
        var malformed = await admin.GetAsync("/api/v1/admin/orders/not-a-uuid");

        await ApiAssert.ProblemAsync(missing, 404, "order_not_found");
        await ApiAssert.ProblemAsync(malformed, 400, "validation_failed");
    }

    // ---------- Teslimat görünümü ----------

    [Fact]
    public async Task An_admin_can_read_the_delivery_view_and_exactly_one_audit_row_is_written_without_personal_data()
    {
        var admin = await fixture.AdminAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 1);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();
        var before = await fixture.AuditCountAsync("order.delivery_view", orderId);

        var response = await admin.PostAsync($"/api/v1/admin/orders/{orderId}/delivery-view", null);

        using var json = await response.JsonAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Ayşe Yılmaz", json.RootElement.GetProperty("recipientName").GetString());
        Assert.Equal("5321234567", json.RootElement.GetProperty("phone").GetString());
        Assert.Equal("İstanbul", json.RootElement.GetProperty("province").GetString());
        Assert.Equal("Kadıköy", json.RootElement.GetProperty("district").GetString());
        Assert.Contains("Moda Caddesi", json.RootElement.GetProperty("streetAddress").GetString(), StringComparison.Ordinal);
        Assert.Equal("34000", json.RootElement.GetProperty("postalCode").GetString());
        Assert.Equal("Ev", json.RootElement.GetProperty("label").GetString());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal(before + 1, await fixture.AuditCountAsync("order.delivery_view", orderId));
        var changes = await fixture.ScalarAsync<string>($"SELECT changes::text FROM admin_audit_log WHERE action = 'order.delivery_view' AND target_id = '{orderId}' ORDER BY occurred_at DESC LIMIT 1");
        Assert.Equal(admin.UserId, await fixture.ScalarAsync<Guid>($"SELECT actor_user_id FROM admin_audit_log WHERE action = 'order.delivery_view' AND target_id = '{orderId}' LIMIT 1"));
        AssertNoPersonalData(changes!);
        foreach (var value in PersonalValues)
        {
            Assert.DoesNotContain(value, changes!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Every_delivery_view_leaves_its_own_audit_row()
    {
        var admin = await fixture.AdminAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 1);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/admin/orders/{orderId}/delivery-view", null)).StatusCode);
        }

        Assert.Equal(3, await fixture.AuditCountAsync("order.delivery_view", orderId));
    }

    [Fact]
    public async Task Warehouse_and_customers_cannot_read_delivery_data_and_nothing_is_audited_for_them()
    {
        var warehouse = await fixture.WarehouseAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 1);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();
        var auditBefore = await fixture.AuditTotalAsync();

        var asWarehouse = await warehouse.PostAsync($"/api/v1/admin/orders/{orderId}/delivery-view", null);
        var asCustomer = await shopper.Client.PostAsync($"/api/v1/admin/orders/{orderId}/delivery-view", null);
        var anonymous = await fixture.Anonymous().PostAsync($"/api/v1/admin/orders/{orderId}/delivery-view", null);

        var text = await asWarehouse.TextAsync();
        await ApiAssert.ProblemAsync(asWarehouse, 403, "forbidden");
        await ApiAssert.ProblemAsync(asCustomer, 403, "forbidden");
        await ApiAssert.ProblemAsync(anonymous, 401, "unauthenticated");
        AssertNoPersonalData(text);
        Assert.Equal(auditBefore, await fixture.AuditTotalAsync());
    }

    [Fact]
    public async Task The_delivery_view_of_an_unknown_order_is_404_and_writes_no_audit_row_and_a_get_is_not_allowed()
    {
        var admin = await fixture.AdminAsync();
        var auditBefore = await fixture.AuditTotalAsync();

        var missing = await admin.PostAsync($"/api/v1/admin/orders/{Guid.NewGuid()}/delivery-view", null);
        var malformed = await admin.PostAsync("/api/v1/admin/orders/not-a-uuid/delivery-view", null);
        var get = await admin.GetAsync($"/api/v1/admin/orders/{Guid.NewGuid()}/delivery-view");

        await ApiAssert.ProblemAsync(missing, 404, "order_not_found");
        await ApiAssert.ProblemAsync(malformed, 400, "validation_failed");
        Assert.True(get.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound);
        Assert.Equal(auditBefore, await fixture.AuditTotalAsync());
    }

    [Fact]
    public async Task Delivery_data_never_appears_in_lists_or_the_customers_staff_views()
    {
        var admin = await fixture.AdminAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 1);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();
        await admin.PostAsync($"/api/v1/admin/orders/{orderId}/delivery-view", null);

        var list = await (await admin.GetAsync("/api/v1/admin/orders?pageSize=50")).TextAsync();
        var open = await (await admin.GetAsync("/api/v1/admin/orders?open=true&pageSize=50")).TextAsync();
        var detail = await (await admin.GetAsync($"/api/v1/admin/orders/{orderId}")).TextAsync();
        var audit = await (await admin.GetAsync("/api/v1/admin/audit?targetType=order&pageSize=50")).TextAsync();
        var dashboard = await (await admin.GetAsync("/api/v1/admin/dashboard")).TextAsync();

        foreach (var text in new[] { list, open, detail, audit, dashboard })
        {
            AssertNoPersonalData(text);
        }
    }
}

using System.Net;
using System.Text.Json;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Inventory;

namespace SSCommerce.Api.Tests.Orders;

/// <summary>
/// Durum makinesi HTTP üzerinden: her yasal ve yasadışı kenar, her rol için. Beklenen sonuç tablosu BURADA ELLE yazılmıştır (Domain'deki tabloyu
/// çağırmaz): kod ile sözleşme ayrışırsa bu test kırılır.
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class OrderStateTransitionTests(InventoryApiFixture fixture)
{
    private static readonly string[] States = ["placed", "processing", "shipped", "completed", "cancelled"];

    private static readonly HashSet<(string From, string To)> LegalEdges =
    [
        ("placed", "processing"), ("placed", "cancelled"),
        ("processing", "shipped"), ("processing", "cancelled"),
        ("shipped", "completed"),
    ];

    /// <summary>Sözleşme: aynı durum => 200 replay; kenar yok => 409; Warehouse + iptal => 403; diğerleri 200.</summary>
    private static (HttpStatusCode Status, string? Code) Expected(string role, string from, string to)
    {
        if (from == to)
        {
            return (HttpStatusCode.OK, null);
        }

        if (!LegalEdges.Contains((from, to)))
        {
            return (HttpStatusCode.Conflict, "order_transition_invalid");
        }

        return role == "Warehouse" && to == "cancelled" ? (HttpStatusCode.Forbidden, "forbidden") : (HttpStatusCode.OK, null);
    }

    public static TheoryData<string, string, string> StaffMatrix()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var role in new[] { "Warehouse", "Admin" })
        {
            foreach (var from in States)
            {
                foreach (var to in States)
                {
                    data.Add(role, from, to);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StaffMatrix))]
    public async Task Staff_transitions_follow_the_state_table_for_every_role(string role, string from, string to)
    {
        var admin = await fixture.AdminAsync();
        var actor = role == "Admin" ? admin : await fixture.WarehouseAsync();
        var (shopper, orderId, variant, warehouse) = await fixture.OrderInStateAsync(from, admin, stock: 10, quantity: 2);
        var historyBefore = await fixture.HistoryCountAsync(orderId);
        var onHandBefore = await fixture.OnHandAsync(warehouse, variant);
        var (expectedStatus, expectedCode) = Expected(role, from, to);

        var response = await actor.StaffStatusAsync(orderId, to);

        Assert.Equal(expectedStatus, response.StatusCode);
        using var json = await response.JsonAsync();
        if (expectedStatus == HttpStatusCode.OK)
        {
            Assert.Equal(to, json.RootElement.GetProperty("status").GetString());
            Assert.Equal(from == to, json.RootElement.GetProperty("replayed").GetBoolean());
            Assert.Equal(from == to ? historyBefore : historyBefore + 1, await fixture.HistoryCountAsync(orderId)); // replay yazmaz
        }
        else
        {
            Assert.Equal(expectedCode, OrderKit.Code(json));
            Assert.Equal(historyBefore, await fixture.HistoryCountAsync(orderId));
            Assert.Equal(1, await fixture.OrderStatusCountAsync(orderId, from)); // hiçbir şey değişmedi
        }

        // Stok yalnız GERÇEK bir iptalde döner; başka hiçbir geçiş stoğa dokunmaz.
        var restocked = from != to && to == "cancelled" && expectedStatus == HttpStatusCode.OK;
        Assert.Equal(restocked ? 10 : onHandBefore, await fixture.OnHandAsync(warehouse, variant));
        Assert.NotNull(shopper);
    }

    public static TheoryData<string> CustomerStates() => new(States);

    [Theory]
    [MemberData(nameof(CustomerStates))]
    public async Task A_customer_can_cancel_only_a_placed_order_a_cancelled_one_replays_every_other_state_is_409(string from)
    {
        var admin = await fixture.AdminAsync();
        var (shopper, orderId, variant, warehouse) = await fixture.OrderInStateAsync(from, admin, stock: 10, quantity: 2);
        var historyBefore = await fixture.HistoryCountAsync(orderId);
        var onHandBefore = await fixture.OnHandAsync(warehouse, variant);

        var response = await shopper.Client.CancelAsync(orderId);

        switch (from)
        {
            case "placed":
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(10, await fixture.OnHandAsync(warehouse, variant));
                Assert.Equal(historyBefore + 1, await fixture.HistoryCountAsync(orderId));
                break;
            case "cancelled":
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using (var json = await response.JsonAsync())
                {
                    Assert.True(json.RootElement.GetProperty("replayed").GetBoolean());
                }

                Assert.Equal(historyBefore, await fixture.HistoryCountAsync(orderId));
                break;
            default: // processing, shipped, completed: müşteri iptal edemez (durum çatışması, yetki değil)
                await ApiAssert.ProblemAsync(response, 409, "order_transition_invalid");
                Assert.Equal(historyBefore, await fixture.HistoryCountAsync(orderId));
                Assert.Equal(onHandBefore, await fixture.OnHandAsync(warehouse, variant));
                Assert.Equal(1, await fixture.OrderStatusCountAsync(orderId, from));
                break;
        }

        using var detail = await (await shopper.Client.GetOrderAsync(orderId)).JsonAsync();
        Assert.False(detail.RootElement.GetProperty("canCancel").GetBoolean()); // iptal edilebilir tek durum placed'dı ve artık iptal edildi
    }

    [Fact]
    public async Task A_warehouse_user_ships_an_order_through_the_whole_forward_path_and_each_step_is_recorded_as_staff()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.WarehouseAsync();
        var (shopper, orderId, variant, depot) = await fixture.OrderInStateAsync("placed", admin, stock: 10, quantity: 2);

        foreach (var step in new[] { "processing", "shipped", "completed" })
        {
            var response = await warehouse.StaffStatusAsync(orderId, step, step == "shipped" ? "Kargoya verildi" : null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(8, await fixture.OnHandAsync(depot, variant)); // tamamlanan sipariş stoğu geri vermez
        Assert.Equal(4, await fixture.HistoryCountAsync(orderId));
        Assert.Equal(
            3,
            await fixture.ScalarAsync<long>($"SELECT count(*) FROM order_status_history WHERE order_id = '{orderId}' AND actor_kind = 'staff' AND actor_user_id = '{warehouse.UserId}'"));
        using var detail = await (await shopper.Client.GetOrderAsync(orderId)).JsonAsync();
        Assert.Equal("completed", detail.RootElement.GetProperty("status").GetString());
        Assert.False(detail.RootElement.GetProperty("canCancel").GetBoolean());
        Assert.Equal(
            ["placed", "processing", "shipped", "completed"],
            detail.RootElement.GetProperty("statusHistory").EnumerateArray().Select(h => h.GetProperty("toStatus").GetString()!).ToArray());
    }

    [Fact]
    public async Task A_warehouse_cancel_of_a_placed_or_processing_order_is_403_forbidden_and_changes_nothing()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.WarehouseAsync();
        foreach (var state in new[] { "placed", "processing" })
        {
            var (_, orderId, variant, depot) = await fixture.OrderInStateAsync(state, admin, stock: 10, quantity: 2);

            var response = await warehouse.StaffStatusAsync(orderId, "cancelled", "izinsiz");

            await ApiAssert.ProblemAsync(response, 403, "forbidden");
            Assert.Equal(1, await fixture.OrderStatusCountAsync(orderId, state));
            Assert.Equal(8, await fixture.OnHandAsync(depot, variant));
            Assert.Equal(0, await fixture.MovementsOfOrderAsync(orderId, "order_cancellation"));
        }
    }

    public static TheoryData<string, string> InvalidStaffBodies => new()
    {
        { "unknown status", """{"status":"delivered"}""" },
        { "empty status", """{"status":""}""" },
        { "blank status", """{"status":"   "}""" },
        { "missing status", """{"reason":"x"}""" },
        { "status wrong type", """{"status":3}""" },
        { "status wrong case", """{"status":"Processing"}""" },
        { "reason too long", "{\"status\":\"processing\",\"reason\":\"" + new string('r', 201) + "\"}" },
        { "reason control char", """{"status":"processing","reason":"a\u0000b"}""" },
        { "reason line separator", "{\"status\":\"processing\",\"reason\":\"a\u2028b\"}" },
        { "empty body", "" },
        { "broken json", "{" },
        { "array body", "[]" },
    };

    [Theory]
    [MemberData(nameof(InvalidStaffBodies))]
    public async Task Invalid_staff_status_bodies_get_400_validation_failed_and_change_nothing(string name, string body)
    {
        Assert.False(string.IsNullOrEmpty(name));
        var admin = await fixture.AdminAsync();
        var (_, orderId, variant, depot) = await fixture.OrderInStateAsync("placed", admin, stock: 10, quantity: 2);
        var historyBefore = await fixture.HistoryCountAsync(orderId);

        var response = await admin.SendAsync(HttpMethod.Post, $"{OrderKit.StaffUrl}/{orderId}/status", body);

        await ApiAssert.ProblemAsync(response, 400, "validation_failed");
        Assert.Equal(historyBefore, await fixture.HistoryCountAsync(orderId));
        Assert.Equal(1, await fixture.OrderStatusCountAsync(orderId, "placed"));
        Assert.Equal(8, await fixture.OnHandAsync(depot, variant));
    }

    [Fact]
    public async Task A_staff_status_change_on_a_missing_order_is_404_and_a_malformed_id_is_400()
    {
        var admin = await fixture.AdminAsync();

        await ApiAssert.ProblemAsync(await admin.StaffStatusAsync(Guid.NewGuid(), "processing"), 404, "order_not_found");
        await ApiAssert.ProblemAsync(
            await admin.SendAsync(HttpMethod.Post, $"{OrderKit.StaffUrl}/not-a-guid/status", """{"status":"processing"}"""), 400, "validation_failed");
        await ApiAssert.ProblemAsync(
            await admin.SendAsync(HttpMethod.Post, $"{OrderKit.StaffUrl}/{Guid.Empty}/status", """{"status":"processing"}"""), 400, "validation_failed");
    }

    [Fact]
    public async Task Staff_responses_and_the_staff_list_carry_no_delivery_address_phone_email_or_user_id_but_show_actor_kind_and_reason()
    {
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.WarehouseAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var customer = await fixture.CustomerAsync();
        var marker = Guid.NewGuid().ToString("N")[..10];
        var addressResponse = await customer.PostAsync(
            "/api/v1/addresses",
            new { label = $"Etiket {marker}", recipientName = $"Alici {marker}", phone = "05337654321", province = "Bursa", district = $"Ilce {marker}", streetAddress = $"Sokak {marker} No 9", postalCode = "16000" });
        var addressId = await addressResponse.IdOfAsync();
        await customer.AddToCartAsync(variant, 2);
        var placed = await customer.PlaceAsync(addressId, await customer.QuoteAsync(addressId));
        var orderId = await placed.IdOfAsync();

        var status = await admin.StaffStatusAsync(orderId, "processing", "Hazırlanıyor");
        var statusText = await status.TextAsync();
        var shipped = await warehouse.StaffStatusAsync(orderId, "shipped");
        var shippedText = await shipped.TextAsync();
        var listText = await (await warehouse.GetAsync($"{OrderKit.StaffUrl}?status=shipped&pageSize=50")).TextAsync();
        var summary = await FindInStaffListAsync(warehouse, "shipped", orderId);

        foreach (var text in new[] { statusText, shippedText, listText, summary.GetRawText() })
        {
            foreach (var forbidden in new[] { marker, "05337654321", "5337654321", customer.Email, customer.UserId.ToString(), "deliveryAddress", "recipientName", "phone", "streetAddress", "userId", "email" })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        using var json = JsonDocument.Parse(statusText);
        var history = json.RootElement.GetProperty("statusHistory").EnumerateArray().ToList();
        Assert.Equal("staff", history[^1].GetProperty("actorKind").GetString());
        Assert.Equal("Hazırlanıyor", history[^1].GetProperty("reason").GetString());
        Assert.Equal("customer", history[0].GetProperty("actorKind").GetString());
        Assert.Equal(orderId, summary.GetProperty("id").GetGuid());
        Assert.Equal("shipped", summary.GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_staff_list_filters_by_status_pages_with_bounds_and_rejects_unknown_statuses()
    {
        var admin = await fixture.AdminAsync();
        var (_, orderId, _, _) = await fixture.OrderInStateAsync("cancelled", admin);

        var found = await FindInStaffListAsync(admin, "cancelled", orderId);
        Assert.Equal("cancelled", found.GetProperty("status").GetString());
        await ApiAssert.ProblemAsync(await admin.GetAsync($"{OrderKit.StaffUrl}?status=delivered"), 400, "validation_failed");
        foreach (var query in new[] { "page=0", "page=-1", "page=10001", "page=abc", "pageSize=0", "pageSize=51", "pageSize=x" })
        {
            await ApiAssert.ProblemAsync(await admin.GetAsync($"{OrderKit.StaffUrl}?{query}"), 400, "validation_failed");
        }

        using var edge = await (await admin.GetAsync($"{OrderKit.StaffUrl}?pageSize=50&page=10000")).JsonAsync();
        Assert.Equal(0, edge.RootElement.GetProperty("items").GetArrayLength());
        using var unfiltered = await (await admin.GetAsync($"{OrderKit.StaffUrl}?pageSize=3")).JsonAsync();
        Assert.True(unfiltered.RootElement.GetProperty("items").GetArrayLength() <= 3);
    }

    /// <summary>Paylaşılan test veritabanında sayfaları gezerek siparişi bulur (personel listesi herkesin siparişini içerir).</summary>
    private static async Task<JsonElement> FindInStaffListAsync(ApiClient staff, string status, Guid orderId)
    {
        for (var page = 1; ; page++)
        {
            var response = await staff.GetAsync($"{OrderKit.StaffUrl}?status={status}&pageSize=50&page={page}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.JsonAsync();
            foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
            {
                if (item.GetProperty("id").GetGuid() == orderId)
                {
                    return item.Clone();
                }
            }

            Assert.True(page < json.RootElement.GetProperty("totalPages").GetInt32(), "Sipariş personel listesinde bulunamadı.");
        }
    }
}

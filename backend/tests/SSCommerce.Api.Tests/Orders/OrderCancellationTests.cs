using System.Net;
using System.Text.Json;
using Npgsql;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Inventory;

namespace SSCommerce.Api.Tests.Orders;

/// <summary>
/// İptal: stok ÖZGÜN depolara geri döner (pasif depo/varyant dahil), üst sınır aşımı atomik reddedilir, hareketler değişmez kalır.
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class OrderCancellationTests(InventoryApiFixture fixture)
{
    [Fact]
    public async Task Cancelling_restocks_the_original_warehouses_exactly_and_the_customer_sees_a_cancelled_order()
    {
        var (variant, a, b, _) = await fixture.SplitVariantAsync(stockA: 2, stockB: 5);
        var shopper = await fixture.ShopperAsync(variant, 4);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();
        Assert.Equal(0, await fixture.OnHandAsync(a, variant));
        Assert.Equal(3, await fixture.OnHandAsync(b, variant));

        var response = await shopper.Client.CancelAsync(orderId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await response.JsonAsync();
        Assert.Equal("cancelled", json.RootElement.GetProperty("status").GetString());
        Assert.False(json.RootElement.GetProperty("canCancel").GetBoolean());
        Assert.False(json.RootElement.GetProperty("replayed").GetBoolean());
        var history = json.RootElement.GetProperty("statusHistory").EnumerateArray().ToList();
        Assert.Equal(["placed", "cancelled"], history.Select(h => h.GetProperty("toStatus").GetString()!).ToArray());
        Assert.Equal("placed", history[1].GetProperty("fromStatus").GetString());
        Assert.Equal(2, await fixture.OnHandAsync(a, variant));  // özgün depolar, özgün adetler
        Assert.Equal(5, await fixture.OnHandAsync(b, variant));
        Assert.Equal(2, await fixture.MovementsOfOrderAsync(orderId, "order_cancellation"));
        Assert.Equal(0, await fixture.MovementDeltaSumAsync(variant)); // defter sıfıra döner
        Assert.Equal(
            2,
            await fixture.ScalarAsync<long>(
                $"SELECT count(*) FROM stock_movements WHERE operation_type = 'order_cancellation' AND quantity_delta > 0 AND actor_user_id = '{shopper.UserId}' AND product_variant_id = '{variant}'"));
        using var listed = await (await shopper.Client.GetAsync(OrderKit.OrdersUrl)).JsonAsync();
        var summary = Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("cancelled", summary.GetProperty("status").GetString());
        Assert.False(summary.GetProperty("canCancel").GetBoolean());
    }

    [Fact]
    public async Task Goods_return_to_an_inactive_warehouse_and_an_inactive_variant_still_gets_its_stock_back()
    {
        var (variant, a, b, _) = await fixture.SplitVariantAsync(stockA: 2, stockB: 5);
        var shopper = await fixture.ShopperAsync(variant, 4);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();
        await fixture.ExecuteAsync($"UPDATE warehouses SET is_active = false WHERE id = '{a}'");
        await fixture.SetVariantActiveAsync(variant, false);

        var response = await shopper.Client.CancelAsync(orderId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, await fixture.OnHandAsync(a, variant)); // mal fiziksel olarak oradaydı
        Assert.Equal(5, await fixture.OnHandAsync(b, variant));
        Assert.Equal(2, await fixture.MovementsOfOrderAsync(orderId, "order_cancellation"));
    }

    [Fact]
    public async Task A_balance_row_that_no_longer_exists_is_recreated_by_the_restock()
    {
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(5);
        var shopper = await fixture.ShopperAsync(variant, 5);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();
        await fixture.ExecuteAsync($"DELETE FROM inventory_balances WHERE warehouse_id = '{warehouse}' AND product_variant_id = '{variant}'");

        var response = await shopper.Client.CancelAsync(orderId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.MovementsOfOrderAsync(orderId, "order_cancellation"));
    }

    [Fact]
    public async Task A_restock_beyond_the_max_on_hand_fails_atomically_with_409_stock_limit_exceeded_and_the_order_is_unchanged()
    {
        var (variant, a, b, _) = await fixture.SplitVariantAsync(stockA: 2, stockB: 5);
        var shopper = await fixture.ShopperAsync(variant, 4);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();
        // B'ye sonradan üst sınıra kadar stok girdi: 2 adetlik iade 1.000.000'u aşar. A'nın iadesi (önce işlenir) geri alınmak ZORUNDA.
        await fixture.SetStockAsync(b, variant, 1_000_000);
        var before = await fixture.StateAsync(shopper.UserId, variant);

        var response = await shopper.Client.CancelAsync(orderId);

        await ApiAssert.ProblemAsync(response, 409, "stock_limit_exceeded");
        Assert.Equal(before, await fixture.StateAsync(shopper.UserId, variant)); // A bakiyesi, hareketler, durum: aynen
        Assert.Equal(0, await fixture.OnHandAsync(a, variant));
        Assert.Equal(1, await fixture.OrderStatusCountAsync(orderId, "placed"));
        Assert.Equal(1, await fixture.HistoryCountAsync(orderId));
        Assert.Equal(0, await fixture.MovementsOfOrderAsync(orderId, "order_cancellation"));
        using var detail = await (await shopper.Client.GetOrderAsync(orderId)).JsonAsync();
        Assert.True(detail.RootElement.GetProperty("canCancel").GetBoolean());

        // Düzeltme sonrası aynı iptal başarılı olur (rollback güvenli yeniden denemeyi bozmadı).
        await fixture.SetStockAsync(b, variant, 100);
        Assert.Equal(HttpStatusCode.OK, (await shopper.Client.CancelAsync(orderId)).StatusCode);
        Assert.Equal(2, await fixture.OnHandAsync(a, variant));
        Assert.Equal(102, await fixture.OnHandAsync(b, variant));
    }

    [Fact]
    public async Task A_staff_cancel_by_an_admin_restocks_records_the_admin_as_actor_and_keeps_the_reason_out_of_the_customer_view()
    {
        var admin = await fixture.AdminAsync();
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 3);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();

        var response = await admin.StaffStatusAsync(orderId, "cancelled", "Müşteri aradı");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(10, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(
            admin.UserId,
            await fixture.ScalarAsync<Guid>(
                $"SELECT m.actor_user_id FROM stock_movements m WHERE m.operation_type = 'order_cancellation' AND m.product_variant_id = '{variant}'"));
        Assert.Equal(
            "staff",
            await fixture.ScalarAsync<string>($"SELECT actor_kind FROM order_status_history WHERE order_id = '{orderId}' AND to_status = 'cancelled'"));
        var customerView = await (await shopper.Client.GetOrderAsync(orderId)).TextAsync();
        Assert.DoesNotContain("Müşteri aradı", customerView);
        Assert.DoesNotContain(admin.UserId.ToString(), customerView);
        Assert.DoesNotContain("actorKind", customerView);
        using var staff = await response.JsonAsync();
        var last = staff.RootElement.GetProperty("statusHistory").EnumerateArray().Last();
        Assert.Equal("staff", last.GetProperty("actorKind").GetString());
        Assert.Equal("Müşteri aradı", last.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Stock_movements_stay_immutable_placement_and_cancellation_rows_cannot_be_updated_or_deleted()
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 2);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();
        Assert.Equal(HttpStatusCode.OK, (await shopper.Client.CancelAsync(orderId)).StatusCode);

        foreach (var type in new[] { "order_placement", "order_cancellation" })
        {
            var update = await Assert.ThrowsAsync<PostgresException>(
                () => fixture.ExecuteAsync($"UPDATE stock_movements SET quantity_delta = quantity_delta * 2 WHERE product_variant_id = '{variant}' AND operation_type = '{type}'"));
            var delete = await Assert.ThrowsAsync<PostgresException>(
                () => fixture.ExecuteAsync($"DELETE FROM stock_movements WHERE product_variant_id = '{variant}' AND operation_type = '{type}'"));
            Assert.Equal("23001", update.SqlState);
            Assert.Equal("23001", delete.SqlState);
        }

        Assert.Equal(2, await fixture.MovementCountAsync(variant));
        Assert.Equal(0, await fixture.MovementDeltaSumAsync(variant));
    }

    [Fact]
    public async Task A_multi_line_order_cancel_restores_every_line_and_the_order_row_changes_only_status_and_updated_at()
    {
        var (v1, w1, _) = await fixture.StockedVariantAsync(10);
        var (v2, w2, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync([(v1, 4), (v2, 7)]);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();
        var snapshot = await fixture.ScalarAsync<string>(
            $"SELECT item_subtotal || '|' || delivery_phone || '|' || idempotency_key || '|' || request_fingerprint || '|' || created_at FROM orders WHERE id = '{orderId}'");

        Assert.Equal(HttpStatusCode.OK, (await shopper.Client.CancelAsync(orderId)).StatusCode);

        Assert.Equal(10, await fixture.OnHandAsync(w1, v1));
        Assert.Equal(10, await fixture.OnHandAsync(w2, v2));
        Assert.Equal(
            snapshot,
            await fixture.ScalarAsync<string>(
                $"SELECT item_subtotal || '|' || delivery_phone || '|' || idempotency_key || '|' || request_fingerprint || '|' || created_at FROM orders WHERE id = '{orderId}'"));
        using var json = await (await shopper.Client.GetOrderAsync(orderId)).JsonAsync();
        Assert.Equal(11, json.RootElement.GetProperty("itemCount").GetInt32()); // iptal edilmiş sipariş yine de ne alındığını gösterir
        Assert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("subtotalMinor").ValueKind);
    }
}

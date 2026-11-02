using System.Net;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Orders;

namespace SSCommerce.Api.Tests.Inventory;

/// <summary>
/// REGRESYON (D-062): <c>order-</c> öneki sipariş akışına ayrılmıştır. Admin bu önekle bir anahtar gönderebilseydi, sonradan gelen bir sipariş
/// iptalinin tekil iade anahtarını (<c>order-cancel-&lt;dağıtım&gt;</c>) önceden işgal eder, iptali unique ihlaliyle düşürürdü.
/// Ayrıca <c>order_placement</c>/<c>order_cancellation</c> yalnız siparişlerin yazabileceği hareket türleridir; admin girdisi olamaz.
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class ReservedKeyTests(InventoryApiFixture fixture)
{
    public static TheoryData<string> ReservedKeys => new()
    {
        "order-place-0123456789abcdef0123456789abcdef",
        "order-cancel-0123456789abcdef0123456789abcdef",
        "ORDER-place-0123456789abcdef0123456789abcdef",
        "Order-Cancel-0123456789abcdef0123456789abcdef",
        "oRdEr-anything-at-all-12345",
        "order-1234567890123456",
    };

    [Theory]
    [MemberData(nameof(ReservedKeys))]
    public async Task An_order_prefixed_key_in_any_case_is_rejected_on_receive_and_adjust_with_400_and_nothing_is_written(string key)
    {
        var admin = await fixture.AdminAsync();
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);

        var receive = await admin.ReceiveAsync(warehouse, variant, 3, key);
        var adjust = await admin.AdjustAsync(warehouse, variant, -2, "adjustment", key);

        await ApiAssert.ProblemAsync(receive, 400, "validation_failed");
        await ApiAssert.ProblemAsync(adjust, 400, "validation_failed");
        Assert.Contains("Idempotency-Key", await receive.TextAsync());
        Assert.Equal(10, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
        Assert.Equal(0, await fixture.MovementKeyCountAsync(key));
    }

    [Fact]
    public async Task Keys_that_merely_resemble_the_prefix_stay_valid()
    {
        var admin = await fixture.AdminAsync();
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);

        foreach (var key in new[] { "orderly-key-1234567890", "reorder-order-1234567890", "ord-er-1234567890123" })
        {
            Assert.Equal(HttpStatusCode.Created, (await admin.ReceiveAsync(warehouse, variant, 1, key)).StatusCode);
        }

        Assert.Equal(13, await fixture.OnHandAsync(warehouse, variant));
    }

    [Fact]
    public async Task An_admin_cannot_pre_empt_the_reversal_key_of_a_real_allocation_and_the_cancel_still_succeeds()
    {
        var admin = await fixture.AdminAsync();
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 3);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();
        var allocationId = await fixture.ScalarAsync<Guid>(
            $"SELECT a.id FROM order_inventory_allocations a JOIN order_items i ON i.id = a.order_item_id WHERE i.order_id = '{orderId}'");

        var attempt = await admin.ReceiveAsync(warehouse, variant, 3, $"order-cancel-{allocationId:N}");

        await ApiAssert.ProblemAsync(attempt, 400, "validation_failed");
        Assert.Equal(HttpStatusCode.OK, (await shopper.Client.CancelAsync(orderId)).StatusCode);
        Assert.Equal(10, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.MovementKeyCountAsync($"order-cancel-{allocationId:N}"));
    }

    [Theory]
    [InlineData("order_placement")]
    [InlineData("order_cancellation")]
    [InlineData("Order_Placement")]
    public async Task The_order_ledger_types_are_rejected_as_admin_type_input_with_400_validation_failed(string type)
    {
        var admin = await fixture.AdminAsync();
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);

        var negative = await admin.AdjustAsync(warehouse, variant, -3, type);
        var positive = await admin.AdjustAsync(warehouse, variant, 3, type);

        await ApiAssert.ProblemAsync(negative, 400, "validation_failed");
        await ApiAssert.ProblemAsync(positive, 400, "validation_failed");
        Assert.Equal(10, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
        // Kabul edilen türler değişmedi.
        Assert.Equal(HttpStatusCode.Created, (await admin.AdjustAsync(warehouse, variant, -1, "adjustment")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.AdjustAsync(warehouse, variant, 1, "correction")).StatusCode);
    }
}

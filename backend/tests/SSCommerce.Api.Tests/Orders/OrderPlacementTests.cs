using System.Net;
using System.Text.Json;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Inventory;

namespace SSCommerce.Api.Tests.Orders;

/// <summary>
/// POST /orders mutlu yol ve veri bütünlüğü: stok düşümü, defter (hareket), dağıtım, anlık görüntüler, sepet temizliği, geçmiş/ayrıntı,
/// çok depolu bölünme ve "sonradan değişiklik siparişi değiştirmez". Her test kendi verisini kurar (isolation).
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class OrderPlacementTests(InventoryApiFixture fixture)
{
    [Fact]
    public async Task A_valid_cart_and_address_make_a_201_order_with_server_values_unpaid_and_no_shipping_or_grand_total()
    {
        var (variant, warehouse, sku) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 3);

        var response = await shopper.PlaceAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var order = await response.JsonAsync();
        var root = order.RootElement;
        Assert.Equal("placed", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("replayed").GetBoolean());
        Assert.True(root.GetProperty("canCancel").GetBoolean());
        Assert.Matches("^SS-[0-9]{6,}$", root.GetProperty("orderNumber").GetString()!);
        Assert.Equal("TRY", root.GetProperty("currency").GetString());
        Assert.Equal(3, root.GetProperty("itemCount").GetInt32());
        Assert.Equal(30_000, root.GetProperty("subtotalMinor").GetInt64()); // 3 x 100,00 TL: kuruş
        Assert.Equal(JsonValueKind.Null, root.GetProperty("shippingMinor").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("grandTotalMinor").ValueKind);
        Assert.Equal("shippingNotCalculated", root.GetProperty("shippingStatus").GetString());
        Assert.False(root.TryGetProperty("paid", out _));
        Assert.False(root.TryGetProperty("userId", out _));
        Assert.False(root.TryGetProperty("allocations", out _)); // depo bilgisi müşteriye çıkmaz

        var item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(variant, item.GetProperty("variantId").GetGuid());
        Assert.Equal(sku, item.GetProperty("sku").GetString());
        Assert.Equal(10_000, item.GetProperty("unitPriceMinor").GetInt64());
        Assert.Equal(3, item.GetProperty("quantity").GetInt32());
        Assert.Equal(30_000, item.GetProperty("lineTotalMinor").GetInt64());

        var address = root.GetProperty("deliveryAddress");
        Assert.Equal("Ayşe Yılmaz", address.GetProperty("recipientName").GetString());
        Assert.Equal("5321234567", address.GetProperty("phone").GetString()); // sipariş kopyası normalize telefondur

        var history = Assert.Single(root.GetProperty("statusHistory").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, history.GetProperty("fromStatus").ValueKind);
        Assert.Equal("placed", history.GetProperty("toStatus").GetString());
        Assert.False(history.TryGetProperty("actorUserId", out _));
        Assert.False(history.TryGetProperty("actorKind", out _));
        Assert.False(history.TryGetProperty("reason", out _));

        // Stok: 10 -> 7, tek düşüm hareketi, tek dağıtım; sepet temizlendi ama sepet satırı durur.
        Assert.Equal(7, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.MovementCountAsync(variant));
        Assert.Equal(-3, await fixture.MovementDeltaSumAsync(variant));
        Assert.Equal(0, await fixture.LineCountAsync(shopper.UserId));
        Assert.Equal(1, await fixture.CartCountAsync(shopper.UserId));
        using var cart = await (await shopper.Client.GetCartAsync()).JsonAsync();
        Assert.Equal(0, cart.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task The_placement_movement_is_the_order_ledger_entry_with_the_key_derived_from_the_persisted_allocation_id()
    {
        var (variant, warehouse, sku) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 4);

        var response = await shopper.PlaceAsync();
        using var order = await response.JsonAsync();
        var orderId = order.RootElement.GetProperty("id").GetGuid();
        var number = order.RootElement.GetProperty("orderNumber").GetString();

        var allocationId = await fixture.ScalarAsync<Guid>(
            $"SELECT a.id FROM order_inventory_allocations a JOIN order_items i ON i.id = a.order_item_id WHERE i.order_id = '{orderId}'");
        var key = $"order-place-{allocationId:N}";
        // Anahtar KALICI dağıtım kimliğinden türemeli (EF üzerinden Added varlığa kimlik atamasının doğru kaydedildiğinin kanıtı).
        Assert.Equal(1, await fixture.MovementKeyCountAsync(key));
        Assert.Equal(
            1,
            await fixture.ScalarAsync<long>(
                $"""
                SELECT count(*) FROM order_inventory_allocations a JOIN stock_movements m ON m.id = a.placement_movement_id
                WHERE a.id = '{allocationId}' AND m.operation_key = '{key}' AND m.operation_type = 'order_placement'
                  AND m.quantity_delta = -4 AND m.warehouse_id = '{warehouse}' AND m.product_variant_id = '{variant}' AND m.sku = '{sku}'
                  AND m.reference = '{number}' AND m.actor_user_id = '{shopper.UserId}' AND m.on_hand_after = 6
                """));
        Assert.Equal(shopper.UserId, await fixture.MovementActorAsync(key));
    }

    [Fact]
    public async Task A_line_splits_across_warehouses_in_code_order_and_balances_reconcile_with_the_ledger()
    {
        var (variant, a, b, _) = await fixture.SplitVariantAsync(stockA: 2, stockB: 5);
        var shopper = await fixture.ShopperAsync(variant, 4);

        var response = await shopper.PlaceAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var orderId = await response.IdOfAsync();
        Assert.Equal(0, await fixture.OnHandAsync(a, variant));  // önce A boşalır
        Assert.Equal(3, await fixture.OnHandAsync(b, variant));  // kalan 2, B'den
        Assert.Equal(2, await fixture.AllocationCountAsync(orderId));
        Assert.Equal(2, await fixture.MovementsOfOrderAsync(orderId, "order_placement"));
        Assert.Equal(
            2,
            await fixture.ScalarAsync<long>(
                $"SELECT a.quantity FROM order_inventory_allocations a JOIN order_items i ON i.id = a.order_item_id WHERE i.order_id = '{orderId}' AND a.warehouse_id = '{a}'"));
        // Depo toplamları = tohum - düşümler: her bakiye için on_hand = ilk stok + o depodaki hareket toplamı.
        Assert.Equal(-2, await fixture.ScalarAsync<long>($"SELECT sum(quantity_delta) FROM stock_movements WHERE warehouse_id = '{a}' AND product_variant_id = '{variant}'"));
        Assert.Equal(-2, await fixture.ScalarAsync<long>($"SELECT sum(quantity_delta) FROM stock_movements WHERE warehouse_id = '{b}' AND product_variant_id = '{variant}'"));
        Assert.Equal(7 - 4, await fixture.OnHandSumAsync(variant));
        Assert.Equal(-4, await fixture.MovementDeltaSumAsync(variant));
        // Müşteri tek kalem görür (bölünme sızmaz).
        using var json = await (await shopper.Client.GetOrderAsync(orderId)).JsonAsync();
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(4, item.GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task Order_invariants_hold_in_the_database_subtotal_equals_the_sum_of_lines_and_each_allocation_matches_its_item_variant()
    {
        var (v1, _, _) = await fixture.StockedVariantAsync(10);
        var (v2, _, _) = await fixture.StockedVariantAsync(10);
        await fixture.SetPriceAsync(v2, 12.34m);
        var shopper = await fixture.ShopperAsync([(v1, 2), (v2, 3)]);

        var response = await shopper.PlaceAsync();
        var orderId = await response.IdOfAsync();

        Assert.Equal(
            1,
            await fixture.ScalarAsync<long>(
                $"SELECT count(*) FROM orders o WHERE o.id = '{orderId}' AND o.item_subtotal = (SELECT sum(unit_price * quantity) FROM order_items WHERE order_id = o.id) AND o.item_subtotal = 237.02"));
        Assert.Equal(0, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM order_inventory_allocations a JOIN order_items i ON i.id = a.order_item_id WHERE i.order_id = '{orderId}' AND a.product_variant_id <> i.product_variant_id"));
        Assert.Equal(0, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM order_items i WHERE i.order_id = '{orderId}' AND i.quantity <> (SELECT sum(quantity) FROM order_inventory_allocations WHERE order_item_id = i.id)"));
        using var json = await (await shopper.Client.GetOrderAsync(orderId)).JsonAsync();
        Assert.Equal(23_702, json.RootElement.GetProperty("subtotalMinor").GetInt64());
        Assert.Equal(5, json.RootElement.GetProperty("itemCount").GetInt32());
        Assert.Equal(23_702, json.RootElement.GetProperty("items").EnumerateArray().Sum(i => i.GetProperty("lineTotalMinor").GetInt64()));
    }

    [Fact]
    public async Task History_and_detail_show_the_same_order_newest_first_with_a_bounded_preview()
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(20);
        var customer = await fixture.CustomerAsync();
        var address = await customer.CreateAddressAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            await customer.AddToCartAsync(variant, i + 1);
            var placed = await customer.PlaceAsync(address, await customer.QuoteAsync(address));
            Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
            ids.Add(await placed.IdOfAsync());
        }

        using var list = await (await customer.GetAsync($"{OrderKit.OrdersUrl}?pageSize=2")).JsonAsync();
        var items = list.RootElement.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(3, list.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(2, list.RootElement.GetProperty("totalPages").GetInt32());
        Assert.Equal([ids[2], ids[1]], items.Select(i => i.GetProperty("id").GetGuid()).ToArray()); // en yeni önce
        Assert.Equal(3, items[0].GetProperty("itemCount").GetInt32());
        Assert.True(items[0].GetProperty("canCancel").GetBoolean());
        Assert.Equal(1, items[0].GetProperty("previewItems").GetArrayLength());
        Assert.False(items[0].TryGetProperty("deliveryAddress", out _)); // liste adres taşımaz

        using var second = await (await customer.GetAsync($"{OrderKit.OrdersUrl}?pageSize=2&page=2")).JsonAsync();
        Assert.Equal(ids[0], Assert.Single(second.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        using var detail = await (await customer.GetOrderAsync(ids[1])).JsonAsync();
        Assert.Equal(ids[1], detail.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(2, detail.RootElement.GetProperty("itemCount").GetInt32());
        Assert.False(detail.RootElement.GetProperty("replayed").GetBoolean());
    }

    [Fact]
    public async Task Later_address_edits_address_deletion_and_price_changes_do_not_alter_the_placed_order()
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 2);
        var placed = await shopper.PlaceAsync();
        var orderId = await placed.IdOfAsync();
        var before = await (await shopper.Client.GetOrderAsync(orderId)).TextAsync();

        Assert.Equal(
            HttpStatusCode.OK,
            (await shopper.Client.PutAsync($"{CartKit.AddressesUrl}/{shopper.AddressId}", CartKit.ValidAddress("Yeni etiket", phone: "05449998877", postalCode: null))).StatusCode);
        await fixture.SetPriceAsync(variant, 999.99m);
        Assert.Equal(HttpStatusCode.NoContent, (await shopper.Client.DeleteAsync($"{CartKit.AddressesUrl}/{shopper.AddressId}")).StatusCode);

        var after = await (await shopper.Client.GetOrderAsync(orderId)).TextAsync();
        Assert.Equal(before, after); // bayt düzeyinde aynı: adres kopyası, birim fiyat, toplam, geçmiş
        using var json = JsonDocument.Parse(after);
        Assert.Equal(20_000, json.RootElement.GetProperty("subtotalMinor").GetInt64());
        Assert.Equal("Ev", json.RootElement.GetProperty("deliveryAddress").GetProperty("label").GetString());
    }

    [Fact]
    public async Task Unknown_fields_userId_priceMinor_status_and_paid_in_the_body_have_no_effect()
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var victim = await fixture.CustomerAsync();
        var shopper = await fixture.ShopperAsync(variant, 2);
        var body = JsonSerializer.Serialize(new
        {
            addressId = shopper.AddressId,
            quote = shopper.Quote,
            userId = victim.UserId,
            priceMinor = 1,
            unitPriceMinor = 1,
            subtotalMinor = 1,
            status = "completed",
            paid = true,
            shippingMinor = 5000,
            grandTotalMinor = 25_000,
            items = new[] { new { variantId = variant, quantity = 1, priceMinor = 1 } },
        });

        var response = await shopper.Client.SendAsync(HttpMethod.Post, OrderKit.OrdersUrl, body, OrderKit.NewKey());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = await response.JsonAsync();
        var root = json.RootElement;
        Assert.Equal("placed", root.GetProperty("status").GetString());
        Assert.Equal(20_000, root.GetProperty("subtotalMinor").GetInt64());
        Assert.Equal(2, root.GetProperty("itemCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("shippingMinor").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("grandTotalMinor").ValueKind);
        Assert.False(root.TryGetProperty("paid", out _));
        Assert.Equal(1, await fixture.OrderCountAsync(shopper.UserId));
        Assert.Equal(0, await fixture.OrderCountAsync(victim.UserId)); // sahip yalnız oturumdur
    }

    [Fact]
    public async Task The_primary_image_is_snapshotted_into_the_order_item_and_survives_the_image_being_replaced()
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var storageKey = $"/images/urun/siparis-{Guid.NewGuid():N}.webp";
        await fixture.ExecuteAsync(
            $"INSERT INTO product_images (id, product_id, storage_key, alt_text, sort_order, is_primary, created_at, updated_at) " +
            $"SELECT '{Guid.CreateVersion7()}', product_id, '{storageKey}', 'Siparis gorseli', 0, true, now(), now() FROM product_variants WHERE id = '{variant}'");
        var shopper = await fixture.ShopperAsync(variant, 1);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();

        await fixture.ExecuteAsync(
            $"UPDATE product_images SET storage_key = '/images/urun/yeni-{Guid.NewGuid():N}.webp', alt_text = 'Degisti' WHERE storage_key = '{storageKey}'");

        using var json = await (await shopper.Client.GetOrderAsync(orderId)).JsonAsync();
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(storageKey, item.GetProperty("imageUrl").GetString());
        Assert.Equal("Siparis gorseli", item.GetProperty("imageAlt").GetString());
    }

    [Fact]
    public async Task Stock_in_an_inactive_warehouse_is_never_sold_and_empty_balance_rows_are_skipped()
    {
        var (variant, activeEmpty, inactiveFull, _) = await fixture.SplitVariantAsync(stockA: 0, stockB: 0);
        await fixture.SeedBalanceAsync(activeEmpty, variant, 0);   // boş ama var olan satır: kilitlenir, dağıtımda atlanır
        await fixture.SeedBalanceAsync(inactiveFull, variant, 9);
        await fixture.ExecuteAsync($"UPDATE warehouses SET is_active = false WHERE id = '{inactiveFull}'");
        var activeFull = await fixture.CreateWarehouseAsync();
        await fixture.SeedBalanceAsync(activeFull, variant, 5);
        var shopper = await fixture.ShopperAsync(variant, 3); // sepete eklenebilsin diye aktif depoda stok varken
        await fixture.SetStockAsync(activeFull, variant, 0);

        // Yalnız pasif depoda stok var: satılamaz.
        await ApiAssert.ProblemAsync(await shopper.PlaceAsync(), 409, "insufficient_stock");
        Assert.Equal(9, await fixture.OnHandAsync(inactiveFull, variant));
        Assert.Equal(0, await fixture.MovementCountAsync(variant));

        // Aktif depoya stok dönünce yalnız o depodan düşer.
        await fixture.SetStockAsync(activeFull, variant, 5);
        var response = await shopper.PlaceAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, await fixture.OnHandAsync(activeFull, variant));
        Assert.Equal(9, await fixture.OnHandAsync(inactiveFull, variant));
        Assert.Equal(0, await fixture.OnHandAsync(activeEmpty, variant));
        Assert.Equal(1, await fixture.AllocationCountAsync(await response.IdOfAsync()));
    }

    // ---------- Reddedilen her istek: hiçbir şey yazılmaz ----------

    private async Task AssertRejectedWithoutWritesAsync(
        Shopper shopper,
        Guid[] variants,
        Func<Task<HttpResponseMessage>> send,
        int status,
        string code)
    {
        var before = await fixture.StateAsync(shopper.UserId, variants);

        var response = await send();

        await ApiAssert.ProblemAsync(response, status, code);
        Assert.Equal(before, await fixture.StateAsync(shopper.UserId, variants));
    }

    public static TheoryData<string, string?, string?> InvalidInputs => new()
    {
        { "missing key", null, """{"addressId":"{A}","quote":"{Q}"}""" },
        { "key too short", "short-key", """{"addressId":"{A}","quote":"{Q}"}""" },
        { "key bad chars", "key with spaces and ! 123456", """{"addressId":"{A}","quote":"{Q}"}""" },
        { "key too long", new string('a', 129), """{"addressId":"{A}","quote":"{Q}"}""" },
        { "address missing", "k-valid-key-12345678", """{"quote":"{Q}"}""" },
        { "address empty guid", "k-valid-key-12345678", """{"addressId":"00000000-0000-0000-0000-000000000000","quote":"{Q}"}""" },
        { "address not a guid", "k-valid-key-12345678", """{"addressId":"nope","quote":"{Q}"}""" },
        { "address wrong type", "k-valid-key-12345678", """{"addressId":123,"quote":"{Q}"}""" },
        { "quote missing", "k-valid-key-12345678", """{"addressId":"{A}"}""" },
        { "quote uppercase", "k-valid-key-12345678", """{"addressId":"{A}","quote":"ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789"}""" },
        { "quote short", "k-valid-key-12345678", """{"addressId":"{A}","quote":"abc"}""" },
        { "quote non hex", "k-valid-key-12345678", """{"addressId":"{A}","quote":"zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"}""" },
        { "quote wrong type", "k-valid-key-12345678", """{"addressId":"{A}","quote":42}""" },
        { "empty body", "k-valid-key-12345678", "" },
        { "broken json", "k-valid-key-12345678", "{" },
        { "array body", "k-valid-key-12345678", "[]" },
        { "null body", "k-valid-key-12345678", "null" },
    };

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public async Task Invalid_input_gets_400_validation_failed_and_writes_nothing(string name, string? key, string? template)
    {
        Assert.False(string.IsNullOrEmpty(name)); // senaryo adı rapordaki satır etiketidir
        var (variant, _, _) = await fixture.StockedVariantAsync(5);
        var shopper = await fixture.ShopperAsync(variant, 1);
        var body = template!.Replace("{A}", shopper.AddressId.ToString()).Replace("{Q}", shopper.Quote);

        await AssertRejectedWithoutWritesAsync(
            shopper,
            [variant],
            () => shopper.Client.SendAsync(HttpMethod.Post, OrderKit.OrdersUrl, body, key),
            400,
            "validation_failed");
    }

    [Fact]
    public async Task Two_idempotency_key_headers_and_an_oversized_body_are_400_and_write_nothing()
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(5);
        var shopper = await fixture.ShopperAsync(variant, 1);
        var good = $$"""{"addressId":"{{shopper.AddressId}}","quote":"{{shopper.Quote}}"}""";
        var padded = $$"""{"addressId":"{{shopper.AddressId}}","quote":"{{shopper.Quote}}","pad":"{{new string('x', 17 * 1024)}}"}""";

        await AssertRejectedWithoutWritesAsync(
            shopper,
            [variant],
            () => shopper.Client.SendAsync(HttpMethod.Post, OrderKit.OrdersUrl, good, "ok-first-key-12345678, ok-second-key-1234567"),
            400,
            "validation_failed");
        await AssertRejectedWithoutWritesAsync(
            shopper, [variant], () => shopper.Client.SendAsync(HttpMethod.Post, OrderKit.OrdersUrl, padded, OrderKit.NewKey()), 400, "validation_failed");
        await AssertRejectedWithoutWritesAsync(
            shopper, [variant], () => shopper.Client.SendUnknownLengthAsync(OrderKit.OrdersUrl, padded, OrderKit.NewKey()), 400, "validation_failed");
        await AssertRejectedWithoutWritesAsync(
            shopper,
            [variant],
            () => shopper.Client.SendAsync(HttpMethod.Post, OrderKit.OrdersUrl, good, OrderKit.NewKey(), contentType: "text/plain"),
            415,
            "unsupported_media_type");
    }

    [Fact]
    public async Task An_empty_cart_and_a_user_without_any_cart_get_409_cart_empty()
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(5);
        var shopper = await fixture.ShopperAsync(variant, 1);
        var noCart = await fixture.CustomerAsync();
        var noCartAddress = await noCart.CreateAddressAsync();
        Assert.Equal(HttpStatusCode.OK, (await shopper.Client.RemoveItemAsync(await shopper.Client.ItemIdAsync(variant))).StatusCode); // sepet satırı var, satır yok

        await AssertRejectedWithoutWritesAsync(shopper, [variant], () => shopper.PlaceAsync(), 409, "cart_empty");

        var before = await fixture.OrderCountAsync(noCart.UserId);
        await ApiAssert.ProblemAsync(await noCart.PlaceAsync(noCartAddress, new string('a', 64)), 409, "cart_empty");
        Assert.Equal(before, await fixture.OrderCountAsync(noCart.UserId));
        Assert.Equal(0, await fixture.CartCountAsync(noCart.UserId)); // reddedilen istek sepet bile yaratmadı
    }

    [Fact]
    public async Task A_missing_address_and_another_users_address_get_the_same_404_address_not_found()
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(5);
        var shopper = await fixture.ShopperAsync(variant, 1);
        var stranger = await fixture.CustomerAsync();
        var strangerAddress = await stranger.CreateAddressAsync();

        var missing = await shopper.Client.PlaceAsync(Guid.NewGuid(), shopper.Quote);
        var foreign = await shopper.Client.PlaceAsync(strangerAddress, shopper.Quote);

        await ApiAssert.ProblemAsync(missing, 404, "address_not_found");
        await ApiAssert.ProblemAsync(foreign, 404, "address_not_found");
        Assert.Equal(await missing.BodyWithoutTraceIdAsync(), await foreign.BodyWithoutTraceIdAsync());
        Assert.Equal(0, await fixture.OrderCountAsync(shopper.UserId));
        Assert.Equal(1, await fixture.LineCountAsync(shopper.UserId));
        Assert.Equal(5, await fixture.OnHandSumAsync(variant));
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
    }

    [Theory]
    [InlineData("variant")]
    [InlineData("product")]
    [InlineData("category")]
    public async Task An_inactive_variant_product_or_category_gets_409_order_item_inactive_and_writes_nothing(string which)
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(5);
        var (other, _, _) = await fixture.StockedVariantAsync(5);
        var shopper = await fixture.ShopperAsync([(variant, 1), (other, 1)]); // quote, pasif olmadan önce alındı
        var itemId = await shopper.Client.ItemIdAsync(variant);
        await (which switch
        {
            "variant" => fixture.SetVariantActiveAsync(variant, false),
            "product" => fixture.SetProductActiveOfVariantAsync(variant, false),
            _ => fixture.SetCategoryActiveOfVariantAsync(variant, false),
        });
        var before = await fixture.StateAsync(shopper.UserId, variant, other);

        var response = await shopper.PlaceAsync();

        await ApiAssert.ProblemAsync(response, 409, "order_item_inactive");
        using var json = await response.JsonAsync();
        Assert.Equal([itemId], json.RootElement.GetProperty("itemIds").EnumerateArray().Select(e => e.GetGuid()).ToArray());
        Assert.Equal(before, await fixture.StateAsync(shopper.UserId, variant, other));
    }

    [Fact]
    public async Task Insufficient_stock_lists_every_short_line_and_writes_nothing_even_for_the_lines_that_would_have_fit()
    {
        var (fits, _, _) = await fixture.StockedVariantAsync(10);
        var (shortA, shortWarehouseA, _) = await fixture.StockedVariantAsync(5);
        var (shortB, shortWarehouseB, _) = await fixture.StockedVariantAsync(5);
        var shopper = await fixture.ShopperAsync([(fits, 2), (shortA, 3), (shortB, 4)]);
        await fixture.SetStockAsync(shortWarehouseA, shortA, 2);
        await fixture.SetStockAsync(shortWarehouseB, shortB, 0);
        var before = await fixture.StateAsync(shopper.UserId, fits, shortA, shortB);

        var response = await shopper.PlaceAsync();

        await ApiAssert.ProblemAsync(response, 409, "insufficient_stock");
        using var json = await response.JsonAsync();
        var expected = new[] { await shopper.Client.ItemIdAsync(shortA), await shopper.Client.ItemIdAsync(shortB) }.Order().ToArray();
        Assert.Equal(expected, json.RootElement.GetProperty("itemIds").EnumerateArray().Select(e => e.GetGuid()).Order().ToArray());
        Assert.Equal(before, await fixture.StateAsync(shopper.UserId, fits, shortA, shortB));
        Assert.Equal(10, await fixture.OnHandSumAsync(fits)); // sığan satır da düşülmedi
    }

    [Fact]
    public async Task A_price_change_after_the_quote_gets_409_order_quote_changed_and_a_fresh_quote_then_succeeds()
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(5);
        var shopper = await fixture.ShopperAsync(variant, 1);
        await fixture.SetPriceAsync(variant, 101.00m);

        await AssertRejectedWithoutWritesAsync(shopper, [variant], () => shopper.PlaceAsync(), 409, "order_quote_changed");

        var fresh = await shopper.Client.QuoteAsync(shopper.AddressId);
        var retry = await shopper.Client.PlaceAsync(shopper.AddressId, fresh);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        using var json = await retry.JsonAsync();
        Assert.Equal(10_100, json.RootElement.GetProperty("subtotalMinor").GetInt64());
    }

    [Fact]
    public async Task Offsetting_price_changes_that_keep_the_subtotal_equal_still_change_the_quote_and_are_rejected()
    {
        var (a, _, _) = await fixture.StockedVariantAsync(5);
        var (b, _, _) = await fixture.StockedVariantAsync(5);
        var shopper = await fixture.ShopperAsync([(a, 1), (b, 1)]); // 100 + 100 = 200,00
        await fixture.SetPriceAsync(a, 110.00m);
        await fixture.SetPriceAsync(b, 90.00m);                      // toplam yine 200,00
        using (var preview = await (await shopper.Client.PreviewAsync(shopper.AddressId)).JsonAsync())
        {
            Assert.Equal(20_000, preview.RootElement.GetProperty("subtotalMinor").GetInt64());
            Assert.NotEqual(shopper.Quote, preview.RootElement.GetProperty("quote").GetString()); // çıplak toplam karşılaştırması bunu kaçırırdı
        }

        await AssertRejectedWithoutWritesAsync(shopper, [a, b], () => shopper.PlaceAsync(), 409, "order_quote_changed");
    }

    [Fact]
    public async Task A_tampered_or_random_quote_is_rejected_a_quote_from_another_cart_too()
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(5);
        var shopper = await fixture.ShopperAsync(variant, 2);
        var other = await fixture.ShopperAsync(variant, 1);
        var flipped = (shopper.Quote[0] == '0' ? "1" : "0") + shopper.Quote[1..];

        foreach (var quote in new[] { new string('0', 64), flipped, other.Quote })
        {
            await AssertRejectedWithoutWritesAsync(
                shopper, [variant], () => shopper.Client.PlaceAsync(shopper.AddressId, quote), 409, "order_quote_changed");
        }
    }

    [Fact]
    public async Task A_total_beyond_the_money_limit_gets_409_cart_total_too_large_and_writes_nothing()
    {
        var customer = await fixture.CustomerAsync();
        var address = await customer.CreateAddressAsync();
        var (a, _, _) = await fixture.StockedVariantAsync(3);
        var (b, _, _) = await fixture.StockedVariantAsync(3);
        await fixture.SetPriceAsync(a, 50_000_000_000_000m);
        await fixture.SetPriceAsync(b, 50_000_000_000_000m);
        await fixture.SeedCartLinesAsync(customer.UserId, [a, b]);
        var before = await fixture.StateAsync(customer.UserId, a, b);

        await ApiAssert.ProblemAsync(await customer.PlaceAsync(address, new string('a', 64)), 409, "cart_total_too_large");

        Assert.Equal(before, await fixture.StateAsync(customer.UserId, a, b));
    }

    [Fact]
    public async Task The_rejected_attempt_does_not_consume_the_key_and_the_same_key_then_places_the_order_once_fixed()
    {
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(5);
        var shopper = await fixture.ShopperAsync(variant, 2);
        var key = OrderKit.NewKey();
        await fixture.SetStockAsync(warehouse, variant, 1);
        await ApiAssert.ProblemAsync(await shopper.PlaceAsync(key), 409, "insufficient_stock");
        Assert.Equal(0, await fixture.OrderCountAsync(shopper.UserId));

        await fixture.SetStockAsync(warehouse, variant, 5);
        var retry = await shopper.PlaceAsync(key);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode); // 201, replay değil: ret anahtarı tüketmedi
        using var json = await retry.JsonAsync();
        Assert.False(json.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(3, await fixture.OnHandAsync(warehouse, variant));
    }
}

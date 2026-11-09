using System.Net;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Inventory;

namespace SSCommerce.Api.Tests.Orders;

/// <summary>
/// İDEMPOTENCY ve YARIŞ testleri: gerçek paralel HTTP istekleri, gerçek PostgreSQL. Kanıtlanan şeyler: aynı (kullanıcı, anahtar) tek sipariştir
/// (advisory kilit), son birim için yarışan kullanıcılardan en çok biri kazanır (bakiye FOR UPDATE), çok depolu siparişler tek kilit
/// sırasıyla kilitlendiğinden deadlock olmaz, iptal-iptal ve iptal-sevkiyat <c>orders</c> satır kilidinde sıraya girer.
/// Yarış testlerinde sonuç dağılımı ZAMANLAMAYA bağlı olabilir; bu yüzden her zaman DEĞİŞMEZLER (toplamlar, tekillik, negatif olmama) doğrulanır.
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class OrderConcurrencyTests(InventoryApiFixture fixture)
{
    // ---------- Idempotency ----------

    [Fact]
    public async Task Twenty_four_parallel_requests_with_one_key_make_exactly_one_order_one_set_of_movements_one_cart_clear_and_replay_the_rest()
    {
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 3);
        var key = OrderKit.NewKey();

        var results = await OrderKit.RunParallelAsync(24, _ => shopper.PlaceAsync(key));

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(23, results.Count(r => r.Status == HttpStatusCode.OK));
        var ids = results.Select(r => r.Body.RootElement.GetProperty("id").GetGuid()).Distinct().ToList();
        var orderId = Assert.Single(ids); // hepsi AYNI siparişi gösterir
        Assert.Equal(1, results.Count(r => !r.Body.RootElement.GetProperty("replayed").GetBoolean()));
        Assert.All(results, r => Assert.Equal("placed", r.Body.RootElement.GetProperty("status").GetString()));
        Assert.Equal(1, await fixture.OrderCountAsync(shopper.UserId));
        Assert.Equal(1, await fixture.AllocationCountAsync(orderId));
        Assert.Equal(1, await fixture.MovementCountAsync(variant));
        Assert.Equal(7, await fixture.OnHandAsync(warehouse, variant)); // tek düşüm
        Assert.Equal(0, await fixture.LineCountAsync(shopper.UserId));
        Assert.Equal(1, await fixture.HistoryCountAsync(orderId));
    }

    [Fact]
    public async Task The_same_key_with_a_different_address_or_quote_is_409_idempotency_key_reused_and_never_exposes_the_first_order()
    {
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 1);
        var key = OrderKit.NewKey();
        var first = await shopper.PlaceAsync(key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = await first.IdOfAsync();
        var otherAddress = await shopper.Client.CreateAddressAsync("Is");
        await shopper.Client.AddToCartAsync(variant, 1);
        var freshQuote = await shopper.Client.QuoteAsync(shopper.AddressId);

        var differentAddress = await shopper.Client.PlaceAsync(otherAddress, shopper.Quote, key);
        var differentQuote = await shopper.Client.PlaceAsync(shopper.AddressId, new string('d', 64), key);

        foreach (var response in new[] { differentAddress, differentQuote })
        {
            await ApiAssert.ProblemAsync(response, 409, "idempotency_key_reused");
            Assert.DoesNotContain(firstId.ToString(), await response.TextAsync());
        }

        Assert.Equal(1, await fixture.OrderCountAsync(shopper.UserId));
        Assert.Equal(9, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.LineCountAsync(shopper.UserId)); // sonraki sepet dokunulmadı
        // Aynı anahtarla ASIL istek hâlâ replay'dir (sepet dolu olsa, quote değişse de).
        var replay = await shopper.PlaceAsync(key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstId, await replay.IdOfAsync());
        Assert.NotEmpty(freshQuote);
    }

    [Fact]
    public async Task The_same_key_from_another_user_is_independent_and_never_replays_or_exposes_the_first_users_order()
    {
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);
        var alice = await fixture.ShopperAsync(variant, 1);
        var bob = await fixture.ShopperAsync(variant, 2);
        var key = OrderKit.NewKey();
        var aliceOrder = await alice.PlaceAsync(key);
        Assert.Equal(HttpStatusCode.Created, aliceOrder.StatusCode);
        var aliceId = await aliceOrder.IdOfAsync();

        // Bob, Alice'in gövdesini (adres + quote) kendi isteğiymiş gibi aynı anahtarla gönderir: Alice'in siparişi dönmez.
        var steal = await bob.Client.PlaceAsync(alice.AddressId, alice.Quote, key);
        await ApiAssert.ProblemAsync(steal, 404, "address_not_found");
        Assert.DoesNotContain(aliceId.ToString(), await steal.TextAsync());

        // Bob'un kendi isteği aynı anahtarla bağımsız yeni bir siparişi oluşturur.
        var bobOrder = await bob.PlaceAsync(key);
        Assert.Equal(HttpStatusCode.Created, bobOrder.StatusCode);
        var bobId = await bobOrder.IdOfAsync();
        Assert.NotEqual(aliceId, bobId);
        Assert.Equal(1, await fixture.OrderCountAsync(alice.UserId));
        Assert.Equal(1, await fixture.OrderCountAsync(bob.UserId));
        Assert.Equal(7, await fixture.OnHandAsync(warehouse, variant));
        await ApiAssert.ProblemAsync(await bob.Client.GetOrderAsync(aliceId), 404, "order_not_found");
        await ApiAssert.ProblemAsync(await alice.Client.GetOrderAsync(bobId), 404, "order_not_found");

        // Paralel: aynı anahtar, iki kullanıcı => iki ayrı sipariş (kilit anahtarı kullanıcıyı içerir).
        var (variant2, _, _) = await fixture.StockedVariantAsync(10);
        var c = await fixture.ShopperAsync(variant2, 1);
        var d = await fixture.ShopperAsync(variant2, 1);
        var shared = OrderKit.NewKey();
        var both = await OrderKit.RunParallelAsync(2, i => i == 0 ? c.PlaceAsync(shared) : d.PlaceAsync(shared));
        Assert.All(both, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        Assert.NotEqual(both[0].Body.RootElement.GetProperty("id").GetGuid(), both[1].Body.RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Parallel_requests_from_one_user_with_DIFFERENT_keys_on_one_cart_make_exactly_one_order_the_rest_find_an_empty_cart()
    {
        // Aynı kullanıcı, aynı sepet, her istek FARKLI anahtar (çift tıklama + sekme yenileme): advisory kilit bunları sıralamaz,
        // sepet satırı FOR UPDATE sıralar. Kilit olmasa iki istek de aynı satırları okur, stok iki kez düşer, iki sipariş doğar.
        for (var round = 0; round < 4; round++)
        {
            var (variant, warehouse, _) = await fixture.StockedVariantAsync(30);
            var shopper = await fixture.ShopperAsync(variant, 5);

            var results = await OrderKit.RunParallelAsync(12, _ => shopper.PlaceAsync(OrderKit.NewKey()));

            Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
            Assert.All(
                results.Where(r => r.Status != HttpStatusCode.Created),
                r => Assert.True(r.Status == HttpStatusCode.Conflict && OrderKit.Code(r.Body) == "cart_empty", $"round {round}: {r.Status}"));
            Assert.Equal(1, await fixture.OrderCountAsync(shopper.UserId));
            Assert.Equal(25, await fixture.OnHandAsync(warehouse, variant)); // 5 adet bir kez düşüldü
            Assert.Equal(1, await fixture.MovementCountAsync(variant));
        }
    }

    // ---------- Son birim yarışı ----------

    [Fact]
    public async Task Twenty_four_users_competing_for_the_last_unit_make_at_most_one_order_stock_never_goes_negative_and_losers_keep_their_cart()
    {
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(1);
        var shoppers = new List<Shopper>();
        for (var i = 0; i < 24; i++)
        {
            shoppers.Add(await fixture.ShopperAsync(variant, 1));
        }

        var results = await OrderKit.RunParallelAsync(shoppers.Count, i => shoppers[i].PlaceAsync());

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
        var losers = results.Where(r => r.Status != HttpStatusCode.Created).ToList();
        Assert.Equal(23, losers.Count);
        Assert.All(losers, r =>
        {
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Equal("insufficient_stock", OrderKit.Code(r.Body));
        });
        Assert.Equal(0, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.MovementCountAsync(variant));
        Assert.Equal(-1, await fixture.MovementDeltaSumAsync(variant));
        var winner = Enumerable.Range(0, shoppers.Count).Single(i => results[i].Status == HttpStatusCode.Created);
        for (var i = 0; i < shoppers.Count; i++)
        {
            Assert.Equal(i == winner ? 1 : 0, await fixture.OrderCountAsync(shoppers[i].UserId));
            Assert.Equal(i == winner ? 0 : 1, await fixture.LineCountAsync(shoppers[i].UserId)); // kaybedenin sepeti korunur
        }
    }

    [Fact]
    public async Task Many_users_ordering_the_same_stock_in_parallel_sell_exactly_the_stock_and_no_more()
    {
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(7);
        var shoppers = new List<Shopper>();
        for (var i = 0; i < 20; i++)
        {
            shoppers.Add(await fixture.ShopperAsync(variant, 1));
        }

        var results = await OrderKit.RunParallelAsync(shoppers.Count, i => shoppers[i].PlaceAsync());

        Assert.Equal(7, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(13, results.Count(r => r.Status == HttpStatusCode.Conflict && OrderKit.Code(r.Body) == "insufficient_stock"));
        Assert.Equal(0, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(-7, await fixture.MovementDeltaSumAsync(variant));
    }

    [Fact]
    public async Task A_concurrent_admin_stock_adjustment_and_an_order_never_oversell_exactly_one_takes_the_stock()
    {
        var admin = await fixture.AdminAsync();
        for (var round = 0; round < 6; round++)
        {
            var (variant, a, b, _) = await fixture.SplitVariantAsync(stockA: 3, stockB: 3);
            var shopper = await fixture.ShopperAsync(variant, 6); // iki depoyu birlikte ister
            var key = InventoryApiFixture.NewKey();

            var results = await OrderKit.RunParallelAsync(
                2,
                i => i == 0 ? shopper.PlaceAsync() : admin.AdjustAsync(a, variant, -3, "adjustment", key));

            var order = results[0];
            var adjust = results[1];
            // Biri kazanır, öteki 409: 3 + 3 birimin 6'sını sipariş, 3'ünü düzeltme alır; ikisi birden alamaz.
            Assert.True(
                (order.Status == HttpStatusCode.Created && adjust.Status == HttpStatusCode.Conflict)
                || (order.Status == HttpStatusCode.Conflict && adjust.Status == HttpStatusCode.Created),
                $"round {round}: order={order.Status}, adjust={adjust.Status}");
            Assert.True(await fixture.OnHandAsync(a, variant) >= 0 && await fixture.OnHandAsync(b, variant) >= 0);
            var onHand = await fixture.OnHandSumAsync(variant);
            Assert.Equal(order.Status == HttpStatusCode.Created ? 0 : 3, onHand);
            Assert.Equal(6 + await fixture.MovementDeltaSumAsync(variant), onHand); // bakiye = tohum + defter
        }
    }

    // ---------- Deadlock yok ----------

    [Fact]
    public async Task Multi_item_multi_warehouse_orders_placed_in_opposite_cart_order_complete_without_deadlock()
    {
        for (var round = 0; round < 5; round++)
        {
            var (x, _, _, _) = await fixture.SplitVariantAsync(stockA: 4, stockB: 4);
            var (y, _, _, _) = await fixture.SplitVariantAsync(stockA: 4, stockB: 4);
            var forward = await fixture.ShopperAsync([(x, 5), (y, 5)]);   // her biri iki depoya bölünür
            var backward = await fixture.ShopperAsync([(y, 3), (x, 3)]);  // sepet sırası ters

            var results = await OrderKit.RunParallelAsync(2, i => i == 0 ? forward.PlaceAsync() : backward.PlaceAsync());

            // Yeterli stok: ikisi de başarılı olmalı (x: 5+3=8, y: 5+3=8). Deadlock olsaydı biri 500 (40P01) olurdu.
            Assert.All(results, r => Assert.True(r.Status == HttpStatusCode.Created, $"round {round}: {r.Status} {r.Body.RootElement}"));
            Assert.Equal(0, await fixture.OnHandSumAsync(x));
            Assert.Equal(0, await fixture.OnHandSumAsync(y));
        }
    }

    [Fact]
    public async Task Many_users_ordering_overlapping_multi_item_carts_in_every_direction_never_deadlock_and_never_oversell()
    {
        var variants = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            variants.Add((await fixture.SplitVariantAsync(stockA: 6, stockB: 6)).Variant);
        }

        var shoppers = new List<Shopper>();
        for (var i = 0; i < 12; i++)
        {
            // Her kullanıcı farklı bir başlangıç ve yön ile üç varyant ister.
            var order = Enumerable.Range(0, 3).Select(k => variants[(i + (i % 2 == 0 ? k : 3 - k)) % 4]).Distinct().ToList();
            shoppers.Add(await fixture.ShopperAsync(order.Select(v => (v, 2)).ToList()));
        }

        var results = await OrderKit.RunParallelAsync(shoppers.Count, i => shoppers[i].PlaceAsync());

        Assert.DoesNotContain(results, r => r.Status is HttpStatusCode.InternalServerError or HttpStatusCode.ServiceUnavailable);
        Assert.All(results, r => Assert.True(
            r.Status == HttpStatusCode.Created || (r.Status == HttpStatusCode.Conflict && OrderKit.Code(r.Body) == "insufficient_stock")));
        foreach (var variant in variants)
        {
            var sold = -await fixture.MovementDeltaSumAsync(variant);
            Assert.InRange(sold, 0, 12);
            Assert.Equal(12 - sold, await fixture.OnHandSumAsync(variant));
        }
    }

    [Fact]
    public async Task Cart_mutations_racing_a_placement_never_lose_or_duplicate_a_unit_the_quote_decides_what_was_ordered()
    {
        for (var round = 0; round < 6; round++)
        {
            var (variant, warehouse, _) = await fixture.StockedVariantAsync(20);
            var shopper = await fixture.ShopperAsync(variant, 1);

            var results = await OrderKit.RunParallelAsync(
                4,
                i => i == 0 ? shopper.PlaceAsync() : shopper.Client.AddToCartAsync(variant, 1));

            var place = results[0];
            var addsOk = results.Skip(1).Count(r => r.Status == HttpStatusCode.OK);
            Assert.All(results.Skip(1), r => Assert.Equal(HttpStatusCode.OK, r.Status));
            var ordered = place.Status == HttpStatusCode.Created
                ? place.Body.RootElement.GetProperty("itemCount").GetInt32()
                : 0;
            Assert.True(
                place.Status == HttpStatusCode.Created || (place.Status == HttpStatusCode.Conflict && OrderKit.Code(place.Body) == "order_quote_changed"),
                $"round {round}: {place.Status}");
            var inCart = await fixture.LineQuantityAsync(shopper.UserId, variant) ?? 0;
            // Sipariş verildiyse tam olarak onaylanan 1 adet; sepet de yalnız sonradan eklenenleri tutar. Hiçbir adet kaybolmaz/çoğalmaz.
            Assert.Equal(1 + addsOk, ordered + inCart);
            Assert.Equal(20 - ordered, await fixture.OnHandAsync(warehouse, variant));
            Assert.Equal(place.Status == HttpStatusCode.Created ? 1 : 0, await fixture.OrderCountAsync(shopper.UserId));
        }
    }

    // ---------- İptal yarışları ----------

    [Fact]
    public async Task Parallel_cancels_make_one_transition_and_one_reversal_per_allocation()
    {
        var (variant, a, b, _) = await fixture.SplitVariantAsync(stockA: 2, stockB: 5);
        var shopper = await fixture.ShopperAsync(variant, 4);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();
        Assert.Equal(2, await fixture.AllocationCountAsync(orderId));

        var results = await OrderKit.RunParallelAsync(12, _ => shopper.Client.CancelAsync(orderId));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Equal(1, results.Count(r => !r.Body.RootElement.GetProperty("replayed").GetBoolean()));
        Assert.Equal(2, await fixture.MovementsOfOrderAsync(orderId, "order_cancellation")); // tahsisat başına bir iade
        Assert.Equal(2, await fixture.OnHandAsync(a, variant));
        Assert.Equal(5, await fixture.OnHandAsync(b, variant));
        Assert.Equal(2, await fixture.HistoryCountAsync(orderId)); // placed, cancelled
        Assert.Equal(1, await fixture.OrderStatusCountAsync(orderId, "cancelled"));
        Assert.Equal(0, await fixture.MovementDeltaSumAsync(variant));
    }

    [Fact]
    public async Task Cancel_racing_a_staff_shipment_has_exactly_one_winner_and_the_stock_follows_the_winner()
    {
        var admin = await fixture.AdminAsync();
        var warehouseUser = await fixture.WarehouseAsync();
        for (var round = 0; round < 8; round++)
        {
            var (_, orderId, variant, warehouse) = await fixture.OrderInStateAsync("processing", admin, stock: 10, quantity: 3);
            Assert.Equal(7, await fixture.OnHandAsync(warehouse, variant));

            var results = await OrderKit.RunParallelAsync(
                2,
                i => i == 0 ? admin.StaffStatusAsync(orderId, "cancelled") : warehouseUser.StaffStatusAsync(orderId, "shipped"));

            var cancel = results[0];
            var ship = results[1];
            Assert.True(
                (cancel.Status == HttpStatusCode.OK && ship.Status == HttpStatusCode.Conflict)
                || (cancel.Status == HttpStatusCode.Conflict && ship.Status == HttpStatusCode.OK),
                $"round {round}: cancel={cancel.Status}, ship={ship.Status}");
            var loser = cancel.Status == HttpStatusCode.Conflict ? cancel : ship;
            Assert.Equal("order_transition_invalid", OrderKit.Code(loser.Body));
            if (cancel.Status == HttpStatusCode.OK)
            {
                Assert.Equal(1, await fixture.OrderStatusCountAsync(orderId, "cancelled"));
                Assert.Equal(10, await fixture.OnHandAsync(warehouse, variant));
                Assert.Equal(1, await fixture.MovementsOfOrderAsync(orderId, "order_cancellation"));
            }
            else
            {
                Assert.Equal(1, await fixture.OrderStatusCountAsync(orderId, "shipped"));
                Assert.Equal(7, await fixture.OnHandAsync(warehouse, variant)); // sevk edilen sipariş stok iade etmez
                Assert.Equal(0, await fixture.MovementsOfOrderAsync(orderId, "order_cancellation"));
            }
        }
    }

    [Fact]
    public async Task A_customer_cancel_racing_a_staff_processing_step_has_exactly_one_winner()
    {
        var admin = await fixture.AdminAsync();
        for (var round = 0; round < 8; round++)
        {
            var (shopper, orderId, variant, warehouse) = await fixture.OrderInStateAsync("placed", admin, stock: 10, quantity: 2);

            var results = await OrderKit.RunParallelAsync(
                2,
                i => i == 0 ? shopper.Client.CancelAsync(orderId) : admin.StaffStatusAsync(orderId, "processing"));

            var cancel = results[0];
            var process = results[1];
            Assert.True(
                (cancel.Status == HttpStatusCode.OK && process.Status == HttpStatusCode.Conflict)
                || (cancel.Status == HttpStatusCode.Conflict && process.Status == HttpStatusCode.OK),
                $"round {round}: cancel={cancel.Status}, process={process.Status}");
            if (cancel.Status == HttpStatusCode.OK)
            {
                Assert.Equal(10, await fixture.OnHandAsync(warehouse, variant));
                Assert.Equal(1, await fixture.OrderStatusCountAsync(orderId, "cancelled"));
            }
            else
            {
                Assert.Equal("order_transition_invalid", OrderKit.Code(cancel.Body));
                Assert.Equal(8, await fixture.OnHandAsync(warehouse, variant));
                Assert.Equal(1, await fixture.OrderStatusCountAsync(orderId, "processing"));
            }
        }
    }

    [Fact]
    public async Task Two_users_ordering_opposite_directions_while_an_admin_receives_stock_stay_consistent()
    {
        var admin = await fixture.AdminAsync();
        var (x, xa, _, _) = await fixture.SplitVariantAsync(stockA: 2, stockB: 2);
        var (y, ya, _, _) = await fixture.SplitVariantAsync(stockA: 2, stockB: 2);
        var one = await fixture.ShopperAsync([(x, 4), (y, 4)]);
        var two = await fixture.ShopperAsync([(y, 4), (x, 4)]);

        var results = await OrderKit.RunParallelAsync(
            4,
            i => i switch
            {
                0 => one.PlaceAsync(),
                1 => two.PlaceAsync(),
                2 => admin.ReceiveAsync(xa, x, 1),
                _ => admin.ReceiveAsync(ya, y, 1),
            });

        Assert.DoesNotContain(results, r => r.Status is HttpStatusCode.InternalServerError or HttpStatusCode.ServiceUnavailable);
        foreach (var variant in new[] { x, y })
        {
            var onHand = await fixture.OnHandSumAsync(variant);
            Assert.True(onHand >= 0);
            Assert.Equal(4 + await fixture.MovementDeltaSumAsync(variant), onHand);
        }
    }
}

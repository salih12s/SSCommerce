using System.Diagnostics;
using System.Net;
using Npgsql;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Orders;

/// <summary>
/// HATA ve KURTARMA: transaction'ın ortasında gerçek bir veritabanı hatası (test amaçlı trigger), kayıp yanıt, kilit zaman aşımı ve veritabanı kesintisi.
/// Her durumda bakiye, hareket, sipariş ve sepet satırı değişmez; aynı Idempotency-Key ile tekrar BAŞARILI olur (anahtar tüketilmedi).
/// Trigger'lar yalnızca bu testin KULLANICISINA uygulanır ve test bitince kaldırılır; paylaşılan izole test veritabanındaki diğer testler etkilenmez.
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class OrderFailureRecoveryTests(InventoryApiFixture fixture)
{
    private const string ForcedMessage = "test-only forced failure";

    /// <summary>Yalnız verilen kullanıcının satırlarında hata fırlatan, dispose edilince kendini kaldıran test amaçlı trigger.</summary>
    private sealed class FailureTrigger : IAsyncDisposable
    {
        private readonly InventoryApiFixture _fixture;
        private readonly string _name;
        private readonly string _table;

        private FailureTrigger(InventoryApiFixture fixture, string name, string table)
        {
            _fixture = fixture;
            _name = name;
            _table = table;
        }

        /// <param name="condition">PL/pgSQL IF koşulu (NEW/OLD kullanılabilir): doğruysa hata fırlatır.</param>
        public static async Task<FailureTrigger> InstallAsync(InventoryApiFixture fixture, string table, string timing, string whenEvent, string condition)
        {
            var name = "tf_" + Guid.NewGuid().ToString("N")[..12];
            var row = whenEvent == "DELETE" ? "OLD" : "NEW";
            await fixture.ExecuteAsync(
                $"CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $fn$ BEGIN IF {condition} THEN RAISE EXCEPTION '{ForcedMessage}' USING ERRCODE = 'P0001'; END IF; RETURN {row}; END $fn$");
            await fixture.ExecuteAsync($"CREATE TRIGGER {name} {timing} {whenEvent} ON {table} FOR EACH ROW EXECUTE FUNCTION {name}()");
            return new FailureTrigger(fixture, name, table);
        }

        public async ValueTask DisposeAsync()
        {
            await _fixture.ExecuteAsync($"DROP TRIGGER IF EXISTS {_name} ON {_table}");
            await _fixture.ExecuteAsync($"DROP FUNCTION IF EXISTS {_name}()");
        }
    }

    public static TheoryData<string> FailurePoints => new()
    {
        "order_status_history insert",       // son yazım: bakiye, sipariş, kalem, hareket, dağıtım zaten yazılmış
        "order_inventory_allocations insert", // ilk stok düşümünden sonra
        "cart_items delete",                  // sepet temizliği: en son adım
    };

    private static Task<FailureTrigger> InstallFor(InventoryApiFixture fixture, string point, Guid userId) => point switch
    {
        "order_status_history insert" => FailureTrigger.InstallAsync(
            fixture, "order_status_history", "BEFORE", "INSERT", $"EXISTS (SELECT 1 FROM orders o WHERE o.id = NEW.order_id AND o.user_id = '{userId}')"),
        "order_inventory_allocations insert" => FailureTrigger.InstallAsync(
            fixture, "order_inventory_allocations", "BEFORE", "INSERT",
            $"EXISTS (SELECT 1 FROM order_items i JOIN orders o ON o.id = i.order_id WHERE i.id = NEW.order_item_id AND o.user_id = '{userId}')"),
        _ => FailureTrigger.InstallAsync(
            fixture, "cart_items", "BEFORE", "DELETE", $"EXISTS (SELECT 1 FROM carts c WHERE c.id = OLD.cart_id AND c.user_id = '{userId}')"),
    };

    [Theory]
    [MemberData(nameof(FailurePoints))]
    public async Task A_forced_database_failure_mid_transaction_rolls_everything_back_and_the_same_key_then_succeeds(string point)
    {
        var (variant, a, b, _) = await fixture.SplitVariantAsync(stockA: 2, stockB: 5); // iki dağıtım: ilk düşümden SONRA hata
        var shopper = await fixture.ShopperAsync(variant, 4);
        var key = OrderKit.NewKey();
        var before = await fixture.StateAsync(shopper.UserId, variant);
        HttpResponseMessage failed;
        await using (await InstallFor(fixture, point, shopper.UserId))
        {
            failed = await shopper.PlaceAsync(key);
        }

        await ApiAssert.ProblemAsync(failed, 500, "internal_error");
        var text = await failed.TextAsync();
        Assert.DoesNotContain(ForcedMessage, text);
        Assert.DoesNotContain("order_status_history", text);
        Assert.Equal(before, await fixture.StateAsync(shopper.UserId, variant)); // bakiye, hareket, sipariş, sepet: aynen
        Assert.Equal(2, await fixture.OnHandAsync(a, variant));
        Assert.Equal(5, await fixture.OnHandAsync(b, variant));
        Assert.Equal(0, await fixture.OrderCountAsync(shopper.UserId));
        Assert.Equal(0, await fixture.ScalarAsync<long>($"SELECT count(*) FROM order_inventory_allocations WHERE product_variant_id = '{variant}'"));

        var retry = await shopper.PlaceAsync(key); // trigger kalktı; anahtar tüketilmemişti

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(0, await fixture.OnHandAsync(a, variant));
        Assert.Equal(3, await fixture.OnHandAsync(b, variant));
        Assert.Equal(1, await fixture.OrderCountAsync(shopper.UserId));
        Assert.Equal(0, await fixture.LineCountAsync(shopper.UserId));
        Assert.Equal(2, await fixture.MovementCountAsync(variant));
    }

    [Fact]
    public async Task A_forced_failure_while_restocking_a_cancel_rolls_back_the_restock_and_the_status_and_a_retry_restocks_once()
    {
        var (variant, a, b, _) = await fixture.SplitVariantAsync(stockA: 2, stockB: 5);
        var shopper = await fixture.ShopperAsync(variant, 4);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();
        var before = await fixture.StateAsync(shopper.UserId, variant);
        HttpResponseMessage failed;
        await using (await FailureTrigger.InstallAsync(
                         fixture, "stock_movements", "BEFORE", "INSERT", $"NEW.operation_type = 'order_cancellation' AND NEW.actor_user_id = '{shopper.UserId}'"))
        {
            failed = await shopper.Client.CancelAsync(orderId);
        }

        await ApiAssert.ProblemAsync(failed, 500, "internal_error");
        Assert.Equal(before, await fixture.StateAsync(shopper.UserId, variant)); // iade bakiyeleri geri alındı
        Assert.Equal(1, await fixture.OrderStatusCountAsync(orderId, "placed"));
        Assert.Equal(1, await fixture.HistoryCountAsync(orderId));

        Assert.Equal(HttpStatusCode.OK, (await shopper.Client.CancelAsync(orderId)).StatusCode);

        Assert.Equal(2, await fixture.OnHandAsync(a, variant));
        Assert.Equal(5, await fixture.OnHandAsync(b, variant));
        Assert.Equal(2, await fixture.MovementsOfOrderAsync(orderId, "order_cancellation"));
    }

    // ---------- Kayıp yanıt ----------

    [Fact]
    public async Task A_lost_response_after_commit_replays_the_order_with_no_duplicate_even_after_the_cart_is_empty_the_price_changed_and_the_address_is_gone()
    {
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 2);
        var key = OrderKit.NewKey();
        var first = await shopper.PlaceAsync(key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode); // istemci bu yanıtı hiç görmedi, varsayalım
        var orderId = await first.IdOfAsync();
        await fixture.SetPriceAsync(variant, 555.55m);
        Assert.Equal(HttpStatusCode.NoContent, (await shopper.Client.DeleteAsync($"{CartKit.AddressesUrl}/{shopper.AddressId}")).StatusCode);
        await fixture.SetStockAsync(warehouse, variant, 0);
        var after = await fixture.StateAsync(shopper.UserId, variant);

        var replay = await shopper.PlaceAsync(key); // aynı anahtar, aynı gövde

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var json = await replay.JsonAsync();
        Assert.Equal(orderId, json.RootElement.GetProperty("id").GetGuid());
        Assert.True(json.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(20_000, json.RootElement.GetProperty("subtotalMinor").GetInt64()); // ilk siparişin fiyatı, bugünkü değil
        Assert.Equal(after, await fixture.StateAsync(shopper.UserId, variant)); // yeni hareket, sipariş ya da düşüm yok
        Assert.Equal(1, await fixture.OrderCountAsync(shopper.UserId));
    }

    [Fact]
    public async Task A_repeated_cancel_after_a_lost_response_replays_without_a_second_restock()
    {
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 3);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();
        var first = await shopper.Client.CancelAsync(orderId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var after = await fixture.StateAsync(shopper.UserId, variant);

        var again = await shopper.Client.CancelAsync(orderId);
        var third = await shopper.Client.CancelAsync(orderId);

        foreach (var response in new[] { again, third })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = await response.JsonAsync();
            Assert.True(json.RootElement.GetProperty("replayed").GetBoolean());
            Assert.Equal("cancelled", json.RootElement.GetProperty("status").GetString());
            Assert.False(json.RootElement.GetProperty("canCancel").GetBoolean());
        }

        Assert.Equal(after, await fixture.StateAsync(shopper.UserId, variant));
        Assert.Equal(10, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.MovementsOfOrderAsync(orderId, "order_cancellation"));
        Assert.Equal(2, await fixture.HistoryCountAsync(orderId));
    }

    // ---------- Kilit zaman aşımı ----------

    private static async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> HoldAsync(InventoryApiFixture fixture, string sql)
    {
        var connection = await fixture.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var hold = new NpgsqlCommand(sql, connection, transaction);
        await hold.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return (connection, transaction);
    }

    private async Task AssertBusyThenRecoversAsync(Shopper shopper, Guid variant, string holdSql)
    {
        var key = OrderKit.NewKey();
        var before = await fixture.StateAsync(shopper.UserId, variant);
        var (connection, transaction) = await HoldAsync(fixture, holdSql);
        await using (connection)
        await using (transaction)
        {
            var stopwatch = Stopwatch.StartNew();
            var blocked = await shopper.PlaceAsync(key);
            stopwatch.Stop();

            await ApiAssert.ProblemAsync(blocked, 503, "busy");
            Assert.NotNull(blocked.Headers.CacheControl);
            Assert.InRange(stopwatch.Elapsed.TotalSeconds, 4.0, 30.0); // gerçekten 5 sn bekledi, sonsuza dek değil
            Assert.Equal(before, await fixture.StateAsync(shopper.UserId, variant)); // hiçbir şey yazılmadı
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        var retry = await shopper.PlaceAsync(key); // AYNI anahtar

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(1, await fixture.OrderCountAsync(shopper.UserId));
    }

    [Fact]
    public async Task A_placement_blocked_by_a_held_cart_row_lock_returns_503_busy_writes_nothing_and_the_same_key_succeeds_after_release()
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 1);

        await AssertBusyThenRecoversAsync(shopper, variant, $"SELECT id FROM carts WHERE user_id = '{shopper.UserId}' FOR UPDATE");
    }

    [Fact]
    public async Task A_placement_blocked_by_a_held_balance_row_lock_returns_503_busy_after_the_cart_was_locked_and_writes_nothing()
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 1);

        await AssertBusyThenRecoversAsync(shopper, variant, $"SELECT id FROM inventory_balances WHERE product_variant_id = '{variant}' FOR UPDATE");
    }

    [Fact]
    public async Task A_cancel_blocked_by_a_held_order_row_lock_returns_503_busy_changes_nothing_and_succeeds_after_release()
    {
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 2);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();
        var before = await fixture.StateAsync(shopper.UserId, variant);
        var (connection, transaction) = await HoldAsync(fixture, $"SELECT id FROM orders WHERE id = '{orderId}' FOR UPDATE");
        await using (connection)
        await using (transaction)
        {
            var stopwatch = Stopwatch.StartNew();
            var blocked = await shopper.Client.CancelAsync(orderId);
            stopwatch.Stop();

            await ApiAssert.ProblemAsync(blocked, 503, "busy");
            Assert.InRange(stopwatch.Elapsed.TotalSeconds, 4.0, 30.0);
            Assert.Equal(before, await fixture.StateAsync(shopper.UserId, variant));
            Assert.Equal(1, await fixture.OrderStatusCountAsync(orderId, "placed"));
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(HttpStatusCode.OK, (await shopper.Client.CancelAsync(orderId)).StatusCode);
        Assert.Equal(10, await fixture.OnHandAsync(warehouse, variant));
    }

    [Fact]
    public async Task A_key_whose_advisory_lock_is_held_elsewhere_returns_503_busy_for_that_key_only()
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 1);
        var other = await fixture.ShopperAsync(variant, 1);
        var key = OrderKit.NewKey();
        await using var holder = await fixture.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@key, 0))", holder))
        {
            acquire.Parameters.AddWithValue("key", $"order:{shopper.UserId:D}:{key}");
            await acquire.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();
        var blocked = await shopper.PlaceAsync(key);
        stopwatch.Stop();
        var free = await other.PlaceAsync(key); // aynı anahtar, başka kullanıcı: kilit anahtarı farklı

        await ApiAssert.ProblemAsync(blocked, 503, "busy");
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 4.0, 30.0);
        Assert.Equal(HttpStatusCode.Created, free.StatusCode);
        Assert.Equal(0, await fixture.OrderCountAsync(shopper.UserId));

        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@key, 0))", holder))
        {
            release.Parameters.AddWithValue("key", $"order:{shopper.UserId:D}:{key}");
            Assert.True((bool)(await release.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        }

        Assert.Equal(HttpStatusCode.Created, (await shopper.PlaceAsync(key)).StatusCode);
    }

    // ---------- Veritabanı kesintisi ----------

    [Fact]
    public async Task While_the_database_is_unreachable_every_order_route_returns_a_safe_503_nothing_is_written_and_the_same_key_works_when_it_returns()
    {
        var real = new NpgsqlConnectionStringBuilder(fixture.Database.ConnectionString);
        using var forwarder = new DatabaseOutageAuthenticatedTests.TcpForwarder(real.Host!, real.Port);
        var viaForwarder = new NpgsqlConnectionStringBuilder(fixture.Database.ConnectionString) { Host = "127.0.0.1", Port = forwarder.Port, Timeout = 2 };
        await using var factory = ApiFactory.Create("Development", viaForwarder.ConnectionString, extraConfiguration: InventoryApiFixture.HighLimits);
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);
        var customer = await fixture.LoginAsync(factory, AppRoles.Customer);
        var address = await customer.CreateAddressAsync();
        await customer.AddToCartAsync(variant, 2);
        var quote = await customer.QuoteAsync(address);
        var key = OrderKit.NewKey();
        var before = await fixture.StateAsync(customer.UserId, variant);

        forwarder.Close();
        await DatabaseUnavailableTests.AssertServiceUnavailableAsync(await customer.PlaceAsync(address, quote, key));
        await DatabaseUnavailableTests.AssertServiceUnavailableAsync(await customer.GetAsync(OrderKit.OrdersUrl));
        await DatabaseUnavailableTests.AssertServiceUnavailableAsync(await customer.GetOrderAsync(Guid.NewGuid()));
        await DatabaseUnavailableTests.AssertServiceUnavailableAsync(await customer.CancelAsync(Guid.NewGuid()));

        Assert.Equal(before, await fixture.StateAsync(customer.UserId, variant));
        forwarder.Resume();
        NpgsqlConnection.ClearAllPools(); // kopan havuz bağlantılarını at: yeniden deneme/uyku olmadan belirlenimli dönüş

        var retry = await customer.PlaceAsync(address, quote, key);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(8, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.OrderCountAsync(customer.UserId));
    }
}

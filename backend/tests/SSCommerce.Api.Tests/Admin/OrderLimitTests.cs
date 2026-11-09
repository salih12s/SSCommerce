using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SSCommerce.Application.Orders;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Database;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.Api.Tests.Orders;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Admin;

/// <summary>Gerçek saate eklenen kaydırma: "20 saat önce verilmiş sipariş" gibi durumlar bekleyerek değil saati oynatarak kurulur (flaky olmasın).</summary>
internal sealed class OffsetTimeProvider : TimeProvider
{
    private long _offsetTicks;

    public TimeSpan Offset
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
        set => Interlocked.Exchange(ref _offsetTicks, value.Ticks);
    }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
}

/// <summary>
/// INTEGRATION testleri: ödenmemiş sipariş sınırları (F8.5, varsayılanlar 3 açık / 24 saatte 10). Neden integration: "iki istek aynı anda 2 açık görür" yarışı yalnız
/// gerçek PostgreSQL kilitleriyle oluşur ve yalnız gerçek transaction'la çözülür. Kurallar: replay'ler hiç sayılmaz; reddedilen istek anahtar/stok/sepet tüketmez;
/// iptal edip yeniden verme döngüsü günlük sınırda durur; Retry-After en eski ilgili siparişin pencereden çıkışına göredir.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class OrderLimitTests(InventoryApiFixture fixture)
{
    private sealed record Placed(HttpResponseMessage Response, string Key, string Quote);

    private static async Task<Placed> PlaceFreshAsync(ApiClient client, Guid address, Guid variant, string? key = null)
    {
        Assert.Equal(HttpStatusCode.OK, (await client.AddToCartAsync(variant, 1)).StatusCode);
        var quote = await client.QuoteAsync(address);
        key ??= OrderKit.NewKey();
        return new Placed(await client.PlaceAsync(address, quote, key), key, quote);
    }

    private async Task<(ApiClient Client, Guid Address, Guid Variant)> UserWithStockAsync(WebApplicationFactory<Program>? factory = null)
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(200);
        var client = await fixture.CustomerAsync(factory);
        return (client, await client.CreateAddressAsync(), variant);
    }

    // ---------- Açık sipariş sınırı ----------

    [Fact]
    public async Task The_fourth_open_order_is_409_open_order_limit_with_the_limit_and_the_rejected_attempt_consumes_nothing()
    {
        var (client, address, variant) = await UserWithStockAsync();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await PlaceFreshAsync(client, address, variant)).Response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.AddToCartAsync(variant, 1)).StatusCode);
        var quote = await client.QuoteAsync(address);
        var before = await fixture.StateAsync(client.UserId, variant);
        var key = OrderKit.NewKey();

        var rejected = await client.PlaceAsync(address, quote, key);

        using var json = await rejected.JsonAsync();
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("open_order_limit", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(before, await fixture.StateAsync(client.UserId, variant)); // bakiye, hareket, sipariş sayısı, sepet satırları aynı
        Assert.Equal(0, await fixture.CountAsync($"SELECT count(*) FROM orders WHERE user_id = '{client.UserId}' AND idempotency_key = '{key}'")); // anahtar tüketilmedi
    }

    [Fact]
    public async Task Cancelling_an_open_order_frees_a_slot_and_the_same_key_then_succeeds()
    {
        var (client, address, variant) = await UserWithStockAsync();
        var first = await PlaceFreshAsync(client, address, variant);
        await PlaceFreshAsync(client, address, variant);
        await PlaceFreshAsync(client, address, variant);
        var key = OrderKit.NewKey();
        Assert.Equal(HttpStatusCode.OK, (await client.AddToCartAsync(variant, 1)).StatusCode);
        var quote = await client.QuoteAsync(address);
        await ApiAssert.ProblemAsync(await client.PlaceAsync(address, quote, key), 409, "open_order_limit");

        Assert.Equal(HttpStatusCode.OK, (await client.CancelAsync(await first.Response.IdOfAsync())).StatusCode);
        var retry = await client.PlaceAsync(address, quote, key); // aynı anahtar: reddedilen deneme anahtarı tüketmemişti

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task Forward_status_transitions_do_not_free_a_slot_until_the_order_is_completed_or_cancelled_open_means_placed_or_processing()
    {
        var admin = await fixture.AdminAsync();
        var (client, address, variant) = await UserWithStockAsync();
        var first = await PlaceFreshAsync(client, address, variant);
        await PlaceFreshAsync(client, address, variant);
        await PlaceFreshAsync(client, address, variant);
        var firstId = await first.Response.IdOfAsync();

        Assert.Equal(HttpStatusCode.OK, (await admin.StaffStatusAsync(firstId, "processing")).StatusCode);
        await ApiAssert.ProblemAsync((await PlaceFreshAsync(client, address, variant)).Response, 409, "open_order_limit"); // processing hâlâ açık

        Assert.Equal(HttpStatusCode.OK, (await admin.StaffStatusAsync(firstId, "shipped")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PlaceFreshAsync(client, address, variant)).Response.StatusCode); // shipped artık açık sayılmaz
    }

    [Fact]
    public async Task A_replay_of_an_earlier_request_over_the_limit_still_returns_200_and_is_never_counted()
    {
        var (client, address, variant) = await UserWithStockAsync();
        var first = await PlaceFreshAsync(client, address, variant);
        await PlaceFreshAsync(client, address, variant);
        await PlaceFreshAsync(client, address, variant);
        var ordersBefore = await fixture.OrderCountAsync(client.UserId);

        var replay = await client.PlaceAsync(address, first.Quote, first.Key);

        using var json = await replay.JsonAsync();
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); // sınır doluyken bile replay 200
        Assert.True(json.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(ordersBefore, await fixture.OrderCountAsync(client.UserId));
    }

    [Fact]
    public async Task Replays_are_not_counted_toward_either_limit_so_many_replays_never_block_a_new_order()
    {
        var (client, address, variant) = await UserWithStockAsync();
        var first = await PlaceFreshAsync(client, address, variant);
        for (var i = 0; i < 15; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PlaceAsync(address, first.Quote, first.Key)).StatusCode);
        }

        Assert.Equal(1, await fixture.OrderCountAsync(client.UserId));
        Assert.Equal(HttpStatusCode.Created, (await PlaceFreshAsync(client, address, variant)).Response.StatusCode);
    }

    [Fact]
    public async Task The_limit_applies_to_every_role_an_admin_who_shops_is_not_exempt()
    {
        var admin = await fixture.AdminAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(100);
        var address = await admin.CreateAddressAsync();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await PlaceFreshAsync(admin, address, variant)).Response.StatusCode);
        }

        var fourth = await PlaceFreshAsync(admin, address, variant);

        await ApiAssert.ProblemAsync(fourth.Response, 409, "open_order_limit");
    }

    [Fact]
    public async Task One_users_open_orders_never_block_another_user()
    {
        var (first, firstAddress, variant) = await UserWithStockAsync();
        for (var i = 0; i < 3; i++)
        {
            await PlaceFreshAsync(first, firstAddress, variant);
        }

        var second = await fixture.CustomerAsync();
        var secondAddress = await second.CreateAddressAsync();

        Assert.Equal(HttpStatusCode.Created, (await PlaceFreshAsync(second, secondAddress, variant)).Response.StatusCode);
    }

    // ---------- Günlük sınır ----------

    [Fact]
    public async Task A_cancel_and_replace_cycle_stops_at_ten_orders_in_24_hours_even_when_no_order_is_open()
    {
        var (client, address, variant) = await UserWithStockAsync();
        for (var i = 0; i < 10; i++)
        {
            var placed = await PlaceFreshAsync(client, address, variant);
            Assert.Equal(HttpStatusCode.Created, placed.Response.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.CancelAsync(await placed.Response.IdOfAsync())).StatusCode);
        }

        Assert.Equal(0, await fixture.CountAsync($"SELECT count(*) FROM orders WHERE user_id = '{client.UserId}' AND status IN ('placed', 'processing')"));
        Assert.Equal(HttpStatusCode.OK, (await client.AddToCartAsync(variant, 1)).StatusCode);
        var quote = await client.QuoteAsync(address);
        var before = await fixture.StateAsync(client.UserId, variant);
        var key = OrderKit.NewKey();

        var eleventh = await client.PlaceAsync(address, quote, key);

        using var json = await eleventh.JsonAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
        Assert.Equal("order_daily_limit", json.RootElement.GetProperty("code").GetString()); // IP sınırlayıcının rate_limited kodundan ayrı
        Assert.Equal(10, json.RootElement.GetProperty("limit").GetInt32());
        var retryAfter = Assert.Single(eleventh.Headers.GetValues("Retry-After"));
        Assert.InRange(int.Parse(retryAfter, System.Globalization.CultureInfo.InvariantCulture), 86_000, 86_400);
        Assert.Equal(before, await fixture.StateAsync(client.UserId, variant));
        Assert.Equal(0, await fixture.CountAsync($"SELECT count(*) FROM orders WHERE user_id = '{client.UserId}' AND idempotency_key = '{key}'"));
    }

    [Fact]
    public async Task Retry_after_is_derived_from_the_oldest_relevant_order_for_n_equal_to_the_limit_and_for_n_above_it()
    {
        var clock = new OffsetTimeProvider();
        var (variant, _, _) = await fixture.StockedVariantAsync(100);
        var limited = ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            services => services.AddSingleton<TimeProvider>(clock),
            new Dictionary<string, string?>(InventoryApiFixture.HighLimits)
            {
                ["Ordering:MaxOpenOrdersPerUser"] = "1000",
                ["Ordering:MaxOrdersPerRollingDay"] = "3",
            });
        var client = await fixture.CustomerAsync(limited);
        var address = await client.CreateAddressAsync();

        // Saati geri alıp üç sipariş ver: 20 saat, 10 saat ve 1 saat önce.
        foreach (var hoursAgo in new[] { 20, 10, 1 })
        {
            clock.Offset = TimeSpan.FromHours(-hoursAgo);
            Assert.Equal(HttpStatusCode.Created, (await PlaceFreshAsync(client, address, variant)).Response.StatusCode);
        }

        clock.Offset = TimeSpan.Zero;
        Assert.Equal(HttpStatusCode.OK, (await client.AddToCartAsync(variant, 1)).StatusCode);
        var quote = await client.QuoteAsync(address);

        // n = max (3 sipariş, sınır 3): en eski sipariş (20 saat önce) 4 saat sonra çıkar.
        var atLimit = await client.PlaceAsync(address, quote, OrderKit.NewKey());
        await ApiAssert.ProblemAsync(atLimit, 429, "order_daily_limit");
        var atLimitSeconds = int.Parse(Assert.Single(atLimit.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(atLimitSeconds, (4 * 3600) - 120, 4 * 3600);

        // n > max: sınır sonradan 1'e indirildi (3 sipariş var). (3 - 1 + 1) = 3. en eski = 1 saat önceki sipariş => 23 saat sonra.
        limited.Services.GetRequiredService<IOptions<OrderLimitOptions>>().Value.MaxOrdersPerRollingDay = 1;
        var overLimit = await client.PlaceAsync(address, quote, OrderKit.NewKey());
        await ApiAssert.ProblemAsync(overLimit, 429, "order_daily_limit");
        var overSeconds = int.Parse(Assert.Single(overLimit.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(overSeconds, (23 * 3600) - 120, 23 * 3600);
        await limited.DisposeAsync();
    }

    [Fact]
    public async Task Retry_after_is_the_exact_ceiling_of_the_seconds_until_the_relevant_order_leaves_the_window_with_a_frozen_clock()
    {
        // Saat DONDURULUR (gerçek saatle birlikte akmaz): 'created_at' damgası ve 'now' aynı sahte saatten gelir, bu yüzden değer toleranssız, TAM sınanır (flaky olmaz).
        var real = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(real.Ticks - (real.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
        var clock = new FixedTimeProvider(start);
        var (variant, _, _) = await fixture.StockedVariantAsync(100);
        await using var limited = ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            services => services.AddSingleton<TimeProvider>(clock),
            new Dictionary<string, string?>(InventoryApiFixture.HighLimits)
            {
                ["Ordering:MaxOpenOrdersPerUser"] = "1000",
                ["Ordering:MaxOrdersPerRollingDay"] = "3",
            });
        var client = await fixture.CustomerAsync(limited);
        var address = await client.CreateAddressAsync();

        foreach (var hoursAgo in new[] { 20, 10, 1 })
        {
            clock.Now = start.AddHours(-hoursAgo);
            Assert.Equal(HttpStatusCode.Created, (await PlaceFreshAsync(client, address, variant)).Response.StatusCode);
        }

        clock.Now = start;
        Assert.Equal(HttpStatusCode.OK, (await client.AddToCartAsync(variant, 1)).StatusCode);
        var quote = await client.QuoteAsync(address);

        async Task<int> RetryAfterAsync()
        {
            var response = await client.PlaceAsync(address, quote, OrderKit.NewKey());
            await ApiAssert.ProblemAsync(response, 429, "order_daily_limit");
            return int.Parse(Assert.Single(response.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture);
        }

        // n = max = 3: en eski sipariş (start - 20 saat) start + 4 saatte çıkar => tam 14400.
        Assert.Equal(4 * 3600, await RetryAfterAsync());
        clock.Now = start.AddSeconds(1);
        Assert.Equal((4 * 3600) - 1, await RetryAfterAsync()); // saat ilerledikçe bekleme tam bir saniye azalır
        clock.Now = start.AddMilliseconds(1250);
        Assert.Equal((4 * 3600) - 1, await RetryAfterAsync()); // 14398.75 sn => YUKARI yuvarlanır (erken yeniden deneme olmaz)
        clock.Now = start;

        // n > max: sınır 1'e indirildi (3 sipariş): 3. en eski = start - 1 saat => start + 23 saatte çıkar => tam 82800.
        limited.Services.GetRequiredService<IOptions<OrderLimitOptions>>().Value.MaxOrdersPerRollingDay = 1;
        Assert.Equal(23 * 3600, await RetryAfterAsync());
        // (Alt sınır 1 ve üst sınır 86400 sıkıştırması OrderLimitMathTests'te kesin değerle sınanır; 23 saat ileri sarmak oturum çerezini de süresi doldurur.)
    }

    // ---------- Başlangıç doğrulaması ----------

    [Theory]
    [InlineData("Ordering:MaxOpenOrdersPerUser", "0")]
    [InlineData("Ordering:MaxOpenOrdersPerUser", "1001")]
    [InlineData("Ordering:MaxOpenOrdersPerUser", "-3")]
    [InlineData("Ordering:MaxOrdersPerRollingDay", "0")]
    [InlineData("Ordering:MaxOrdersPerRollingDay", "1001")]
    public void An_invalid_limit_value_stops_the_application_from_starting(string key, string value)
    {
        using var factory = ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            extraConfiguration: new Dictionary<string, string?>(InventoryApiFixture.HighLimits) { [key] = value });

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1000")]
    public async Task The_boundary_values_1_and_1000_are_accepted(string value)
    {
        await using var factory = ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            extraConfiguration: new Dictionary<string, string?>(InventoryApiFixture.HighLimits)
            {
                ["Ordering:MaxOpenOrdersPerUser"] = value,
                ["Ordering:MaxOrdersPerRollingDay"] = value,
            });

        var response = await factory.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SSCommerce.Api.Observability;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Inventory;
using static SSCommerce.Api.Tests.Observability.ObservabilityKit;

namespace SSCommerce.Api.Tests.Orders;

/// <summary>
/// Faz 9 B4: sipariş anahtarı (<c>Ordering:Enabled</c>). Kavram: "kill switch" (acil kapatma anahtarı): yeni sipariş kabulünü, mevcut siparişleri
/// bozmadan durdurur. Kapalıyken: POST /orders 503 ordering_disabled, stoğa/sepete/DB'ye yazılmaz (kilit bile alınmaz), Idempotency-Key tüketilmez;
/// okuma uçları ve müşteri iptali çalışır. Anahtar, çalışan uygulamada yapılandırma yeniden yüklenerek çevrilir (<c>IOptionsMonitor</c>).
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class OrderingSwitchTests(InventoryApiFixture fixture)
{
    private WebApplicationFactory<Program> NewFactory(bool enabled) =>
        ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            extraConfiguration: new Dictionary<string, string?>(InventoryApiFixture.HighLimits) { ["Ordering:Enabled"] = enabled ? "true" : "false" });

    private static void Set(WebApplicationFactory<Program> factory, bool enabled)
    {
        var root = (IConfigurationRoot)factory.Services.GetRequiredService<IConfiguration>();
        root["Ordering:Enabled"] = enabled ? "true" : "false";
        root.Reload(); // IOptionsMonitor değişikliği görür (canlı anahtar)
    }

    private static string Metrics(WebApplicationFactory<Program> factory) => factory.Services.GetRequiredService<PrometheusCollector>().Render();

    [Fact]
    public async Task When_disabled_placing_returns_503_and_writes_nothing_then_the_same_idempotency_key_works_after_re_enabling()
    {
        await using var factory = NewFactory(enabled: true);
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 3, factory);
        var key = OrderKit.NewKey();
        var before = await fixture.StateAsync(shopper.UserId, variant);

        Set(factory, enabled: false);
        var refused = await shopper.PlaceAsync(key);

        await ApiAssert.ProblemAsync(refused, 503, "ordering_disabled");
        Assert.Equal("no-store", refused.Headers.CacheControl?.ToString());
        Assert.Equal(before, await fixture.StateAsync(shopper.UserId, variant)); // stok, hareket, sipariş, sepet DEĞİŞMEDİ
        Assert.Equal(0, await fixture.OrderCountAsync(shopper.UserId));
        Assert.Equal(10, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.ScalarAsync<long>($"SELECT count(*) FROM carts WHERE user_id = '{shopper.UserId}'"));
        Assert.Equal(0, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM orders WHERE user_id = '{shopper.UserId}' OR idempotency_key = '{key}'"));

        // Kapı açılınca AYNI anahtar tüketilmemiş olduğundan normal sipariş verilir.
        Set(factory, enabled: true);
        var placed = await shopper.PlaceAsync(key);

        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        Assert.Equal(7, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.OrderCountAsync(shopper.UserId));
    }

    [Fact]
    public async Task When_disabled_the_body_is_not_even_read_and_anonymous_callers_still_get_401()
    {
        await using var factory = NewFactory(enabled: false);
        var client = await fixture.CustomerAsync(factory);
        var anonymous = fixture.Anonymous(factory);

        var garbage = await client.PostAsync(OrderKit.OrdersUrl, new { addressId = "bozuk", quote = 5 }, "kisa"); // geçersiz gövde + geçersiz anahtar
        var unauthenticated = await anonymous.PostAsync(OrderKit.OrdersUrl, new { }, OrderKit.NewKey(), sendCsrf: false);

        await ApiAssert.ProblemAsync(garbage, 503, "ordering_disabled"); // 400 validation_failed DEĞİL: doğrulamaya hiç girmedi
        await ApiAssert.ProblemAsync(unauthenticated, 401, "unauthenticated");
    }

    [Fact]
    public async Task When_disabled_reads_and_customer_cancel_keep_working_and_checkout_preview_reports_orderingAvailable_false()
    {
        await using var factory = NewFactory(enabled: true);
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);
        var first = await fixture.ShopperAsync(variant, 2, factory);
        var placed = await first.PlaceAsync();
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var orderId = await placed.IdOfAsync();
        Assert.Equal(8, await fixture.OnHandAsync(warehouse, variant));
        var second = await fixture.ShopperAsync(variant, 1, factory);
        using (var open = await (await second.Client.PreviewAsync(second.AddressId)).JsonAsync())
        {
            Assert.True(open.RootElement.GetProperty("orderingAvailable").GetBoolean());
        }

        Set(factory, enabled: false);

        Assert.Equal(HttpStatusCode.OK, (await first.Client.GetOrderAsync(orderId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.Client.GetAsync(OrderKit.OrdersUrl)).StatusCode);
        using (var closed = await (await second.Client.PreviewAsync(second.AddressId)).JsonAsync())
        {
            Assert.False(closed.RootElement.GetProperty("orderingAvailable").GetBoolean());
            Assert.True(closed.RootElement.GetProperty("canProceed").GetBoolean()); // yalnız bayrak değişir; şema ve diğer alanlar aynı
        }

        var cancelled = await first.Client.CancelAsync(orderId); // mevcut sipariş yönetilebilir
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal(10, await fixture.OnHandAsync(warehouse, variant));
    }

    [Fact]
    public async Task The_orders_counter_records_ordering_disabled_placed_and_replayed_outcomes_without_personal_labels()
    {
        await using var factory = NewFactory(enabled: true);
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 1, factory);
        var key = OrderKit.NewKey();

        Assert.Equal(HttpStatusCode.Created, (await shopper.PlaceAsync(key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await shopper.PlaceAsync(key)).StatusCode); // aynı anahtar + aynı içerik: replay
        Set(factory, enabled: false);
        await shopper.PlaceAsync();
        await shopper.PlaceAsync();
        var text = Metrics(factory);

        Assert.Equal(1, Value(text, "ssc_orders_total", "outcome=\"placed\""));
        Assert.Equal(1, Value(text, "ssc_orders_total", "outcome=\"replayed\""));
        Assert.Equal(2, Value(text, "ssc_orders_total", "outcome=\"ordering_disabled\""));
        Assert.DoesNotContain(shopper.UserId.ToString(), text);
        Assert.DoesNotContain(shopper.Client.Email, text, StringComparison.OrdinalIgnoreCase);
        Assert.Subset(new HashSet<string> { "method", "route", "status_class", "le", "outcome", "to" }, LabelNames(text));
    }

    [Fact]
    public async Task The_default_is_enabled_when_the_key_is_not_configured()
    {
        await using var factory = ApiFactory.Create("Development", fixture.Database.ConnectionString, extraConfiguration: InventoryApiFixture.HighLimits);
        var (variant, _, _) = await fixture.StockedVariantAsync(5);
        var shopper = await fixture.ShopperAsync(variant, 1, factory);

        Assert.Equal(HttpStatusCode.Created, (await shopper.PlaceAsync()).StatusCode);
    }
}

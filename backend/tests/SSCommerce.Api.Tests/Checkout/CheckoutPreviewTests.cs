using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Catalog;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.Application.Orders;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Checkout;

/// <summary>
/// Checkout preview (F6.4, D-054): salt okunur, ödenecek tutar uydurulmaz. "Hiçbir şey yazmadı" iddiası, önce/sonra veritabanı görüntüsünün
/// (bakiye, sürüm, updated_at, hareket sayısı, sepet ve adres satırları) birebir aynı olmasıyla kanıtlanır.
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class CheckoutPreviewTests(InventoryApiFixture fixture)
{
    private async Task<(ApiClient Customer, Guid Address, Guid Variant, Guid Warehouse)> ReadyCartAsync(int stock = 10, int quantity = 2)
    {
        var customer = await fixture.CustomerAsync();
        var address = await customer.CreateAddressAsync();
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(stock);
        await customer.AddToCartAsync(variant, quantity);
        return (customer, address, variant, warehouse);
    }

    /// <summary>Kullanıcıya ve varyanta ait tüm ilgili tabloların güvenilir görüntüsü: değişen tek bir sütun bile özeti değiştirir.</summary>
    private async Task<string> SnapshotAsync(Guid userId, Guid variantId) =>
        (await fixture.ScalarAsync<string>(
            $"""
            SELECT md5(
              coalesce((SELECT string_agg(id::text || ':' || on_hand_quantity || ':' || version || ':' || updated_at::text, ',' ORDER BY id) FROM inventory_balances WHERE product_variant_id = '{variantId}'), '') || '|' ||
              coalesce((SELECT count(*)::text FROM stock_movements WHERE product_variant_id = '{variantId}'), '') || '|' ||
              coalesce((SELECT string_agg(id::text || ':' || updated_at::text, ',' ORDER BY id) FROM carts WHERE user_id = '{userId}'), '') || '|' ||
              coalesce((SELECT string_agg(i.id::text || ':' || i.quantity || ':' || i.unit_price_at_add || ':' || i.updated_at::text, ',' ORDER BY i.id) FROM cart_items i JOIN carts c ON c.id = i.cart_id WHERE c.user_id = '{userId}'), '') || '|' ||
              coalesce((SELECT string_agg(id::text || ':' || is_default || ':' || updated_at::text, ',' ORDER BY id) FROM customer_addresses WHERE user_id = '{userId}'), ''))
            """))!;

    [Fact]
    public async Task The_preview_has_the_documented_shape_and_never_invents_shipping_or_a_grand_total()
    {
        var (customer, address, variant, _) = await ReadyCartAsync(stock: 10, quantity: 3);

        var response = await customer.PreviewAsync(address);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await response.JsonAsync();
        var root = json.RootElement;
        Assert.Equal(
            new[] { "address", "calculatedAt", "canProceed", "currency", "grandTotalMinor", "issues", "itemCount", "lines", "orderingAvailable", "quote", "shippingMinor", "shippingStatus", "subtotalMinor" },
            root.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("shippingMinor").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("grandTotalMinor").ValueKind);
        Assert.Equal("shippingNotCalculated", root.GetProperty("shippingStatus").GetString());
        // Faz 7: sipariş artık verilebilir; quote, sipariş verme ile AYNI fonksiyondan çıkan 64 haneli küçük harfli onaltılık özet.
        Assert.True(root.GetProperty("orderingAvailable").GetBoolean());
        var quote = root.GetProperty("quote").GetString();
        Assert.Matches("^[0-9a-f]{64}$", quote);
        Assert.Equal(OrderQuote.Compute(30_000, [(variant, 3, 10_000)]), quote);
        Assert.True(root.GetProperty("canProceed").GetBoolean());
        Assert.Equal(0, root.GetProperty("issues").GetArrayLength());
        Assert.Equal(3, root.GetProperty("itemCount").GetInt32());
        Assert.Equal(30_000, root.GetProperty("subtotalMinor").GetInt64());
        Assert.Equal("TRY", root.GetProperty("currency").GetString());
        Assert.True(Math.Abs((DateTimeOffset.UtcNow - root.GetProperty("calculatedAt").GetDateTimeOffset()).TotalMinutes) < 5);
        Assert.Equal(address, root.GetProperty("address").GetProperty("id").GetGuid());
        var line = Assert.Single(root.GetProperty("lines").EnumerateArray());
        Assert.Equal(variant, line.GetProperty("variantId").GetGuid());
        Assert.Equal("available", line.GetProperty("status").GetString());
        Assert.Equal(30_000, line.GetProperty("lineSubtotalMinor").GetInt64());
    }

    [Fact]
    public async Task The_preview_writes_nothing_balances_movements_cart_rows_addresses_and_updated_at_are_untouched_and_no_order_or_payment_rows_appear()
    {
        var (customer, address, variant, warehouse) = await ReadyCartAsync(stock: 10, quantity: 4);
        await Task.Delay(30, TestContext.Current.CancellationToken); // updated_at değişseydi farklı olurdu
        var before = await SnapshotAsync(customer.UserId, variant);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await customer.PreviewAsync(address)).StatusCode);
        }

        Assert.Equal(before, await SnapshotAsync(customer.UserId, variant));
        Assert.Equal(10, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.VersionAsync(warehouse, variant));
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
        Assert.Equal(4, await fixture.LineQuantityAsync(customer.UserId, variant));
        // Sipariş tabloları artık VAR (Faz 7) ama preview onlara hiçbir şey yazmaz: bu kullanıcı için sipariş, kalem, dağıtım, geçmiş ve
        // bu varyant için sipariş hareketi satırı yoktur.
        Assert.Equal(0, await fixture.ScalarAsync<long>($"SELECT count(*) FROM orders WHERE user_id = '{customer.UserId}'"));
        Assert.Equal(0, await fixture.ScalarAsync<long>($"SELECT count(*) FROM order_items WHERE product_variant_id = '{variant}'"));
        Assert.Equal(0, await fixture.ScalarAsync<long>($"SELECT count(*) FROM order_inventory_allocations WHERE product_variant_id = '{variant}'"));
        Assert.Equal(0, await fixture.ScalarAsync<long>($"SELECT count(*) FROM order_status_history WHERE actor_user_id = '{customer.UserId}'"));
        Assert.Equal(
            0,
            await fixture.ScalarAsync<long>(
                $"SELECT count(*) FROM stock_movements WHERE product_variant_id = '{variant}' AND operation_type IN ('order_placement', 'order_cancellation')"));
        // Ödeme ve rezervasyon tabloları hâlâ yoktur: sipariş ödenmemiş operasyon kaydıdır, preview rezervasyon yaratmaz.
        Assert.Equal(
            0,
            await fixture.ScalarAsync<long>(
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name IN ('payments', 'stock_reservations', 'reservations')"));
    }

    [Fact]
    public async Task A_rejected_preview_also_writes_nothing()
    {
        var (customer, _, variant, _) = await ReadyCartAsync();
        var before = await SnapshotAsync(customer.UserId, variant);

        await ApiAssert.ProblemAsync(await customer.PreviewAsync(Guid.NewGuid()), 404, "address_not_found");

        Assert.Equal(before, await SnapshotAsync(customer.UserId, variant));
    }

    [Fact]
    public async Task An_empty_cart_gets_409_cart_empty_and_a_missing_or_foreign_address_404()
    {
        var customer = await fixture.CustomerAsync();
        var address = await customer.CreateAddressAsync();

        await ApiAssert.ProblemAsync(await customer.PreviewAsync(address), 409, "cart_empty");
        Assert.Equal(0, await fixture.CartCountAsync(customer.UserId)); // preview sepet oluşturmaz

        var (variant, _, _) = await fixture.StockedVariantAsync(5);
        await customer.AddToCartAsync(variant);
        await ApiAssert.ProblemAsync(await customer.PreviewAsync(Guid.NewGuid()), 404, "address_not_found");
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"addressId":null}""")]
    [InlineData("""{"addressId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"addressId":"abc"}""")]
    [InlineData("""{"addressId":5}""")]
    [InlineData("")]
    [InlineData("[]")]
    public async Task A_bad_preview_body_gets_400_validation_failed(string body)
    {
        var (customer, _, _, _) = await ReadyCartAsync();

        await ApiAssert.ProblemAsync(await customer.SendAsync(HttpMethod.Post, CartKit.PreviewUrl, body), 400, "validation_failed");
    }

    [Fact]
    public async Task Line_problems_are_not_errors_the_preview_is_200_with_canProceed_false_and_issues_with_item_ids()
    {
        var customer = await fixture.CustomerAsync();
        var address = await customer.CreateAddressAsync();
        var (fine, _, _) = await fixture.StockedVariantAsync(10);
        var (gone, goneWarehouse, _) = await fixture.StockedVariantAsync(10);
        var (partial, partialWarehouse, _) = await fixture.StockedVariantAsync(10);
        var (inactive, _, _) = await fixture.StockedVariantAsync(10);
        foreach (var variant in new[] { fine, gone, partial, inactive })
        {
            await customer.AddToCartAsync(variant, 5);
        }

        await fixture.SetStockAsync(goneWarehouse, gone, 0);
        await fixture.SetStockAsync(partialWarehouse, partial, 2);
        await fixture.SetVariantActiveAsync(inactive, false);

        var response = await customer.PreviewAsync(address);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await response.JsonAsync();
        Assert.False(json.RootElement.GetProperty("canProceed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("quote").ValueKind); // ilerlenemeyen sepete onay verilmez
        var issues =json.RootElement.GetProperty("issues").EnumerateArray().ToDictionary(
            i => i.GetProperty("itemId").GetGuid(),
            i => i.GetProperty("code").GetString());
        var lines = json.RootElement.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("variantId").GetGuid());
        Assert.Equal(3, issues.Count);
        Assert.Equal("out_of_stock", issues[lines[gone].GetProperty("id").GetGuid()]);
        Assert.Equal("insufficient_stock", issues[lines[partial].GetProperty("id").GetGuid()]);
        Assert.Equal("unavailable", issues[lines[inactive].GetProperty("id").GetGuid()]);
        Assert.DoesNotContain(lines[fine].GetProperty("id").GetGuid(), issues.Keys);
        Assert.Equal(JsonValueKind.Null, lines[inactive].GetProperty("unitPriceMinor").ValueKind);
        // Ara toplam: unavailable hariç 3 satır × 5 adet × 100 TL.
        Assert.Equal(150_000, json.RootElement.GetProperty("subtotalMinor").GetInt64());
    }

    [Fact]
    public async Task A_price_change_is_an_informational_issue_that_does_not_block_proceeding()
    {
        var (customer, address, variant, _) = await ReadyCartAsync(stock: 10, quantity: 2);
        await fixture.SetPriceAsync(variant, 120.00m);

        using var json = await (await customer.PreviewAsync(address)).JsonAsync();

        Assert.True(json.RootElement.GetProperty("canProceed").GetBoolean());
        var issue = Assert.Single(json.RootElement.GetProperty("issues").EnumerateArray());
        Assert.Equal("price_changed", issue.GetProperty("code").GetString());
        var line = Assert.Single(json.RootElement.GetProperty("lines").EnumerateArray());
        Assert.Equal(issue.GetProperty("itemId").GetGuid(), line.GetProperty("id").GetGuid());
        Assert.Equal(10_000, line.GetProperty("previousUnitPriceMinor").GetInt64());
        Assert.Equal(24_000, json.RootElement.GetProperty("subtotalMinor").GetInt64());
    }

    [Fact]
    public async Task A_total_beyond_the_money_limit_gets_409_cart_total_too_large()
    {
        var customer = await fixture.CustomerAsync();
        var address = await customer.CreateAddressAsync();
        var (a, _, _) = await fixture.StockedVariantAsync(3);
        var (b, _, _) = await fixture.StockedVariantAsync(3);
        await fixture.SetPriceAsync(a, 50_000_000_000_000m);
        await fixture.SetPriceAsync(b, 50_000_000_000_000m);
        await fixture.SeedCartLinesAsync(customer.UserId, [a, b]);

        await ApiAssert.ProblemAsync(await customer.PreviewAsync(address), 409, "cart_total_too_large");
    }

    // ---------- Sorgu sayısı (N+1 yok) ----------

    private sealed class LogCapture : ILoggerProvider
    {
        public ConcurrentQueue<string> Commands { get; } = new();

        public ILogger CreateLogger(string categoryName) => categoryName == CatalogQueryShapeTests.EfCommandCategory ? new Sink(Commands) : new Off();

        public void Dispose()
        {
        }

        private sealed class Sink(ConcurrentQueue<string> commands) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id == CatalogQueryShapeTests.CommandExecuted)
                {
                    commands.Enqueue(formatter(state, exception));
                }
            }
        }

        private sealed class Off : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => false;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
            }
        }
    }

    [Fact]
    public async Task Cart_read_and_preview_use_a_bounded_number_of_queries_independent_of_the_line_count()
    {
        var capture = new LogCapture();
        await using var factory = ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            services =>
            {
                services.AddSingleton<ILoggerProvider>(capture);
                services.AddLogging(logging => logging.AddFilter(CatalogQueryShapeTests.EfCommandCategory, LogLevel.Information));
            },
            InventoryApiFixture.HighLimits);
        var small = await fixture.LoginAsync(factory, AppRoles.Customer);
        var large = await fixture.LoginAsync(factory, AppRoles.Customer);
        var smallAddress = await small.CreateAddressAsync();
        var largeAddress = await large.CreateAddressAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var product = await fixture.CreateProductAsync(Enumerable.Repeat(true, 40).ToList());
        foreach (var (id, _) in product.Variants)
        {
            await fixture.SeedBalanceAsync(warehouse, id, 5);
        }

        await fixture.SeedCartLinesAsync(small.UserId, product.Variants.Take(1).Select(v => v.Id));
        await fixture.SeedCartLinesAsync(large.UserId, product.Variants.Select(v => v.Id));

        async Task<int> CountAsync(Func<Task<HttpResponseMessage>> request)
        {
            capture.Commands.Clear();
            var response = await request();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return capture.Commands.Count;
        }

        var smallGet = await CountAsync(() => small.GetCartAsync());
        var largeGet = await CountAsync(() => large.GetCartAsync());
        var smallPreview = await CountAsync(() => small.PreviewAsync(smallAddress));
        var largePreview = await CountAsync(() => large.PreviewAsync(largeAddress));

        // Kimliği doğrulanmış her istek, security stamp denetimi için sabit sayıda ek sorgu yapar (D-033). Ölçüt: tek sorgu yapan
        // bilinen bir uç (GET /addresses) aynı yükü taşır; sepet okuma ONUNLA AYNI sayıda (yani tek sorgu), preview bir fazla (adres + satırlar)
        // olmalı ve bu sayılar 1 satır ile 40 satır için aynı kalmalı.
        var baseline = await CountAsync(() => small.GetAsync(CartKit.AddressesUrl));
        Assert.True(smallGet == baseline, $"sepet okuma tek sorgu olmalı: get={smallGet} baseline={baseline}");
        Assert.True(largeGet == baseline, $"40 satırlık sepet de tek sorgu olmalı: get={largeGet} baseline={baseline}");
        Assert.True(smallPreview == baseline + 1, $"preview iki sorgu olmalı: preview={smallPreview} baseline={baseline}");
        Assert.True(largePreview == baseline + 1, $"40 satırlık preview iki sorgu olmalı: preview={largePreview} baseline={baseline}");
    }
}

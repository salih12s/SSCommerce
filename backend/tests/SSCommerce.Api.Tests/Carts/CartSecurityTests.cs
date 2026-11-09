using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.Application.Addresses;
using SSCommerce.Application.Carts;
using SSCommerce.Application.Checkout;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Carts;

/// <summary>Kimlik doğrulama, CSRF/Origin, no-store, rate limit, güvenli hata ve kişisel verinin loga sızmaması (cart/addresses/checkout).</summary>
[Collection(InventoryApiCollection.Name)]
public class CartSecurityTests(InventoryApiFixture fixture)
{
    private static readonly Guid SomeId = new("5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a01");

    private static readonly (string Name, string Method, string Url, string? Body)[] AllRoutes =
    [
        ("GET cart", "GET", "/api/v1/cart", null),
        ("POST cart item", "POST", "/api/v1/cart/items", """{"variantId":"5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a01","quantity":1}"""),
        ("PATCH cart item", "PATCH", "/api/v1/cart/items/5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a01", """{"quantity":1}"""),
        ("DELETE cart item", "DELETE", "/api/v1/cart/items/5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a01", null),
        ("GET addresses", "GET", "/api/v1/addresses", null),
        ("POST address", "POST", "/api/v1/addresses", """{"label":"Ev"}"""),
        ("PUT address", "PUT", "/api/v1/addresses/5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a01", """{"label":"Ev"}"""),
        ("POST address default", "POST", "/api/v1/addresses/5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a01/default", null),
        ("DELETE address", "DELETE", "/api/v1/addresses/5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a01", null),
        ("POST preview", "POST", "/api/v1/checkout/preview", """{"addressId":"5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a01"}"""),
    ];

    private static Task<HttpResponseMessage> Send(ApiClient client, (string Name, string Method, string Url, string? Body) route, bool csrf = true, string? origin = null) =>
        client.SendAsync(new HttpMethod(route.Method), route.Url, route.Body, sendCsrf: csrf, origin: origin);

    // ---------- Kimlik ----------

    [Fact]
    public async Task Every_route_returns_401_unauthenticated_problem_json_for_anonymous_callers_without_a_redirect()
    {
        var anonymous = fixture.Anonymous();
        foreach (var route in AllRoutes)
        {
            var response = await Send(anonymous, route, csrf: false);
            await ApiAssert.ProblemAsync(response, 401, "unauthenticated");
            Assert.Null(response.Headers.Location); // giriş sayfasına yönlendirme yok
        }
    }

    [Fact]
    public async Task A_cookie_revoked_by_logout_gets_401_on_cart_address_and_checkout_routes()
    {
        var customer = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(5);
        await customer.AddToCartAsync(variant);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetCartAsync()).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await customer.PostAsync("/api/v1/auth/logout", null)).StatusCode);

        // AYNI (artık çalıntı sayılan) cookie ile tüm uçlar 401.
        foreach (var route in AllRoutes)
        {
            await ApiAssert.ProblemAsync(await Send(customer, route), 401, "unauthenticated");
        }

        Assert.Equal(1, await fixture.LineQuantityAsync(customer.UserId, variant)); // sepet verisi durur, erişim kapanır
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Warehouse")]
    [InlineData("Admin")]
    public async Task Every_role_owns_and_uses_its_own_cart_addresses_and_preview(string role)
    {
        var user = await fixture.LoginAsync(fixture.DevelopmentFactory, role);
        var other = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(10);

        Assert.Equal(HttpStatusCode.OK, (await user.AddToCartAsync(variant, 2)).StatusCode);
        var address = await user.CreateAddressAsync();
        var preview = await user.PreviewAsync(address);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(2, await fixture.LineQuantityAsync(user.UserId, variant));
        using var otherCart = await (await other.GetCartAsync()).JsonAsync();
        Assert.Equal(0, otherCart.RootElement.GetProperty("items").GetArrayLength());
    }

    // ---------- CSRF / Origin ----------

    [Fact]
    public async Task Missing_csrf_and_a_foreign_Origin_are_rejected_on_every_write_route_and_nothing_is_written()
    {
        var customer = await fixture.CustomerAsync(fixture.ProductionFactory);
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var address = await customer.CreateAddressAsync("Var olan");
        await customer.AddToCartAsync(variant, 2);
        var item = await customer.ItemIdAsync(variant);
        var addressBefore = await fixture.AddressCountAsync(customer.UserId);
        var payloads = new (string Method, string Url, string? Body)[]
        {
            ("POST", CartKit.ItemsUrl, JsonSerializer.Serialize(new { variantId = variant, quantity = 3 })),
            ("PATCH", $"{CartKit.ItemsUrl}/{item}", """{"quantity":9}"""),
            ("DELETE", $"{CartKit.ItemsUrl}/{item}", null),
            ("POST", CartKit.AddressesUrl, JsonSerializer.Serialize(CartKit.ValidAddress("Yeni"))),
            ("PUT", $"{CartKit.AddressesUrl}/{address}", JsonSerializer.Serialize(CartKit.ValidAddress("Değişti"))),
            ("POST", $"{CartKit.AddressesUrl}/{address}/default", null),
            ("DELETE", $"{CartKit.AddressesUrl}/{address}", null),
            ("POST", CartKit.PreviewUrl, JsonSerializer.Serialize(new { addressId = address })),
        };

        foreach (var (method, url, body) in payloads)
        {
            await ApiAssert.ProblemAsync(
                await customer.SendAsync(new HttpMethod(method), url, body, sendCsrf: false), 400, "csrf_invalid");
            await ApiAssert.ProblemAsync(
                await customer.SendAsync(new HttpMethod(method), url, body, origin: "https://evil.example"), 403, "origin_not_allowed");
        }

        Assert.Equal(2, await fixture.LineQuantityAsync(customer.UserId, variant));
        Assert.Equal(addressBefore, await fixture.AddressCountAsync(customer.UserId));
        Assert.Equal("Var olan", await fixture.ScalarAsync<string>($"SELECT label FROM customer_addresses WHERE id = '{address}'"));
        // İzinli origin ve geçerli CSRF ile aynı yazma çalışır.
        Assert.Equal(
            HttpStatusCode.OK,
            (await customer.SendAsync(HttpMethod.Patch, $"{CartKit.ItemsUrl}/{item}", """{"quantity":4}""", origin: InventoryApiFixture.ProductionOrigin)).StatusCode);
    }

    // ---------- no-store ----------

    [Fact]
    public async Task Cart_address_and_checkout_responses_are_no_store_on_success_and_on_every_error()
    {
        var customer = await fixture.CustomerAsync();
        var anonymous = fixture.Anonymous();
        var (variant, _, _) = await fixture.StockedVariantAsync(1);
        var address = await customer.CreateAddressAsync();

        var responses = new List<HttpResponseMessage>
        {
            await customer.GetCartAsync(),                                          // 200
            await customer.AddToCartAsync(variant),                                 // 200
            await customer.AddToCartAsync(variant),                                 // 409 stok
            await customer.AddToCartAsync(Guid.NewGuid()),                          // 404
            await customer.SendAsync(HttpMethod.Post, CartKit.ItemsUrl, "{"),       // 400
            await customer.SendAsync(HttpMethod.Post, CartKit.ItemsUrl, "{}", sendCsrf: false), // csrf 400
            await customer.GetAsync(CartKit.AddressesUrl),                          // 200
            await customer.PreviewAsync(address),                                   // 200
            await customer.PreviewAsync(Guid.NewGuid()),                            // 404
            await customer.RemoveItemAsync(Guid.NewGuid()),                         // 404
            await customer.DeleteAsync($"{CartKit.AddressesUrl}/{address}"),        // 204
            await anonymous.GetCartAsync(),                                         // 401
            await anonymous.GetAsync(CartKit.AddressesUrl),                         // 401
            await anonymous.PreviewAsync(SomeId),                                   // 401
        };

        Assert.All(responses, response => Assert.Equal("no-store", response.Headers.CacheControl?.ToString()));
    }

    // ---------- Rate limit ----------

    [Fact]
    public async Task Writes_and_preview_share_the_mutation_limit_get_is_not_counted_and_429_is_problem_json()
    {
        await using var factory = ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            extraConfiguration: new Dictionary<string, string?>(InventoryApiFixture.HighLimits)
            {
                ["Security:RateLimit:MutationPermitLimit"] = "4",
                ["Security:RateLimit:WindowSeconds"] = "600",
            });
        var customer = await fixture.LoginAsync(factory, AppRoles.Customer);
        var (variant, _, _) = await fixture.StockedVariantAsync(10);

        var statuses = new List<HttpStatusCode>
        {
            (await customer.AddToCartAsync(variant)).StatusCode,                          // 1 (mutation)
            (await customer.PostAsync(CartKit.AddressesUrl, CartKit.ValidAddress())).StatusCode, // 2
            (await customer.PreviewAsync(Guid.NewGuid())).StatusCode,                      // 3 (404 ama sayılır)
            (await customer.RemoveItemAsync(Guid.NewGuid())).StatusCode,                   // 4 (404)
        };
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await customer.GetCartAsync()).StatusCode);   // okuma sayılmaz
        }

        var limited = await customer.AddToCartAsync(variant);

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.NotFound, HttpStatusCode.NotFound], statuses);
        await ApiAssert.ProblemAsync(limited, 429, "rate_limited");
        Assert.True(limited.Headers.RetryAfter?.Delta is { } delta && delta.TotalSeconds is > 0 and <= 600);
        Assert.Equal(1, await fixture.LineQuantityAsync(customer.UserId, variant)); // 429 hiçbir şey yazmadı
    }

    [Fact]
    public void Every_cart_address_and_checkout_write_route_carries_the_mutation_policy_and_every_route_requires_authorization()
    {
        var endpoints = fixture.DevelopmentFactory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => new[] { "/api/v1/cart", "/api/v1/addresses", "/api/v1/checkout" }.Any(p => e.RoutePattern.RawText!.StartsWith(p, StringComparison.Ordinal)))
            .ToList();
        Assert.Equal(AllRoutes.Length, endpoints.Count);

        foreach (var endpoint in endpoints)
        {
            var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()!.HttpMethods;
            Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>());
            Assert.Null(endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>());
            if (methods.Any(m => m is "POST" or "PUT" or "PATCH" or "DELETE"))
            {
                Assert.Contains(
                    endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>(),
                    attribute => attribute.PolicyName == "mutation");
            }
        }
    }

    // ---------- Güvenli hata ----------

    private sealed class ThrowingCart(string message) : ICartService
    {
        public Task<CartOperationResult> GetAsync(Guid userId, CancellationToken cancellationToken) => throw new InvalidOperationException(message);

        public Task<CartOperationResult> AddItemAsync(Guid userId, AddCartItemCommand command, CancellationToken cancellationToken) => throw new InvalidOperationException(message);

        public Task<CartOperationResult> SetQuantityAsync(Guid userId, Guid itemId, int quantity, CancellationToken cancellationToken) => throw new InvalidOperationException(message);

        public Task<CartOperationResult> RemoveItemAsync(Guid userId, Guid itemId, CancellationToken cancellationToken) => throw new InvalidOperationException(message);
    }

    private sealed class ThrowingAddresses(string message) : IAddressService
    {
        public Task<IReadOnlyList<AddressDto>> ListAsync(Guid userId, CancellationToken cancellationToken) => throw new InvalidOperationException(message);

        public Task<AddressOperationResult> CreateAsync(Guid userId, AddressInput input, CancellationToken cancellationToken) => throw new InvalidOperationException(message);

        public Task<AddressOperationResult> UpdateAsync(Guid userId, Guid id, AddressInput input, CancellationToken cancellationToken) => throw new InvalidOperationException(message);

        public Task<AddressOperationResult> SetDefaultAsync(Guid userId, Guid id, CancellationToken cancellationToken) => throw new InvalidOperationException(message);

        public Task<AddressOperationResult> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken) => throw new InvalidOperationException(message);
    }

    private sealed class ThrowingPreview(string message) : ICheckoutPreviewService
    {
        public Task<CheckoutPreviewResult> PreviewAsync(Guid userId, Guid addressId, CancellationToken cancellationToken) => throw new InvalidOperationException(message);
    }

    [Fact]
    public async Task Unexpected_failures_return_a_generic_500_without_exception_text_sql_or_personal_data()
    {
        const string secret = "Host=10.1.2.3;Password=hunter2;cart_items violates 5321234567";
        await using var factory = ApiFactory.Create(
            "Production",
            fixture.Database.ConnectionString,
            services =>
            {
                services.AddScoped<ICartService>(_ => new ThrowingCart(secret));
                services.AddScoped<IAddressService>(_ => new ThrowingAddresses(secret));
                services.AddScoped<ICheckoutPreviewService>(_ => new ThrowingPreview(secret));
            },
            new Dictionary<string, string?>(InventoryApiFixture.HighLimits) { ["Security:AllowedOrigins:0"] = InventoryApiFixture.ProductionOrigin });
        var customer = await fixture.LoginAsync(factory, AppRoles.Customer);

        var responses = new[]
        {
            await customer.GetCartAsync(),
            await customer.AddToCartAsync(SomeId),
            await customer.SetQuantityAsync(SomeId, 1),
            await customer.RemoveItemAsync(SomeId),
            await customer.GetAsync(CartKit.AddressesUrl),
            await customer.PostAsync(CartKit.AddressesUrl, CartKit.ValidAddress()),
            await customer.PutAsync($"{CartKit.AddressesUrl}/{SomeId}", CartKit.ValidAddress()),
            await customer.PostAsync($"{CartKit.AddressesUrl}/{SomeId}/default", null),
            await customer.DeleteAsync($"{CartKit.AddressesUrl}/{SomeId}"),
            await customer.PreviewAsync(SomeId),
        };

        foreach (var response in responses)
        {
            await ApiAssert.ProblemAsync(response, 500, "internal_error");
            var text = await response.TextAsync();
            foreach (var forbidden in new[] { "hunter2", "10.1.2.3", "cart_items", "5321234567", "Exception", "InvalidOperation" })
            {
                Assert.DoesNotContain(forbidden, text);
            }
        }
    }

    // ---------- PII ve log ----------

    private sealed class AllLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Sink(Lines);

        public void Dispose()
        {
        }

        private sealed class Sink(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
        }
    }

    [Fact]
    public async Task Addresses_phones_names_emails_and_cart_contents_never_appear_in_any_log_line_even_at_trace_level()
    {
        var logs = new AllLogs();
        var marker = Guid.NewGuid().ToString("N")[..10];
        await using var factory = ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            services =>
            {
                services.AddSingleton<ILoggerProvider>(logs);
                services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace));
            },
            InventoryApiFixture.HighLimits);
        var customer = await fixture.LoginAsync(factory, AppRoles.Customer);
        var (variant, _, sku) = await fixture.StockedVariantAsync(10);
        var phone = "5329" + Random.Shared.Next(100000, 999999);
        var recipient = $"Gizli Alici {marker}";
        var street = $"Gizli Sokak {marker} No 7";
        var label = $"Gizli Etiket {marker}";

        // Başarılı akış
        var created = await customer.PostAsync(CartKit.AddressesUrl, new { label, recipientName = recipient, phone, province = "Bursa", district = $"Ilce {marker}", streetAddress = street, postalCode = "16000" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = await created.JsonAsync();
        var addressId = createdJson.RootElement.GetProperty("id").GetGuid();
        await customer.AddToCartAsync(variant, 2);
        await customer.PreviewAsync(addressId);
        await customer.PutAsync($"{CartKit.AddressesUrl}/{addressId}", new { label, recipientName = recipient, phone, province = "Bursa", district = $"Ilce {marker}", streetAddress = street, postalCode = (string?)null });
        // Hatalı akışlar (doğrulama, 404, 409, yetkisiz) da PII sızdırmamalı.
        await customer.PostAsync(CartKit.AddressesUrl, new { label, recipientName = recipient, phone = "bozuk-" + phone, province = "Atlantis", district = "x", streetAddress = street });
        await customer.SendAsync(HttpMethod.Post, CartKit.AddressesUrl, $$"""{"label":"{{label}}","recipientName":"{{recipient}}","phone":""" );
        await customer.AddToCartAsync(variant, 99);
        await customer.PreviewAsync(Guid.NewGuid());
        await fixture.Anonymous(factory).PostAsync(CartKit.AddressesUrl, new { label, recipientName = recipient, phone, streetAddress = street });
        await customer.DeleteAsync($"{CartKit.AddressesUrl}/{addressId}");

        var text = string.Join('\n', logs.Lines);
        Assert.NotEmpty(logs.Lines); // yakalama gerçekten çalışıyor (boş log "sızıntı yok" demek olmasın)
        foreach (var secret in new[] { phone, recipient, street, label, marker, customer.Email, sku, "Atlantis" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}

using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Orders;

/// <summary>
/// Sipariş uçlarının güvenliği: kimlik (401), rol (403), CSRF/Origin, sahiplik (başkasının siparişi "yok" ile bayt düzeyinde aynı),
/// girdi sınırları, no-store, rate limit, iptal edilmiş oturum ve kişisel verinin loga sızmaması.
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class OrderSecurityTests(InventoryApiFixture fixture)
{
    private static readonly Guid SomeId = new("5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a01");

    private static readonly (string Name, string Method, string Url, string? Body)[] AllRoutes =
    [
        ("place", "POST", "/api/v1/orders", """{"addressId":"5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a01","quote":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}"""),
        ("list", "GET", "/api/v1/orders", null),
        ("detail", "GET", "/api/v1/orders/5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a01", null),
        ("cancel", "POST", "/api/v1/orders/5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a01/cancel", null),
        ("staff list", "GET", "/api/v1/admin/orders", null),
        ("staff status", "POST", "/api/v1/admin/orders/5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a01/status", """{"status":"processing"}"""),
        // Faz 8: personel detayı (WarehouseOrAdmin) ve Admin-only teslimat görünümü (POST).
        ("staff detail", "GET", "/api/v1/admin/orders/5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a01", null),
        ("delivery view", "POST", "/api/v1/admin/orders/5b0c1d6e-7a34-4f0e-9c1a-2d8f6e3b0a01/delivery-view", null),
    ];

    private static Task<HttpResponseMessage> Send(ApiClient client, (string Name, string Method, string Url, string? Body) route, bool csrf = true, string? origin = null) =>
        client.SendAsync(new HttpMethod(route.Method), route.Url, route.Body, route.Method == "POST" ? OrderKit.NewKey() : null, csrf, origin);

    // ---------- 401 / 403 ----------

    [Fact]
    public async Task Every_order_route_returns_401_unauthenticated_for_anonymous_callers_without_a_redirect()
    {
        var anonymous = fixture.Anonymous();

        foreach (var route in AllRoutes)
        {
            var response = await Send(anonymous, route, csrf: false);
            await ApiAssert.ProblemAsync(response, 401, "unauthenticated");
            Assert.Null(response.Headers.Location);
        }
    }

    [Fact]
    public async Task A_customer_gets_403_forbidden_on_the_staff_routes_and_the_order_is_untouched()
    {
        var admin = await fixture.AdminAsync();
        var (shopper, orderId, variant, warehouse) = await fixture.OrderInStateAsync("placed", admin);

        await ApiAssert.ProblemAsync(await shopper.Client.GetAsync(OrderKit.StaffUrl), 403, "forbidden");
        await ApiAssert.ProblemAsync(await shopper.Client.StaffStatusAsync(orderId, "processing"), 403, "forbidden");
        await ApiAssert.ProblemAsync(await shopper.Client.StaffStatusAsync(orderId, "cancelled"), 403, "forbidden");

        Assert.Equal(1, await fixture.OrderStatusCountAsync(orderId, "placed"));
        Assert.Equal(8, await fixture.OnHandAsync(warehouse, variant));
    }

    [Fact]
    public async Task Staff_users_can_read_the_staff_list_and_staff_roles_still_own_a_private_customer_history()
    {
        var warehouse = await fixture.WarehouseAsync();
        var admin = await fixture.AdminAsync();

        Assert.Equal(HttpStatusCode.OK, (await warehouse.GetAsync(OrderKit.StaffUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(OrderKit.StaffUrl)).StatusCode);
        // Personel kendi müşteri geçmişini görür; başkasının siparişini /orders üzerinden GÖRMEZ.
        var (_, orderId, _, _) = await fixture.OrderInStateAsync("placed", admin);
        await ApiAssert.ProblemAsync(await warehouse.GetOrderAsync(orderId), 404, "order_not_found");
        await ApiAssert.ProblemAsync(await admin.GetOrderAsync(orderId), 404, "order_not_found");
        await ApiAssert.ProblemAsync(await admin.CancelAsync(orderId), 404, "order_not_found");
    }

    // ---------- Sahiplik ----------

    [Fact]
    public async Task Another_users_order_is_a_404_byte_identical_to_a_missing_one_on_detail_and_cancel_and_nothing_changes()
    {
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);
        var owner = await fixture.ShopperAsync(variant, 2);
        var intruder = await fixture.CustomerAsync();
        var orderId = await (await owner.PlaceAsync()).IdOfAsync();
        var before = await fixture.StateAsync(owner.UserId, variant);

        var foreignDetail = await intruder.GetOrderAsync(orderId);
        var missingDetail = await intruder.GetOrderAsync(Guid.NewGuid());
        var foreignCancel = await intruder.CancelAsync(orderId);
        var missingCancel = await intruder.CancelAsync(Guid.NewGuid());

        await ApiAssert.ProblemAsync(foreignDetail, 404, "order_not_found");
        await ApiAssert.ProblemAsync(foreignCancel, 404, "order_not_found");
        Assert.Equal(await missingDetail.BodyWithoutTraceIdAsync(), await foreignDetail.BodyWithoutTraceIdAsync());
        Assert.Equal(await missingCancel.BodyWithoutTraceIdAsync(), await foreignCancel.BodyWithoutTraceIdAsync());
        Assert.Equal(missingDetail.Content.Headers.ContentType, foreignDetail.Content.Headers.ContentType);
        Assert.Equal(missingDetail.Headers.CacheControl?.ToString(), foreignDetail.Headers.CacheControl?.ToString());
        Assert.Equal(before, await fixture.StateAsync(owner.UserId, variant));
        Assert.Equal(8, await fixture.OnHandAsync(warehouse, variant));
        // Başkasının siparişi listede de görünmez.
        using var list = await (await intruder.GetAsync(OrderKit.OrdersUrl)).JsonAsync();
        Assert.Equal(0, list.RootElement.GetProperty("items").GetArrayLength());
    }

    // ---------- CSRF / Origin ----------

    [Fact]
    public async Task Missing_csrf_and_a_foreign_Origin_are_rejected_on_every_order_mutation_and_nothing_is_written()
    {
        var admin = await fixture.AdminAsync(fixture.ProductionFactory);
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 2, fixture.ProductionFactory);
        var before = await fixture.StateAsync(shopper.UserId, variant);
        var (_, adminOrderId, _, _) = await fixture.OrderInStateAsync("placed", admin);
        var key = OrderKit.NewKey();
        var placeBody = $$"""{"addressId":"{{shopper.AddressId}}","quote":"{{shopper.Quote}}"}""";

        var mutations = new (ApiClient Client, string Url, string? Body, string? Key)[]
        {
            (shopper.Client, OrderKit.OrdersUrl, placeBody, key),
            (shopper.Client, $"{OrderKit.OrdersUrl}/{adminOrderId}/cancel", null, null),
            (admin, $"{OrderKit.StaffUrl}/{adminOrderId}/status", """{"status":"processing"}""", null),
        };
        foreach (var (client, url, body, idempotencyKey) in mutations)
        {
            await ApiAssert.ProblemAsync(
                await client.SendAsync(HttpMethod.Post, url, body, idempotencyKey, sendCsrf: false), 400, "csrf_invalid");
            await ApiAssert.ProblemAsync(
                await client.SendAsync(HttpMethod.Post, url, body, idempotencyKey, origin: "https://evil.example"), 403, "origin_not_allowed");
        }

        Assert.Equal(before, await fixture.StateAsync(shopper.UserId, variant));
        Assert.Equal(1, await fixture.OrderStatusCountAsync(adminOrderId, "placed"));
        Assert.Equal(10, await fixture.OnHandAsync(warehouse, variant)); // reddedilen istekler stoğa dokunmadı
        // İzinli origin + geçerli CSRF ile aynı istek çalışır.
        var allowed = await shopper.Client.SendAsync(HttpMethod.Post, OrderKit.OrdersUrl, placeBody, key, origin: InventoryApiFixture.ProductionOrigin);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    // ---------- Girdi sınırları ----------

    [Fact]
    public async Task Malformed_route_ids_and_paging_bounds_are_400_validation_failed()
    {
        var customer = await fixture.CustomerAsync();

        foreach (var id in new[] { "not-a-guid", "123", "00000000-0000-0000-0000-000000000000", "%20" })
        {
            await ApiAssert.ProblemAsync(await customer.GetAsync($"{OrderKit.OrdersUrl}/{id}"), 400, "validation_failed");
            await ApiAssert.ProblemAsync(await customer.PostAsync($"{OrderKit.OrdersUrl}/{id}/cancel", null), 400, "validation_failed");
        }

        foreach (var query in new[] { "page=0", "page=-1", "page=10001", "page=abc", "page=1.5", "pageSize=0", "pageSize=51", "pageSize=-5", "pageSize=x", "page=99999999999999999999" })
        {
            await ApiAssert.ProblemAsync(await customer.GetAsync($"{OrderKit.OrdersUrl}?{query}"), 400, "validation_failed");
        }

        foreach (var query in new[] { "page=1&pageSize=1", "page=1&pageSize=50", "page=10000&pageSize=50", string.Empty })
        {
            Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync($"{OrderKit.OrdersUrl}?{query}")).StatusCode);
        }

        using var empty = await (await customer.GetAsync($"{OrderKit.OrdersUrl}?page=10000&pageSize=50")).JsonAsync();
        Assert.Equal(0, empty.RootElement.GetProperty("items").GetArrayLength());
        using var defaults = await (await customer.GetAsync(OrderKit.OrdersUrl)).JsonAsync();
        Assert.Equal(10, defaults.RootElement.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Idempotency_keys_at_the_length_bounds_work_and_hostile_keys_are_rejected_without_echoing_them()
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(20);
        var shopper = await fixture.ShopperAsync(variant, 1);
        var body = $$"""{"addressId":"{{shopper.AddressId}}","quote":"{{shopper.Quote}}"}""";

        foreach (var hostile in new[] { new string('a', 15), new string('a', 129), "key'; DROP TABLE orders; --", "анахтар-анахтар-анахтар-1", "key with space 123456789", "key/with/slash/1234567" })
        {
            var response = await shopper.Client.SendAsync(HttpMethod.Post, OrderKit.OrdersUrl, body, hostile);
            await ApiAssert.ProblemAsync(response, 400, "validation_failed");
            Assert.DoesNotContain("DROP TABLE", await response.TextAsync());
        }

        Assert.Equal(0, await fixture.OrderCountAsync(shopper.UserId));
        Assert.Equal(HttpStatusCode.Created, (await shopper.Client.SendAsync(HttpMethod.Post, OrderKit.OrdersUrl, body, new string('a', 16))).StatusCode);
        var second = await fixture.ShopperAsync(variant, 1);
        var secondBody = $$"""{"addressId":"{{second.AddressId}}","quote":"{{second.Quote}}"}""";
        Assert.Equal(HttpStatusCode.Created, (await second.Client.SendAsync(HttpMethod.Post, OrderKit.OrdersUrl, secondBody, new string('Z', 128))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await (await fixture.ShopperAsync(variant, 1)).PlaceAsync("A_b-C_d-E_f-G_h-1234")).StatusCode);
    }

    // ---------- no-store ----------

    [Fact]
    public async Task Every_order_response_is_no_store_on_success_and_on_every_error()
    {
        var admin = await fixture.AdminAsync();
        var customer = await fixture.CustomerAsync();
        var anonymous = fixture.Anonymous();
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 1);
        var key = OrderKit.NewKey();
        var placed = await shopper.PlaceAsync(key);
        var orderId = await placed.IdOfAsync();

        var responses = new List<HttpResponseMessage>
        {
            placed,                                                                                          // 201
            await shopper.PlaceAsync(key),                                                                   // 200 replay
            await shopper.Client.PlaceAsync(shopper.AddressId, new string('0', 64)),                         // 409
            await shopper.Client.SendAsync(HttpMethod.Post, OrderKit.OrdersUrl, "{", OrderKit.NewKey()),     // 400
            await shopper.Client.SendAsync(HttpMethod.Post, OrderKit.OrdersUrl, "{}", OrderKit.NewKey(), sendCsrf: false), // csrf 400
            await shopper.Client.GetAsync(OrderKit.OrdersUrl),                                               // 200
            await shopper.Client.GetAsync($"{OrderKit.OrdersUrl}?page=0"),                                   // 400
            await shopper.Client.GetOrderAsync(orderId),                                                     // 200
            await customer.GetOrderAsync(orderId),                                                           // 404
            await shopper.Client.GetAsync($"{OrderKit.OrdersUrl}/bozuk"),                                    // 400
            await customer.CancelAsync(Guid.NewGuid()),                                                      // 404
            await admin.GetAsync(OrderKit.StaffUrl),                                                         // 200
            await admin.StaffStatusAsync(orderId, "completed"),                                              // 409
            await admin.StaffStatusAsync(orderId, "processing"),                                             // 200
            await customer.GetAsync(OrderKit.StaffUrl),                                                      // 403
            await shopper.Client.CancelAsync(orderId),                                                       // 409
            await anonymous.GetAsync(OrderKit.OrdersUrl),                                                    // 401
            await anonymous.PostAsync(OrderKit.OrdersUrl, new { }, OrderKit.NewKey()),                       // 401
            await anonymous.GetAsync(OrderKit.StaffUrl),                                                     // 401
        };

        Assert.All(responses, response => Assert.Equal("no-store", response.Headers.CacheControl?.ToString()));
    }

    // ---------- Rate limit ----------

    [Fact]
    public async Task Order_writes_share_the_mutation_limit_reads_are_not_counted_and_429_writes_nothing()
    {
        await using var limited = ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            extraConfiguration: new Dictionary<string, string?>(InventoryApiFixture.HighLimits)
            {
                ["Security:RateLimit:MutationPermitLimit"] = "2",
                ["Security:RateLimit:WindowSeconds"] = "600",
            });
        var customer = await fixture.LoginAsync(limited, AppRoles.Customer);
        var address = Guid.NewGuid();

        var first = await customer.PlaceAsync(address, new string('a', 64));   // 1 (409 cart_empty ama sayılır)
        var second = await customer.CancelAsync(Guid.NewGuid());               // 2 (404)
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync(OrderKit.OrdersUrl)).StatusCode); // okuma sayılmaz
        }

        var thirdPlace = await customer.PlaceAsync(address, new string('a', 64));
        var thirdCancel = await customer.CancelAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        await ApiAssert.ProblemAsync(thirdPlace, 429, "rate_limited");
        await ApiAssert.ProblemAsync(thirdCancel, 429, "rate_limited");
        Assert.True(thirdPlace.Headers.RetryAfter?.Delta is { } delta && delta.TotalSeconds is > 0 and <= 600);
        Assert.Equal(0, await fixture.OrderCountAsync(customer.UserId));
    }

    [Fact]
    public void Every_order_write_route_carries_the_mutation_policy_and_every_route_requires_authorization()
    {
        var endpoints = fixture.DevelopmentFactory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => new[] { "/api/v1/orders", "/api/v1/admin/orders" }.Any(p => e.RoutePattern.RawText!.StartsWith(p, StringComparison.Ordinal)))
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

    // ---------- Oturum ----------

    [Fact]
    public async Task A_cookie_revoked_by_logout_gets_401_on_every_order_route_and_nothing_changes()
    {
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var shopper = await fixture.ShopperAsync(variant, 1);
        var orderId = await (await shopper.PlaceAsync()).IdOfAsync();
        var before = await fixture.StateAsync(shopper.UserId, variant);
        Assert.Equal(HttpStatusCode.NoContent, (await shopper.Client.PostAsync("/api/v1/auth/logout", null)).StatusCode);

        foreach (var route in AllRoutes.Where(r => !r.Url.Contains("admin", StringComparison.Ordinal)))
        {
            await ApiAssert.ProblemAsync(await Send(shopper.Client, route), 401, "unauthenticated");
        }

        await ApiAssert.ProblemAsync(await shopper.Client.GetOrderAsync(orderId), 401, "unauthenticated");
        await ApiAssert.ProblemAsync(await shopper.Client.CancelAsync(orderId), 401, "unauthenticated");
        Assert.Equal(before, await fixture.StateAsync(shopper.UserId, variant));
        Assert.Equal(1, await fixture.OrderStatusCountAsync(orderId, "placed"));
    }

    // ---------- Güvenli hata ve PII ----------

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
    public async Task Delivery_names_phones_addresses_emails_and_stock_details_never_appear_in_any_log_line_or_error_body_across_the_whole_order_lifecycle()
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
        var admin = await fixture.AdminAsync(factory);
        var warehouseUser = await fixture.WarehouseAsync(factory);
        var (variant, _, sku) = await fixture.StockedVariantAsync(10);
        var phone = "5329" + Random.Shared.Next(100000, 999999);
        var recipient = $"Gizli Alici {marker}";
        var street = $"Gizli Sokak {marker} No 7";
        var label = $"Gizli Etiket {marker}";
        var created = await customer.PostAsync(
            CartKit.AddressesUrl,
            new { label, recipientName = recipient, phone, province = "Bursa", district = $"Ilce {marker}", streetAddress = street, postalCode = "16000" });
        var addressId = await created.IdOfAsync();
        await customer.AddToCartAsync(variant, 2);
        var quote = await customer.QuoteAsync(addressId);
        var bodies = new List<string>();

        // Başarılı akış: ver, tekrarla, oku, iptal et, tekrar iptal et.
        var key = OrderKit.NewKey();
        var placed = await customer.PlaceAsync(addressId, quote, key);
        var orderId = await placed.IdOfAsync();
        await customer.PlaceAsync(addressId, quote, key);
        await customer.GetOrderAsync(orderId);
        await customer.GetAsync(OrderKit.OrdersUrl);
        await warehouseUser.StaffStatusAsync(orderId, "processing", "gizli neden");
        await customer.CancelAsync(orderId);                // 409: işleniyor
        await warehouseUser.StaffStatusAsync(orderId, "cancelled"); // 403
        await admin.StaffStatusAsync(orderId, "cancelled");
        await customer.CancelAsync(orderId);                // replay
        // Hatalı akışlar da PII sızdırmamalı.
        bodies.Add(await (await customer.PlaceAsync(addressId, quote, key + "x")).TextAsync());          // sepet boş
        bodies.Add(await (await customer.PlaceAsync(Guid.NewGuid(), quote)).TextAsync());                // adres yok
        bodies.Add(await (await customer.PlaceAsync(addressId, quote, key + "y")).TextAsync());
        bodies.Add(await (await customer.GetOrderAsync(Guid.NewGuid())).TextAsync());
        bodies.Add(await (await customer.SendAsync(HttpMethod.Post, OrderKit.OrdersUrl, $$"""{"addressId":"{{addressId}}","quote":""", OrderKit.NewKey())).TextAsync());
        await fixture.Anonymous(factory).PostAsync(OrderKit.OrdersUrl, new { addressId, quote }, OrderKit.NewKey());

        var text = string.Join('\n', logs.Lines);
        Assert.NotEmpty(logs.Lines); // yakalama gerçekten çalışıyor
        foreach (var secret in new[] { phone, recipient, street, label, marker, customer.Email, "Bursa", "gizli neden" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
            foreach (var body in bodies)
            {
                Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.DoesNotContain(sku, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(orderId.ToString(), text); // yalnız sipariş kimliği loglanır (bilgi amaçlı satır var)
    }

    private sealed class ThrowingPlacement(string message) : SSCommerce.Application.Orders.IOrderPlacementService
    {
        public Task<SSCommerce.Application.Orders.OrderOperationResult> PlaceAsync(SSCommerce.Application.Orders.PlaceOrderCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }

    private sealed class ThrowingStatus(string message) : SSCommerce.Application.Orders.IOrderStatusService
    {
        public Task<SSCommerce.Application.Orders.OrderOperationResult> ChangeStatusAsync(SSCommerce.Application.Orders.OrderStatusCommand command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }

    [Fact]
    public async Task Unexpected_failures_return_a_generic_500_without_exception_text_sql_or_personal_data()
    {
        const string secret = "Host=10.1.2.3;Password=hunter2;orders violates 5321234567";
        await using var factory = ApiFactory.Create(
            "Production",
            fixture.Database.ConnectionString,
            services =>
            {
                services.AddScoped<SSCommerce.Application.Orders.IOrderPlacementService>(_ => new ThrowingPlacement(secret));
                services.AddScoped<SSCommerce.Application.Orders.IOrderStatusService>(_ => new ThrowingStatus(secret));
            },
            new Dictionary<string, string?>(InventoryApiFixture.HighLimits) { ["Security:AllowedOrigins:0"] = InventoryApiFixture.ProductionOrigin });
        var customer = await fixture.LoginAsync(factory, AppRoles.Customer);
        var admin = await fixture.AdminAsync(factory);

        var responses = new[]
        {
            await customer.PlaceAsync(SomeId, new string('a', 64)),
            await customer.CancelAsync(SomeId),
            await admin.StaffStatusAsync(SomeId, "processing"),
        };

        foreach (var response in responses)
        {
            await ApiAssert.ProblemAsync(response, 500, "internal_error");
            var text = await response.TextAsync();
            foreach (var forbidden in new[] { "hunter2", "10.1.2.3", "orders violates", "5321234567", "Exception", "InvalidOperation" })
            {
                Assert.DoesNotContain(forbidden, text);
            }
        }
    }
}

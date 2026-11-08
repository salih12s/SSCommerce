using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SSCommerce.Api.Tests.Observability;

/// <summary>
/// Faz 9 B1 (güvenlik başlıkları) ve B2 (X-Request-Id korelasyonu). Veritabanı gerekmez: başlıklar her yanıtta (200, 401, 404, 429, 500, 503) olmalıdır.
/// Kavram: "defense in depth" (derinlemesine savunma): tarayıcıya "bu yanıtı sayfa gibi yorumlama, çerçeveye alma" demek, XSS/clickjacking riskini azaltır.
/// </summary>
public class SecurityHeadersAndRequestIdTests
{
    private const string Unreachable = "Host=127.0.0.1;Port=1;Timeout=2;Database=x;Username=x;Password=x";

    private static readonly Dictionary<string, string> Expected = new()
    {
        ["X-Content-Type-Options"] = "nosniff",
        ["Referrer-Policy"] = "no-referrer",
        ["X-Frame-Options"] = "DENY",
        ["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'",
        ["Cross-Origin-Resource-Policy"] = "same-origin",
    };

    private static void AssertSecurityHeaders(HttpResponseMessage response, string because)
    {
        foreach (var (name, value) in Expected)
        {
            Assert.True(response.Headers.TryGetValues(name, out var values), $"{because}: {name} yok.");
            Assert.Equal(value, Assert.Single(values));
        }

        Assert.False(response.Headers.Contains("Server"), $"{because}: Server başlığı olmamalı.");
        Assert.False(response.Headers.Contains("X-Powered-By"), $"{because}: X-Powered-By olmamalı.");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Successful_401_and_404_responses_carry_all_security_headers(string environment)
    {
        await using var factory = ApiFactory.Create(environment, postgresConnectionString: null);
        using var client = factory.CreateClient();

        var ok = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        var unauthorized = await client.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken);
        var notFound = await client.GetAsync("/api/v1/yok", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        AssertSecurityHeaders(ok, "200");
        AssertSecurityHeaders(unauthorized, "401");
        AssertSecurityHeaders(notFound, "404");
        // NoStoreMiddleware ile çakışma yok: 401'de no-store hâlâ var.
        Assert.Equal("no-store", unauthorized.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_429_rate_limited_response_carries_all_security_headers()
    {
        await using var factory = ApiFactory.Create(
            "Production",
            postgresConnectionString: null,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["Security:RateLimit:GeneralPermitLimit"] = "1",
                ["Security:RateLimit:WindowSeconds"] = "600",
            });
        using var client = factory.CreateClient();

        var first = await client.GetAsync("/api/v1/auth/csrf", TestContext.Current.CancellationToken);
        var limited = await client.GetAsync("/api/v1/auth/csrf", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        AssertSecurityHeaders(limited, "429");
        Assert.Equal("no-store", limited.Headers.CacheControl?.ToString());
        Assert.NotEmpty(limited.Headers.GetValues("X-Request-Id"));
    }

    [Fact]
    public async Task A_503_database_unavailable_response_carries_security_headers_and_the_request_id_even_after_Response_Clear()
    {
        await using var factory = ApiFactory.Create("Production", Unreachable);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/categories", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertSecurityHeaders(response, "503");
        Assert.Matches("^[0-9a-f]{32}$", Assert.Single(response.Headers.GetValues("X-Request-Id")));
    }

    [Fact]
    public async Task A_500_unhandled_exception_response_carries_security_headers()
    {
        await using var factory = ApiFactory.Create(
            "Production",
            postgresConnectionString: null,
            services => services.AddSingleton<HealthCheckService, ThrowingHealthCheckService>());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertSecurityHeaders(response, "500");
    }

    private sealed class ThrowingHealthCheckService : HealthCheckService
    {
        public override Task<HealthReport> CheckHealthAsync(Func<HealthCheckRegistration, bool>? predicate, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("kasitli");
    }

    // ---------- X-Request-Id ----------

    [Theory]
    [InlineData("abcdefgh")] // alt sınır: 8
    [InlineData("req-1234.ABC_def")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123")] // üst sınır: 64
    public async Task A_valid_incoming_request_id_is_accepted_and_echoed(string id)
    {
        await using var factory = ApiFactory.Create("Development", postgresConnectionString: null);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Request-Id", id);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(id, Assert.Single(response.Headers.GetValues("X-Request-Id")));
    }

    [Theory]
    [InlineData("kisa")] // 4 < 8
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")] // 65 > 64
    [InlineData("has space inside")]
    [InlineData("slash/not/allowed")]
    [InlineData("semi;colon-12345")]
    [InlineData("<script>alert(1)</script>")]
    public async Task An_invalid_incoming_request_id_is_replaced_by_a_generated_guid(string id)
    {
        await using var factory = ApiFactory.Create("Development", postgresConnectionString: null);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("X-Request-Id", id);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var returned = Assert.Single(response.Headers.GetValues("X-Request-Id"));
        Assert.NotEqual(id, returned);
        Assert.Matches("^[0-9a-f]{32}$", returned);
    }

    [Fact]
    public async Task A_missing_or_repeated_request_id_is_generated_and_every_request_gets_a_different_one()
    {
        await using var factory = ApiFactory.Create("Development", postgresConnectionString: null);
        using var client = factory.CreateClient();
        using var repeated = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        repeated.Headers.TryAddWithoutValidation("X-Request-Id", ["valid-id-0001", "valid-id-0002"]);

        var a = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        var b = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        var c = await client.SendAsync(repeated, TestContext.Current.CancellationToken);

        var ids = new[] { a, b, c }.Select(r => Assert.Single(r.Headers.GetValues("X-Request-Id"))).ToList();
        Assert.All(ids, id => Assert.Matches("^[0-9a-f]{32}$", id));
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public async Task The_problem_details_contract_keeps_traceId_and_gets_no_requestId_field()
    {
        await using var factory = ApiFactory.Create("Production", postgresConnectionString: null);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Add("X-Request-Id", "korelasyon-12345");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("korelasyon-12345", Assert.Single(response.Headers.GetValues("X-Request-Id")));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        Assert.False(json.RootElement.TryGetProperty("requestId", out _));
        Assert.Equal("unauthenticated", json.RootElement.GetProperty("code").GetString());
    }
}

using System.Net;
using System.Text.Json;

namespace SSCommerce.Api.Tests;

public class HealthEndpointTests
{
    // Hiçbir şey dinlemeyen port (1): bağlantı reddedilir. Sahte kimlik bilgileri.
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Timeout=2;Database=x;Username=x;Password=x";

    [Fact]
    public async Task Live_returns_200_without_any_database()
    {
        await using var factory = ApiFactory.Create("Production", postgresConnectionString: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_returns_503_when_connection_string_is_missing_and_app_still_starts()
    {
        await using var factory = ApiFactory.Create("Production", postgresConnectionString: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Ready_returns_503_with_safe_json_when_postgres_is_unreachable()
    {
        await using var factory = ApiFactory.Create("Production", UnreachableConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var json = JsonDocument.Parse(body);
        Assert.Equal("Unhealthy", json.RootElement.GetProperty("status").GetString());
        var check = Assert.Single(json.RootElement.GetProperty("checks").EnumerateArray());
        Assert.Equal("postgres", check.GetProperty("name").GetString());
        Assert.Equal("Unhealthy", check.GetProperty("status").GetString());
        Assert.True(check.GetProperty("durationMs").GetInt64() >= 0);

        // Bağlantı dizesi parçaları yanıta sızmamalı.
        Assert.DoesNotContain("127.0.0.1", body);
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Username", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ready_returns_200_against_a_real_postgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("SSCOMMERCE_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // CI'da sessiz skip yok: değişken adı yanlış yazılırsa ya da servis kalkmazsa test KIRMIZI olur.
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Fail("CI=true ama SSCOMMERCE_TEST_POSTGRES tanımlı değil; gerçek PostgreSQL testi atlanamaz. Değişkeni ve postgres servisini kontrol edin.");
            }

            Assert.Skip("SSCOMMERCE_TEST_POSTGRES tanımlı değil; gerçek PostgreSQL integration testi atlandı (geçti sayılmaz).");
        }

        await using var factory = ApiFactory.Create("Production", connectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

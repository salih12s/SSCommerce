using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SSCommerce.Api.Tests.Database;

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
        var checks = json.RootElement.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);
        Assert.Equal(["migrations", "postgres"], checks.Keys.Order().ToArray());
        Assert.All(checks.Values, check =>
        {
            Assert.Equal("Unhealthy", check.GetProperty("status").GetString());
            Assert.True(check.GetProperty("durationMs").GetInt64() >= 0);
        });

        // Bağlantı dizesi parçaları yanıta sızmamalı.
        Assert.DoesNotContain("127.0.0.1", body);
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Username", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ready_returns_200_against_a_real_migrated_postgres()
    {
        // Migration'ları uygulanmış izole veritabanı (D-029): hem "postgres" hem "migrations" kontrolü sağlıklı olmalı.
        // CI'da SSCOMMERCE_TEST_POSTGRES yoksa kırmızı, yerelde atlanır (PostgresTestDatabase kuralı).
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        await using var factory = ApiFactory.Create("Production", database.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var status = response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, status);
        using var json = JsonDocument.Parse(body);
        var checks = json.RootElement.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);
        Assert.Equal("Healthy", checks["postgres"].GetProperty("status").GetString());
        Assert.Equal("Healthy", checks["migrations"].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ready_returns_503_when_the_database_has_pending_migrations_and_does_not_leak_details()
    {
        // Bağlantı sağlıklı ama şema yok (migration uygulanmadı): D-054 gereği hazır DEĞİL.
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await using var factory = ApiFactory.Create("Production", database.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var status = response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("Unhealthy", json.RootElement.GetProperty("status").GetString());
        var checks = json.RootElement.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);
        Assert.Equal("Healthy", checks["postgres"].GetProperty("status").GetString());
        Assert.Equal("Unhealthy", checks["migrations"].GetProperty("status").GetString());

        // Migration adları, tablo adları ve bağlantı bilgisi yanıta sızmaz.
        Assert.DoesNotContain("AddInventory", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AddCatalog", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("__EFMigrationsHistory", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", body);
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(database.Name, body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(HttpStatusCode Status, string MigrationsStatus)> ReadyAsync(HttpClient client)
    {
        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        var migrations = json.RootElement.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "migrations");
        return (response.StatusCode, migrations.GetProperty("status").GetString()!);
    }

    [Fact]
    public async Task A_negative_migration_result_is_never_cached_an_unmigrated_database_that_gets_migrated_turns_ready_without_a_restart()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await using var factory = ApiFactory.Create("Production", database.ConnectionString);
        using var client = factory.CreateClient();

        Assert.Equal((HttpStatusCode.ServiceUnavailable, "Unhealthy"), await ReadyAsync(client));
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "Unhealthy"), await ReadyAsync(client)); // tekrar: hâlâ yeniden değerlendiriliyor

        await using (var context = database.CreateDbContext())
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal((HttpStatusCode.OK, "Healthy"), await ReadyAsync(client)); // AYNI uygulama örneği, yeniden başlatma yok
    }

    [Fact]
    public async Task A_positive_migration_result_is_cached_for_the_process_lifetime_without_requerying_the_history()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        await using var factory = ApiFactory.Create("Production", database.ConnectionString);
        using var client = factory.CreateClient();
        Assert.Equal((HttpStatusCode.OK, "Healthy"), await ReadyAsync(client));

        // Geçmiş tablosunu boşalt (yalnız bu izole test veritabanında): önbelleksiz olsaydı tüm migration'lar "bekleyen" görünür ve 503 olurdu.
        await database.ExecuteAsync("DELETE FROM \"__EFMigrationsHistory\"", TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode.OK, "Healthy"), await ReadyAsync(client));

        // Kanıt tersten de sağlam: yeni bir uygulama örneği (önbellek boş) aynı veritabanını artık 503 görür.
        await using var fresh = ApiFactory.Create("Production", database.ConnectionString);
        using var freshClient = fresh.CreateClient();
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "Unhealthy"), await ReadyAsync(freshClient));
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SSCommerce.Api.Errors;
using SSCommerce.Api.Tests.Database;
using SSCommerce.Application.Catalog;

namespace SSCommerce.Api.Tests;

/// <summary>
/// Veritabanına HİÇ ulaşılamıyorsa (bağlantı reddi, açılış zaman aşımı, havuz tükenmesi) genel API 503 service_unavailable döner; sıradan SQL hataları,
/// kısıt ihlalleri ve uygulama hataları 500 KALIR. Yanıtta host, port, istisna iletisi ya da SQL yoktur; Retry-After vardır.
/// </summary>
public class DatabaseUnavailableTests
{
    // Hiçbir şey dinlemeyen port (1): bağlantı reddedilir. Sahte kimlik bilgileri (HealthEndpointTests ile aynı yöntem).
    private const string UnreachableConnectionString = "Host=127.0.0.1;Port=1;Timeout=2;Database=x;Username=x;Password=x";

    private static readonly string[] Leaks =
    [
        "127.0.0.1", "Npgsql", "Failed to connect", "Password", "Username", "Socket", "Exception", "refused", "hunter2", "SELECT ", "   at ",
    ];

    internal static async Task AssertServiceUnavailableAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.True(response.Headers.RetryAfter!.Delta is { TotalSeconds: > 0 and <= 300 });
        using var json = JsonDocument.Parse(body);
        Assert.Equal(503, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("service_unavailable", json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        Assert.False(json.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String && detail.GetString()!.Length > 0);
        foreach (var leak in Leaks)
        {
            Assert.DoesNotContain(leak, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Public_catalog_routes_return_503_service_unavailable_when_the_database_port_is_closed(string environment)
    {
        await using var factory = ApiFactory.Create(environment, UnreachableConnectionString);
        using var client = factory.CreateClient();

        await AssertServiceUnavailableAsync(await client.GetAsync("/api/v1/products", TestContext.Current.CancellationToken));
        await AssertServiceUnavailableAsync(await client.GetAsync("/api/v1/products/some-product", TestContext.Current.CancellationToken));
        await AssertServiceUnavailableAsync(await client.GetAsync("/api/v1/categories", TestContext.Current.CancellationToken));
        await AssertServiceUnavailableAsync(await client.GetAsync("/api/v1/products?category=some-category", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Routes_that_do_not_touch_the_database_keep_working_and_anonymous_protected_routes_still_get_401()
    {
        await using var factory = ApiFactory.Create("Production", UnreachableConnectionString);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/cart", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/warehouses", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/yok", TestContext.Current.CancellationToken)).StatusCode);
        // /health/ready kendi kontrolleriyle 503 verir (değişmedi).
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task A_generic_exception_stays_500_even_if_its_message_looks_like_a_connection_failure()
    {
        await using var factory = ApiFactory.Create(
            "Production",
            UnreachableConnectionString,
            services => services.AddScoped<ICatalogQueries>(_ => new ThrowingCatalog(() => new InvalidOperationException("Failed to connect to 127.0.0.1:1 hunter2"))));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/products", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("internal_error", json.RootElement.GetProperty("code").GetString());
        Assert.Null(response.Headers.RetryAfter);
        Assert.DoesNotContain("hunter2", body);
    }

    [Fact]
    public async Task A_bare_socket_or_timeout_exception_from_application_code_stays_500()
    {
        // Yalnızca Npgsql'den gelen bağlantı hataları 503'tür; uygulama kodundaki çıplak SocketException/TimeoutException değil.
        foreach (var exception in new Func<Exception>[] { () => new SocketException(10061), () => new TimeoutException("x"), () => new IOException("x") })
        {
            await using var factory = ApiFactory.Create("Production", UnreachableConnectionString, services => services.AddScoped<ICatalogQueries>(_ => new ThrowingCatalog(exception)));
            using var client = factory.CreateClient();

            Assert.Equal(HttpStatusCode.InternalServerError, (await client.GetAsync("/api/v1/products", TestContext.Current.CancellationToken)).StatusCode);
        }
    }

    [Fact]
    public async Task An_unexpected_Postgres_constraint_error_surfaced_to_the_pipeline_stays_500()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        await using var factory = ApiFactory.Create(
            "Production",
            database.ConnectionString,
            services => services.AddScoped<ICatalogQueries>(_ => new ViolatingCatalog(database.ConnectionString)));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/products", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Null(response.Headers.RetryAfter);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("internal_error", json.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("ux_warehouses_code", body);
        Assert.DoesNotContain("duplicate key", body, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Sınıflandırıcı (saf) ----------

    [Fact]
    public void Connection_level_Npgsql_failures_are_unavailable_wherever_they_sit_in_the_exception_chain()
    {
        var refused = new NpgsqlException("Failed to connect to 127.0.0.1:1", new SocketException(10061));
        var timedOut = new NpgsqlException("The operation has timed out", new TimeoutException());
        var io = new NpgsqlException("Exception while reading from stream", new IOException("x"));

        Assert.True(DatabaseFailures.IsUnavailable(refused));
        Assert.True(DatabaseFailures.IsUnavailable(timedOut));            // açılış/havuz tükenmesi zaman aşımı
        Assert.True(DatabaseFailures.IsUnavailable(io));
        Assert.True(DatabaseFailures.IsUnavailable(new InvalidOperationException("sarmalayıcı", refused)));
        Assert.True(DatabaseFailures.IsUnavailable(new AggregateException(new InvalidOperationException("x"), refused)));
    }

    [Theory]
    [InlineData("08006", true)]    // connection_failure
    [InlineData("08001", true)]
    [InlineData("57P01", true)]    // admin_shutdown
    [InlineData("57P03", true)]    // cannot_connect_now (başlangıç/kurtarma)
    [InlineData("53300", true)]    // too_many_connections
    [InlineData("23505", false)]   // unique violation
    [InlineData("23503", false)]   // foreign key
    [InlineData("23514", false)]   // check
    [InlineData("42P01", false)]   // tablo yok
    [InlineData("22021", false)]   // geçersiz bayt dizisi (NUL)
    [InlineData("55P03", false)]   // lock timeout: stock_busy/busy kendi eşlemesini kullanır
    [InlineData("40P01", false)]
    public void Postgres_errors_are_unavailable_only_for_connection_class_codes(string sqlState, bool unavailable)
    {
        var exception = new PostgresException("x", "ERROR", "ERROR", sqlState);

        Assert.Equal(unavailable, DatabaseFailures.IsUnavailable(exception));
        Assert.Equal(unavailable, DatabaseFailures.IsUnavailable(new InvalidOperationException("sarmalayıcı", exception)));
    }

    [Fact]
    public void Ordinary_and_unknown_failures_are_not_unavailable()
    {
        Assert.False(DatabaseFailures.IsUnavailable(new InvalidOperationException("ConnectionStrings:Postgres yapılandırılmamış")));
        Assert.False(DatabaseFailures.IsUnavailable(new SocketException(10061)));
        Assert.False(DatabaseFailures.IsUnavailable(new TimeoutException()));
        Assert.False(DatabaseFailures.IsUnavailable(new NpgsqlException("protokol hatası")));              // nedeni bilinmeyen Npgsql hatası: temkinli 500
        Assert.False(DatabaseFailures.IsUnavailable(new OperationCanceledException()));
        Assert.False(DatabaseFailures.IsUnavailable(new AggregateException(new ArgumentException("x"))));
    }

    private sealed class ThrowingCatalog(Func<Exception> create) : ICatalogQueries
    {
        public Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken) => throw create();

        public Task<ProductPageDto?> GetProductsAsync(ProductListQuery query, CancellationToken cancellationToken) => throw create();

        public Task<ProductDetailDto?> GetProductBySlugAsync(string slug, CancellationToken cancellationToken) => throw create();
    }

    /// <summary>Gerçek bir PostgreSQL kısıt ihlali (aynı depo kodu iki kez) üretir ve istisnayı olduğu gibi yukarı bırakır.</summary>
    private sealed class ViolatingCatalog(string connectionString) : ICatalogQueries
    {
        public Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken) => ViolateAsync<IReadOnlyList<CategoryDto>>();

        public Task<ProductPageDto?> GetProductsAsync(ProductListQuery query, CancellationToken cancellationToken) => ViolateAsync<ProductPageDto?>();

        public Task<ProductDetailDto?> GetProductBySlugAsync(string slug, CancellationToken cancellationToken) => ViolateAsync<ProductDetailDto?>();

        private async Task<T> ViolateAsync<T>()
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "INSERT INTO warehouses (id, code, name, is_active, created_at, updated_at) VALUES (gen_random_uuid(), 'DUP-1', 'a', true, now(), now()), (gen_random_uuid(), 'DUP-1', 'b', true, now(), now())",
                connection);
            await command.ExecuteNonQueryAsync();
            throw new InvalidOperationException("erişilmez");
        }
    }
}

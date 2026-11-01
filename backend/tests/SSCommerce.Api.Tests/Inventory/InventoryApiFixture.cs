using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Database;
using SSCommerce.Application.Inventory;
using SSCommerce.Domain.Catalog;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Inventory;

[CollectionDefinition(Name)]
public sealed class InventoryApiCollection : ICollectionFixture<InventoryApiFixture>
{
    public const string Name = "inventory-api";
}

/// <summary>
/// Stok API testleri için tek izole, migration'lı PostgreSQL veritabanı ve iki uygulama örneği (Development, Production).
/// Rate limitler çok yüksektir; limit davranışı kendi küçük limitli örneğinde sınanır. Her test KENDİ deposunu, varyantını ve
/// bakiyesini kurar (isolation): testler yalnız kendi kimliklerini sorgular, sıra değişse sonuç değişmez.
/// Paylaşılan tek şey, hareketlerin aktör FK'si için gereken bir Admin kullanıcısıdır.
/// </summary>
public sealed class InventoryApiFixture : IAsyncLifetime
{
    public const string ProductionOrigin = "https://magaza.test";

    private PostgresTestDatabase? _database;
    private WebApplicationFactory<Program>? _development;
    private WebApplicationFactory<Program>? _production;

    public static IReadOnlyDictionary<string, string?> HighLimits { get; } = new Dictionary<string, string?>
    {
        ["Security:RateLimit:CredentialsPermitLimit"] = "100000",
        ["Security:RateLimit:GeneralPermitLimit"] = "100000",
        ["Security:RateLimit:MutationPermitLimit"] = "100000",
    };

    public PostgresTestDatabase Database => _database ?? throw SkipException();

    internal WebApplicationFactory<Program> DevelopmentFactory => _development ?? throw SkipException();

    internal WebApplicationFactory<Program> ProductionFactory => _production ?? throw SkipException();

    /// <summary>Tohumlama yardımcılarının aktör olarak kullandığı Admin (hareketlerin <c>actor_user_id</c> FK'si için).</summary>
    public Guid SeedActorId { get; private set; }

    public async ValueTask InitializeAsync()
    {
        _database = await PostgresTestDatabase.TryCreateAsync(migrate: true);
        if (_database is null)
        {
            return;
        }

        _development = ApiFactory.Create("Development", _database.ConnectionString, extraConfiguration: HighLimits);
        _production = ApiFactory.Create(
            "Production",
            _database.ConnectionString,
            extraConfiguration: new Dictionary<string, string?>(HighLimits) { ["Security:AllowedOrigins:0"] = ProductionOrigin });

        SeedActorId = (await AuthApiFixture.CreateUserWithRoleAsync(_development, AppRoles.Admin)).Id;
    }

    public async ValueTask DisposeAsync()
    {
        if (_development is not null)
        {
            await _development.DisposeAsync();
        }

        if (_production is not null)
        {
            await _production.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private static Exception SkipException()
    {
        Assert.Skip($"{PostgresTestDatabase.EnvironmentVariable} tanımlı değil; gerçek PostgreSQL testi atlandı (geçti sayılmaz).");
        return new InvalidOperationException();
    }

    // ---------- Oturumlar ----------

    /// <summary>Verilen rollerle yeni bir kullanıcı kurar (HTTP dışından), giriş yapar ve değişmez bir istemciye çevirir.</summary>
    internal async Task<ApiClient> LoginAsync(WebApplicationFactory<Program> factory, params string[] roles)
    {
        var (email, password, id) = await AuthApiFixture.CreateUserWithRoleAsync(factory, roles);
        var session = new TestSession(factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false }));
        var response = await session.LoginAsync(email, password);
        response.EnsureSuccessStatusCode();
        var cookie = string.Join("; ", session.Cookies.Select(c => $"{c.Key}={c.Value}"));
        return new ApiClient(factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false }), cookie, session.Csrf, id, email);
    }

    internal Task<ApiClient> AdminAsync(WebApplicationFactory<Program>? factory = null) => LoginAsync(factory ?? DevelopmentFactory, AppRoles.Admin);

    internal ApiClient Anonymous(WebApplicationFactory<Program>? factory = null) =>
        new((factory ?? DevelopmentFactory).CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false }), null, null, null);

    // ---------- Veri kurucular (fixture) ----------

    public async Task<Guid> CreateWarehouseAsync(bool active = true, string? name = null, string? code = null)
    {
        await using var db = Database.CreateDbContext();
        var warehouse = new SSCommerce.Domain.Inventory.Warehouse(code ?? $"W{Guid.NewGuid():N}"[..14].ToUpperInvariant(), name ?? $"Test Deposu {Guid.NewGuid():N}");
        warehouse.SetActive(active);
        db.Warehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    public async Task<(Guid Id, string Sku)> CreateVariantAsync(bool variantActive = true, bool productActive = true)
    {
        var product = await CreateProductAsync([variantActive], productActive);
        return product.Variants[0];
    }

    /// <summary>Görünür (aktif ürün + aktif kategori) bir ürün ve istenen aktiflikte varyantlar kurar.</summary>
    public async Task<(string Slug, string Name, Guid ProductId, IReadOnlyList<(Guid Id, string Sku)> Variants)> CreateProductAsync(
        IReadOnlyList<bool> variantActiveFlags,
        bool productActive = true)
    {
        await using var db = Database.CreateDbContext();
        var suffix = Guid.NewGuid().ToString("N");
        var category = new Category($"Stok Kategorisi {suffix}", $"stk-{suffix}");
        db.Categories.Add(category);
        var name = $"Stok Urunu {suffix}";
        var slug = $"stk-urun-{suffix}";
        var product = new Product(category.Id, name, slug, isDemo: true);
        var variants = new List<ProductVariant>();
        for (var i = 0; i < variantActiveFlags.Count; i++)
        {
            var variant = product.AddVariant($"T{suffix[..12].ToUpperInvariant()}-{i}", $"{name} v{i}", 100.00m + i, new Dictionary<string, string> { ["secenek"] = $"s{i}" });
            variant.SetActive(variantActiveFlags[i]);
            variants.Add(variant);
        }

        product.SetActive(productActive);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return (slug, name, product.Id, variants.Select(v => (v.Id, v.Sku)).ToList());
    }

    /// <summary>Bakiyeyi HAREKET bırakmadan doğrudan yazar (testin başlangıç durumu; defterde yalnız testin kendi işlemleri kalsın).</summary>
    public async Task SeedBalanceAsync(Guid warehouseId, Guid variantId, int onHand)
    {
        await ExecuteAsync(
            $"INSERT INTO inventory_balances (id, warehouse_id, product_variant_id, on_hand_quantity, version, created_at, updated_at) " +
            $"VALUES ('{Guid.CreateVersion7()}', '{warehouseId}', '{variantId}', {onHand}, 1, now(), now())");
    }

    /// <summary>Tek değer okur; NULL => default. <c>Nullable&lt;T&gt;</c> desteklenir (Convert.ChangeType desteklemez).</summary>
    public async Task<T?> ScalarAsync<T>(string sql)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await Database.OpenConnectionAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
        {
            return default;
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)(value.GetType() == target ? value : Convert.ChangeType(value, target));
    }

    public Task ExecuteAsync(string sql) => Database.ExecuteAsync(sql, TestContext.Current.CancellationToken);

    public async Task<long?> OnHandAsync(Guid warehouseId, Guid variantId) =>
        await ScalarAsync<long?>($"SELECT on_hand_quantity FROM inventory_balances WHERE warehouse_id = '{warehouseId}' AND product_variant_id = '{variantId}'");

    public async Task<long?> VersionAsync(Guid warehouseId, Guid variantId) =>
        await ScalarAsync<long?>($"SELECT version FROM inventory_balances WHERE warehouse_id = '{warehouseId}' AND product_variant_id = '{variantId}'");

    public async Task<long> MovementCountAsync(Guid variantId) =>
        await ScalarAsync<long>($"SELECT count(*) FROM stock_movements WHERE product_variant_id = '{variantId}'");

    public async Task<long> MovementDeltaSumAsync(Guid variantId) =>
        await ScalarAsync<long?>($"SELECT sum(quantity_delta) FROM stock_movements WHERE product_variant_id = '{variantId}'") ?? 0;

    public async Task<long> MovementKeyCountAsync(string operationKey) =>
        await ScalarAsync<long>($"SELECT count(*) FROM stock_movements WHERE operation_key = '{operationKey}'");

    public async Task<Guid?> MovementActorAsync(string operationKey) =>
        await ScalarAsync<Guid?>($"SELECT actor_user_id FROM stock_movements WHERE operation_key = '{operationKey}'");

    internal static string NewKey() => $"k-{Guid.NewGuid():N}";

    /// <summary>Servis, HTTP'siz çağırmak için (kendi kapsamında, kendi DbContext'iyle).</summary>
    internal async Task<T> WithServiceAsync<T>(Func<IInventoryService, Task<T>> action, WebApplicationFactory<Program>? factory = null)
    {
        using var scope = (factory ?? DevelopmentFactory).Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IInventoryService>());
    }

    internal StockCommand Command(
        Guid warehouseId,
        Guid variantId,
        int delta,
        string? key = null,
        Guid? actor = null,
        string reason = "Test nedeni",
        string? reference = null) =>
        new(
            delta > 0 ? StockOperations.Receive : StockOperations.Adjust,
            warehouseId,
            variantId,
            delta,
            delta > 0 ? SSCommerce.Domain.Inventory.StockOperationType.Receiving : SSCommerce.Domain.Inventory.StockOperationType.Adjustment,
            reason,
            reference,
            key ?? NewKey(),
            actor ?? SeedActorId);
}

/// <summary>
/// Oturum bilgisi değişmez bir HTTP istemcisi: eşzamanlı isteklerde güvenlidir (TestSession'ın cookie sözlüğü değildir).
/// CSRF belirteci oturumdan alınmıştır ve birden çok istekte geçerlidir.
/// </summary>
internal sealed class ApiClient(HttpClient client, string? cookie, string? csrf, Guid? userId, string? email = null)
{
    public string Email => email ?? throw new InvalidOperationException("Anonim istemcinin e-postası yok.");

    public Guid UserId => userId ?? throw new InvalidOperationException("Anonim istemcinin kullanıcı kimliği yok.");

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        string? rawBody = null,
        string? idempotencyKey = null,
        bool sendCsrf = true,
        string? origin = null,
        string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, url);
        if (rawBody is not null)
        {
            request.Content = new StringContent(rawBody, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        if (sendCsrf && csrf is not null)
        {
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Content-Length BİLDİRMEYEN (chunked) gövde: başlık ön kontrolünün atlatılamadığını kanıtlar.</summary>
    public async Task<HttpResponseMessage> SendUnknownLengthAsync(string url, string rawBody, string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new UnknownLengthContent(Encoding.UTF8.GetBytes(rawBody)) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        request.Headers.TransferEncodingChunked = true;
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(HttpMethod.Get, url);

    public Task<HttpResponseMessage> PostAsync(string url, object? body, string? idempotencyKey = null, bool sendCsrf = true, string? origin = null) =>
        SendAsync(HttpMethod.Post, url, body is null ? null : JsonSerializer.Serialize(body), idempotencyKey, sendCsrf, origin);

    public Task<HttpResponseMessage> PutAsync(string url, object? body, bool sendCsrf = true, string? origin = null) =>
        SendAsync(HttpMethod.Put, url, body is null ? null : JsonSerializer.Serialize(body), sendCsrf: sendCsrf, origin: origin);

    public Task<HttpResponseMessage> DeleteAsync(string url, bool sendCsrf = true, string? origin = null) =>
        SendAsync(HttpMethod.Delete, url, sendCsrf: sendCsrf, origin: origin);

    public Task<HttpResponseMessage> PatchAsync(string url, object? body, bool sendCsrf = true, string? origin = null) =>
        SendAsync(HttpMethod.Patch, url, body is null ? null : JsonSerializer.Serialize(body), sendCsrf: sendCsrf, origin: origin);

    public Task<HttpResponseMessage> ReceiveAsync(Guid warehouseId, Guid variantId, int quantity, string? key = null, string reason = "Mal kabul", string? reference = null) =>
        PostAsync("/api/v1/admin/inventory/receive", new { warehouseId, variantId, quantity, reason, reference }, key ?? InventoryApiFixture.NewKey());

    public Task<HttpResponseMessage> AdjustAsync(Guid warehouseId, Guid variantId, int quantityDelta, string type = "adjustment", string? key = null, string reason = "Sayim farki") =>
        PostAsync("/api/v1/admin/inventory/adjust", new { warehouseId, variantId, quantityDelta, type, reason }, key ?? InventoryApiFixture.NewKey());
}

internal static class ApiAssert
{
    public static async Task ProblemAsync(HttpResponseMessage response, int status, string code)
    {
        using var json = await response.JsonAsync();
        Assert.Equal((System.Net.HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(status, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
    }
}

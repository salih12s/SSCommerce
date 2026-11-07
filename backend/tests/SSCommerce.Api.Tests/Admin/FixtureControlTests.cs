using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Database;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.E2eHost;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Admin;

/// <summary>
/// Her test sınıfı için: izole gerçek PostgreSQL + Development API (giriş/cookie kontrolü için) + E2eHost'un loopback kontrol ucu (boş porta bağlı).
/// Neden fixture: veritabanı ve sunucu bir kez açılır; her test KENDİ kullanıcısını kurar (isolation).
/// </summary>
public sealed class FixtureControlHost : IAsyncLifetime
{
    private PostgresTestDatabase? _database;
    private WebApplicationFactory<Program>? _api;
    private WebApplication? _control;

    public PostgresTestDatabase Database => _database ?? throw Skip();

    internal WebApplicationFactory<Program> Api => _api ?? throw Skip();

    public string ControlUrl { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _database = await PostgresTestDatabase.TryCreateAsync(migrate: true);
        if (_database is null)
        {
            return;
        }

        _api = ApiFactory.Create("Development", _database.ConnectionString, extraConfiguration: InventoryApiFixture.HighLimits);
        // Port 0: işletim sistemi boş bir port verir (E2E'nin sabit 5082'siyle ya da paralel koşulla çakışmaz).
        _control = FixtureControl.StartControlServer(_database.ConnectionString, "http://127.0.0.1:0");
        ControlUrl = _control.Urls.Single();
    }

    public async ValueTask DisposeAsync()
    {
        if (_control is not null)
        {
            await _control.StopAsync();
            await _control.DisposeAsync();
        }

        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private static Exception Skip()
    {
        Assert.Skip($"{PostgresTestDatabase.EnvironmentVariable} tanımlı değil; gerçek PostgreSQL testi atlandı (geçti sayılmaz).");
        return new InvalidOperationException();
    }
}

/// <summary>
/// INTEGRATION testleri: E2eHost'un <c>/fixture/*</c> loopback ucu (E2E-only). Neden integration, neden unit değil: bu uçtaki hata SQL metninde
/// (eksik <c>@parametre</c> yer tutucusu) ve Npgsql bunu ANCAK gerçek PostgreSQL'e gönderilince söyler; mock bu hatayı hiç göremez (uç her çağrıda 500 dönüyordu).
/// Bu yüzden her test gerçek bir veritabanına SQL gönderir. Kapsam: rol ucu (asıl) + diğer uçların "500 değil" duman testi + üretim API'sinde uç YOKTUR kanıtı.
/// </summary>
public class FixtureControlTests(FixtureControlHost host) : IClassFixture<FixtureControlHost>
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<HttpResponseMessage> PostAsync(string path, object? body)
    {
        using var http = new HttpClient();
        return await http.PostAsJsonAsync(host.ControlUrl + path, body, Ct);
    }

    private async Task<HttpResponseMessage> PostRawAsync(string path, string rawJson)
    {
        using var http = new HttpClient();
        return await http.PostAsync(host.ControlUrl + path, Json(rawJson), Ct);
    }

    private static string Quote(string? value) => value is null ? "null" : System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>Rolsüz yeni hesap + giriş yapmış istemci. (Gerçek kayıt Customer verir; burada rol tablosu boş başlar: ekleme/çıkarma net görünsün.)</summary>
    private async Task<(ApiClient Client, string Email, string Password, Guid Id)> NewAccountAsync()
    {
        var (email, password, id) = await AuthApiFixture.CreateUserWithRoleAsync(host.Api);
        return (await LoginAsync(email, password, id), email, password, id);
    }

    private async Task<ApiClient> LoginAsync(string email, string password, Guid id)
    {
        var session = new TestSession(host.Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false }));
        var response = await session.LoginAsync(email, password);
        response.EnsureSuccessStatusCode();
        var cookie = string.Join("; ", session.Cookies.Select(c => $"{c.Key}={c.Value}"));
        return new ApiClient(host.Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false }), cookie, session.Csrf, id, email);
    }

    private Task<string> StampOfAsync(Guid userId) =>
        host.Database.ScalarAsync<string>($"SELECT \"SecurityStamp\" FROM \"AspNetUsers\" WHERE \"Id\" = '{userId}'", Ct)!;

    private async Task<long> RoleRowsAsync(Guid userId, Guid roleId) =>
        (await host.Database.ScalarAsync<long>($"SELECT count(*) FROM \"AspNetUserRoles\" WHERE \"UserId\" = '{userId}' AND \"RoleId\" = '{roleId}'", Ct));

    private static async Task<string[]> MeRolesAsync(ApiClient client)
    {
        var response = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await response.JsonAsync();
        return json.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray();
    }

    // ---------- /fixture/role: happy path ----------

    [Theory]
    [InlineData("Admin")]
    [InlineData("Warehouse")]
    public async Task Granting_admin_or_warehouse_is_204_adds_exactly_one_role_row_and_the_new_login_carries_the_role(string role)
    {
        var (oldSession, email, password, id) = await NewAccountAsync();
        Assert.Empty(await MeRolesAsync(oldSession));
        var roleId = role == "Admin" ? AppRoles.AdminId : AppRoles.WarehouseId;
        var stampBefore = await StampOfAsync(id);

        var response = await PostAsync("/fixture/role", new { email, role });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, await RoleRowsAsync(id, roleId));
        Assert.NotEqual(stampBefore, await StampOfAsync(id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldSession.GetAsync("/api/v1/auth/me")).StatusCode); // eski cookie düştü (güvenlik damgası yenilendi)
        var fresh = await LoginAsync(email, password, id);
        Assert.Equal([role], await MeRolesAsync(fresh)); // yeni girişte rol geçerli
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync(role == "Admin" ? "/api/v1/admin/me" : "/api/v1/admin/orders")).StatusCode);
    }

    [Fact]
    public async Task The_email_is_matched_case_insensitively_and_trimmed()
    {
        var (_, email, _, id) = await NewAccountAsync();

        var response = await PostAsync("/fixture/role", new { email = "  " + email.ToUpperInvariant() + " ", role = "Warehouse" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, await RoleRowsAsync(id, AppRoles.WarehouseId));
    }

    [Fact]
    public async Task Granting_the_same_role_twice_is_idempotent_with_one_role_row_and_the_second_call_is_also_204()
    {
        var (_, email, _, id) = await NewAccountAsync();

        var first = await PostAsync("/fixture/role", new { email, role = "Admin" });
        var second = await PostAsync("/fixture/role", new { email, role = "Admin" });

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(1, await RoleRowsAsync(id, AppRoles.AdminId)); // ON CONFLICT DO NOTHING: ikinci satır yok, hata yok
    }

    [Fact]
    public async Task Both_roles_can_be_granted_to_the_same_account()
    {
        var (_, email, password, id) = await NewAccountAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync("/fixture/role", new { email, role = "Admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync("/fixture/role", new { email, role = "Warehouse" })).StatusCode);

        Assert.Equal(["Admin", "Warehouse"], (await MeRolesAsync(await LoginAsync(email, password, id))).Order(StringComparer.Ordinal));
    }

    // ---------- /fixture/role: negatifler ----------

    [Fact]
    public async Task An_unknown_account_is_404_and_changes_no_role_row()
    {
        var before = await host.Database.ScalarAsync<long>("SELECT count(*) FROM \"AspNetUserRoles\"", Ct);

        var response = await PostAsync("/fixture/role", new { email = $"yok-{Guid.NewGuid():N}@example.test", role = "Admin" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await host.Database.ScalarAsync<long>("SELECT count(*) FROM \"AspNetUserRoles\"", Ct));
    }

    [Theory]
    [InlineData("Customer")] // kayıtla zaten varsayılan rol; uç yalnız Admin/Warehouse verir (davranış değiştirilmedi)
    [InlineData("admin")]
    [InlineData("ADMIN")]
    [InlineData("warehouse")]
    [InlineData(" Admin")]
    [InlineData("Admin ")]
    [InlineData("Superuser")]
    [InlineData("Admin,Warehouse")]
    [InlineData("")]
    public async Task Only_the_exact_names_Admin_and_Warehouse_are_accepted_everything_else_is_400_and_changes_nothing(string role)
    {
        var (session, email, _, id) = await NewAccountAsync();
        var stamp = await StampOfAsync(id);

        var response = await PostRawAsync("/fixture/role", $"{{\"email\":{Quote(email)},\"role\":{Quote(role)}}}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(stamp, await StampOfAsync(id));
        Assert.Equal(0, await host.Database.ScalarAsync<long>($"SELECT count(*) FROM \"AspNetUserRoles\" WHERE \"UserId\" = '{id}'", Ct));
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/auth/me")).StatusCode); // oturum bozulmadı
    }

    [Fact]
    public async Task A_missing_role_is_400()
    {
        var (_, email, _, _) = await NewAccountAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await PostRawAsync("/fixture/role", $"{{\"email\":{Quote(email)}}}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRawAsync("/fixture/role", $"{{\"email\":{Quote(email)},\"role\":null}}")).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_or_blank_email_is_400(string? email)
    {
        var response = await PostRawAsync("/fixture/role", $"{{\"email\":{Quote(email)},\"role\":\"Admin\"}}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_email_longer_than_254_characters_is_400_while_exactly_254_reaches_the_lookup_and_is_404()
    {
        var tooLong = new string('a', 242) + "@example.test"; // 255
        var boundary = new string('a', 241) + "@example.test"; // 254

        Assert.Equal(255, tooLong.Length);
        Assert.Equal(254, boundary.Length);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/fixture/role", new { email = tooLong, role = "Admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/fixture/role", new { email = boundary, role = "Admin" })).StatusCode);
    }

    [Theory]
    [InlineData("{\"email\":123,\"role\":\"Admin\"}")]
    [InlineData("{\"email\":[\"a\"],\"role\":\"Admin\"}")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task A_wrong_typed_or_malformed_body_is_400_never_500(string body)
    {
        var response = await PostRawAsync("/fixture/role", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task User_input_is_bound_as_a_parameter_so_an_injection_like_email_is_just_an_unknown_account()
    {
        var before = await host.Database.ScalarAsync<long>("SELECT count(*) FROM \"AspNetUserRoles\"", Ct);

        var response = await PostAsync("/fixture/role", new { email = "x' OR '1'='1", role = "Admin" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // string birleştirilseydi herhangi bir kullanıcı eşleşirdi
        Assert.Equal(before, await host.Database.ScalarAsync<long>("SELECT count(*) FROM \"AspNetUserRoles\"", Ct));
    }

    [Fact]
    public async Task Only_loopback_json_posts_are_served_a_get_or_a_non_json_body_is_400()
    {
        using var http = new HttpClient();

        var get = await http.GetAsync(host.ControlUrl + "/fixture/role", Ct);
        var text = await http.PostAsync(host.ControlUrl + "/fixture/role", new StringContent("{}", Encoding.UTF8, "text/plain"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);
    }

    // ---------- Diğer fixture uçları: aynı "eksik yer tutucu" tuzağı var mı? (duman: gerçek SQL gider, 500 dönmez) ----------

    [Fact]
    public async Task The_stock_price_active_and_order_status_endpoints_run_their_sql_against_postgres_without_a_500()
    {
        // Depo + varyant: stok ucu E2E-DEPO'yu ister (Program bunu SeedStockAsync ile kurar; burada yalnız bu testin kendi izole veritabanında).
        await FixtureControl.SeedStockAsync(host.Database.ConnectionString);
        var sku = $"FX{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        await using (var db = host.Database.CreateDbContext())
        {
            var category = new SSCommerce.Domain.Catalog.Category($"Fixture {sku}", $"fx-{sku.ToLowerInvariant()}");
            db.Categories.Add(category);
            var product = new SSCommerce.Domain.Catalog.Product(category.Id, $"Fixture urun {sku}", $"fx-urun-{sku.ToLowerInvariant()}", isDemo: true);
            product.AddVariant(sku, $"Fixture varyant {sku}", 100.00m, new Dictionary<string, string> { ["secenek"] = "s" });
            db.Products.Add(product);
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync("/fixture/stock", new { sku, quantity = 7 })).StatusCode);
        Assert.Equal(7, await host.Database.ScalarAsync<long>(
            $"SELECT b.on_hand_quantity FROM inventory_balances b JOIN product_variants v ON v.id = b.product_variant_id JOIN warehouses w ON w.id = b.warehouse_id WHERE v.sku = '{sku}' AND w.code = 'E2E-DEPO'", Ct));
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync("/fixture/price", new { sku, priceMinor = 12_345 })).StatusCode);
        Assert.Equal(123.45m, await host.Database.ScalarAsync<decimal>($"SELECT price FROM product_variants WHERE sku = '{sku}'", Ct));
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync("/fixture/active", new { sku, isActive = false })).StatusCode);
        Assert.False(await host.Database.ScalarAsync<bool>($"SELECT is_active FROM product_variants WHERE sku = '{sku}'", Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/fixture/stock", new { sku = "YOK-SKU-1", quantity = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/fixture/price", new { sku = "YOK-SKU-1", priceMinor = 100 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/fixture/active", new { sku = "YOK-SKU-1", isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/fixture/order-status", new { orderId = Guid.NewGuid(), status = "processing" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/fixture/order-status", new { orderId = Guid.NewGuid(), status = "cancelled" })).StatusCode);
    }

    // ---------- Üretim API'sinde /fixture YOKTUR ----------

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task The_real_api_has_no_fixture_route_in_any_environment_and_answers_404_for_every_fixture_path(string environment)
    {
        await using var factory = ApiFactory.Create(
            environment,
            host.Database.ConnectionString,
            extraConfiguration: new Dictionary<string, string?>(InventoryApiFixture.HighLimits) { ["Security:AllowedOrigins:0"] = InventoryApiFixture.ProductionOrigin });

        var patterns = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToList();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", InventoryApiFixture.ProductionOrigin);

        Assert.NotEmpty(patterns); // rota tablosu gerçekten okundu
        Assert.DoesNotContain(patterns, p => p.Contains("fixture", StringComparison.OrdinalIgnoreCase));
        foreach (var path in new[] { "/fixture/role", "/fixture/stock", "/fixture/price", "/fixture/active", "/fixture/order-status", "/api/v1/fixture/role" })
        {
            var response = await client.PostAsJsonAsync(path, new { email = "a@example.test", role = "Admin" }, Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public void The_real_api_assembly_does_not_reference_the_e2e_host()
    {
        var referenced = typeof(Program).Assembly.GetReferencedAssemblies().Select(a => a.Name);

        Assert.DoesNotContain("SSCommerce.E2eHost", referenced);
    }
}

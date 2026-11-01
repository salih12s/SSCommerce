using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SSCommerce.Api.Tests.Database;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Auth;

[CollectionDefinition(Name)]
public sealed class AuthApiCollection : ICollectionFixture<AuthApiFixture>
{
    public const string Name = "auth-api";
}

/// <summary>
/// Auth testleri için tek izole PostgreSQL veritabanı (migration'lar sıfırdan: Catalog + Identity) ve iki uygulama örneği:
/// Development ve Production. Her test KENDİ rastgele kullanıcısını kurar (isolation); gerçek e-posta/parola yoktur.
/// Rate limit bu iki örnekte çok yüksektir; limit davranışı kendi küçük limitli örneğinde sınanır.
/// </summary>
public sealed class AuthApiFixture : IAsyncLifetime
{
    public const string ProductionOrigin = "https://magaza.test";

    private PostgresTestDatabase? _database;
    private WebApplicationFactory<Program>? _development;
    private WebApplicationFactory<Program>? _production;

    public PostgresTestDatabase Database => _database ?? throw SkipException();

    internal WebApplicationFactory<Program> DevelopmentFactory => _development ?? throw SkipException();

    internal WebApplicationFactory<Program> ProductionFactory => _production ?? throw SkipException();

    public static IReadOnlyDictionary<string, string?> HighLimits { get; } = new Dictionary<string, string?>
    {
        ["Security:RateLimit:CredentialsPermitLimit"] = "100000",
        ["Security:RateLimit:GeneralPermitLimit"] = "100000",
    };

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

    internal TestSession NewSession(WebApplicationFactory<Program> factory) =>
        new(factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false }));

    internal TestSession Dev() => NewSession(DevelopmentFactory);

    internal TestSession Prod() => NewSession(ProductionFactory);

    /// <summary>Rastgele, ilkelerimizi sağlayan (≥10 karakter, harf+rakam) parola. Gerçek sır değildir.</summary>
    public static string NewPassword() => "Pw1-" + Guid.NewGuid().ToString("N");

    public static string NewEmail() => $"u{Guid.NewGuid():N}@example.test";

    /// <summary>
    /// Rolü olan kullanıcıyı HTTP DIŞINDAN, UserManager ile kurar (rol yükseltmenin HTTP yolu yok, D-032).
    /// Hem Admin hem Warehouse gibi hesapları test için üretir.
    /// </summary>
    internal static async Task<(string Email, string Password, Guid Id)> CreateUserWithRoleAsync(
        WebApplicationFactory<Program> factory,
        params string[] roles)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var email = NewEmail();
        var password = NewPassword();
        var user = new AppUser { UserName = email, Email = email };
        var created = await users.CreateAsync(user, password);
        Assert.True(created.Succeeded, string.Join(',', created.Errors.Select(e => e.Code)));
        foreach (var role in roles)
        {
            var added = await users.AddToRoleAsync(user, role);
            Assert.True(added.Succeeded, string.Join(',', added.Errors.Select(e => e.Code)));
        }

        return (email, password, user.Id);
    }

    private static Exception SkipException()
    {
        Assert.Skip($"{PostgresTestDatabase.EnvironmentVariable} tanımlı değil; gerçek PostgreSQL testi atlandı (geçti sayılmaz).");
        return new InvalidOperationException();
    }
}

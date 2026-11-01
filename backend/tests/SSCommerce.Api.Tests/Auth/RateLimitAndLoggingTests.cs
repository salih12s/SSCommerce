using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SSCommerce.Api.Tests.Auth;

/// <summary>Rate limit (429) ve günlüklerde kişisel veri/sır olmaması.</summary>
[Collection(AuthApiCollection.Name)]
public class RateLimitAndLoggingTests(AuthApiFixture fixture)
{
    private static IReadOnlyDictionary<string, string?> Limits(int credentials, int general, int windowSeconds = 600) =>
        new Dictionary<string, string?>
        {
            ["Security:RateLimit:CredentialsPermitLimit"] = credentials.ToString(),
            ["Security:RateLimit:GeneralPermitLimit"] = general.ToString(),
            ["Security:RateLimit:WindowSeconds"] = windowSeconds.ToString(), // test sırasında pencere dolmaz (flaky olmaz)
        };

    // ---------- Rate limit ----------

    [Fact]
    public async Task Login_over_the_limit_returns_429_problem_json_with_Retry_After()
    {
        await using var factory = ApiFactory.Create("Development", fixture.Database.ConnectionString, extraConfiguration: Limits(credentials: 3, general: 100));
        var session = fixture.NewSession(factory);
        var token = await session.FetchCsrfAsync();
        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;

        for (var i = 0; i < 5; i++)
        {
            last = await session.PostJsonAsync("/api/v1/auth/login", new { email = AuthApiFixture.NewEmail(), password = "Yanlis-parola-1" }, token);
            statuses.Add(last.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal("application/problem+json", last!.Content.Headers.ContentType?.MediaType);
        using var json = await last.JsonAsync();
        Assert.Equal("rate_limited", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(429, json.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        Assert.True(last.Headers.RetryAfter?.Delta is { } delta && delta.TotalSeconds is > 0 and <= 600, "Retry-After saniye cinsinden olmalı.");
        Assert.Equal("no-store", last.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Register_and_login_share_the_credentials_limit()
    {
        await using var factory = ApiFactory.Create("Development", fixture.Database.ConnectionString, extraConfiguration: Limits(credentials: 2, general: 100));
        var session = fixture.NewSession(factory);
        var token = await session.FetchCsrfAsync();

        var register = await session.PostJsonAsync("/api/v1/auth/register", new { email = AuthApiFixture.NewEmail(), password = AuthApiFixture.NewPassword() }, token);
        var login1 = await session.PostJsonAsync("/api/v1/auth/login", new { email = "x@y.z", password = "p" }, token);
        var login2 = await session.PostJsonAsync("/api/v1/auth/login", new { email = "x@y.z", password = "p" }, token);

        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, login1.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, login2.StatusCode);
    }

    [Fact]
    public async Task Rate_limited_requests_do_not_reach_the_login_logic()
    {
        await using var factory = ApiFactory.Create("Development", fixture.Database.ConnectionString, extraConfiguration: Limits(credentials: 1, general: 100));
        var session = fixture.NewSession(factory);
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        // İlk istek kaydı yapar (limit dolar); sonraki "brute force" denemeleri 429'da kalır ve sayaç artırmaz.
        var token = await session.FetchCsrfAsync();
        await session.PostJsonAsync("/api/v1/auth/register", new { email, password }, token);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, (await session.PostJsonAsync("/api/v1/auth/login", new { email, password = "Yanlis-parola-1" }, token)).StatusCode);
        }

        Assert.Equal(0, await fixture.Database.ScalarAsync<long>($"SELECT \"AccessFailedCount\" FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task General_auth_endpoints_have_their_own_larger_limit()
    {
        await using var factory = ApiFactory.Create("Development", fixture.Database.ConnectionString, extraConfiguration: Limits(credentials: 100, general: 2));
        var session = fixture.NewSession(factory);

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            codes.Add((await session.GetAsync("/api/v1/auth/csrf")).StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], codes);
        // Credentials sınırı ayrıdır: login hâlâ çalışır (CSRF reddi 400 gelir, 429 değil).
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await session.PostJsonAsync("/api/v1/auth/login", new { email = "a@b.c", password = "x" }, "sahte")).StatusCode);
    }

    [Fact]
    public async Task Normal_flow_is_not_throttled_at_the_default_limits()
    {
        await using var factory = ApiFactory.Create("Development", fixture.Database.ConnectionString); // yapılandırma varsayılanı: 10/dk
        var session = fixture.NewSession(factory);
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();

        Assert.Equal(HttpStatusCode.Created, (await session.RegisterAsync(email, password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await session.LogoutAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync(email, password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Catalog_and_me_are_not_rate_limited_by_the_auth_limits()
    {
        // Credentials ve General sınırı 1: login (1 csrf + 1 login) hakkı tüketir; sonrasında /me ve katalog defalarca çağrılır.
        await using var factory = ApiFactory.Create("Development", fixture.Database.ConnectionString, extraConfiguration: Limits(credentials: 1, general: 1));
        var (email, password, _) = await AuthApiFixture.CreateUserWithRoleAsync(factory, "Customer");
        var session = fixture.NewSession(factory);
        var token = await session.FetchCsrfAsync();
        var login = await session.PostJsonAsync("/api/v1/auth/login", new { email, password }, token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await session.GetAsync("/api/v1/auth/csrf")).StatusCode); // sınır gerçekten dolu

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/products")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/categories")).StatusCode);
        }
    }

    // ---------- Günlükler ----------

    private sealed class MemoryLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, Lines);

        public void Dispose()
        {
        }

        private sealed class Sink(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                // Hem biçimlendirilmiş ileti hem yapısal alanlar hem istisna metni taranır.
                var fields = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(' ', pairs.Select(p => $"{p.Key}={p.Value}"))
                    : string.Empty;
                lines.Enqueue($"{category} {logLevel} {formatter(state, exception)} {fields} {exception}");
            }
        }
    }

    [Fact]
    public async Task Logs_never_contain_emails_passwords_or_cookie_and_token_values()
    {
        var logs = new MemoryLogs();
        await using var factory = ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            services =>
            {
                services.AddSingleton<ILoggerProvider>(logs);
                services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace)); // en ayrıntılı seviye: sızıntı varsa görünür
            },
            Limits(credentials: 1000, general: 1000));
        var session = fixture.NewSession(factory);
        var localPart = "gizli" + Guid.NewGuid().ToString("N");
        var email = $"{localPart}@example.test";
        var password = AuthApiFixture.NewPassword();
        var wrong = "Yanlis-" + Guid.NewGuid().ToString("N");

        var register = await session.RegisterAsync(email, password);                  // başarılı kayıt
        var authCookie = session.Cookies["ssc.auth"];
        var csrfToken = session.Csrf!;
        await session.RegisterAsync(email, password);                                   // yinelenen e-posta (409)
        await session.RegisterAsync(AuthApiFixture.NewEmail(), "kisa");                 // zayıf parola (400)
        await session.LogoutAsync();
        await session.LoginAsync(email, password);                                      // başarılı giriş
        var session2 = fixture.NewSession(factory);
        for (var i = 0; i < 5; i++)
        {
            await session2.LoginAsync(email, wrong);                                    // yanlış parola x5 => kilit
        }

        await session2.LoginAsync($"yok{localPart}@example.test", wrong);               // bilinmeyen hesap
        await session2.SendAsync(HttpMethod.Get, "/api/v1/auth/me", sendCookies: false);

        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var text = string.Join('\n', logs.Lines);
        Assert.NotEmpty(logs.Lines); // günlük gerçekten toplandı (boş geçtiği için "temiz" sanılmasın)
        Assert.DoesNotContain(localPart, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(password, text);
        Assert.DoesNotContain(wrong, text);
        Assert.DoesNotContain(authCookie, text);
        Assert.DoesNotContain(csrfToken, text);
        // Kullanıcı Id ile loglanır (kimlik, ama kişisel veri değil).
        Assert.Contains("Kullanıcı kaydedildi", text);
    }
}

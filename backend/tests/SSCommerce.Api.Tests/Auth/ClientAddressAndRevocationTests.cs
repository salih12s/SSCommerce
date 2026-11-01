using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Auth;

/// <summary>TestServer'da bağlantının uzak adresi yoktur; <c>X-Test-Peer</c> başlığı onu belirler (ForwardedHeaders'tan ÖNCE çalışır).</summary>
internal sealed class FakePeerStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Peer", out var peer) && IPAddress.TryParse(peer, out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            return nextMiddleware(context);
        });
        next(app);
    };
}

internal sealed class ThrowingSessionRevoker : ISessionRevoker
{
    public Task RevokeAsync(Guid userId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("db://gizli-baglanti Password=hunter2");
}

/// <summary>
/// Rate limit IP bölümlemesi (proxy arkasında doğru istemci) ve çıkışta oturum iptali başarısız olursa durumun dürüstçe bildirilmesi.
/// ForwardedHeaders: yalnız güvenilen proxy'lerden (loopback + <c>Security:TrustedProxies</c>) gelen X-Forwarded-For dikkate alınır ve
/// ForwardLimit=1 olduğundan SAĞDAN İLK adres (yani güvenilen proxy'nin gördüğü gerçek istemci) seçilir; soldaki girdiler istemci uydurmasıdır.
/// </summary>
[Collection(AuthApiCollection.Name)]
public class ClientAddressAndRevocationTests(AuthApiFixture fixture)
{
    private WebApplicationFactory<Program> LimitedFactory(int credentialsLimit, IDictionary<string, string?>? more = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["Security:RateLimit:CredentialsPermitLimit"] = credentialsLimit.ToString(),
            ["Security:RateLimit:GeneralPermitLimit"] = "100000",
            ["Security:RateLimit:WindowSeconds"] = "600",
        };
        foreach (var (key, value) in more ?? new Dictionary<string, string?>())
        {
            config[key] = value;
        }

        return ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            services => services.AddSingleton<IStartupFilter, FakePeerStartupFilter>(),
            config);
    }

    private static async Task<HttpStatusCode> LoginAttemptAsync(TestSession session, string token, string peer, string? forwardedFor)
    {
        var headers = new Dictionary<string, string> { ["X-Test-Peer"] = peer, ["X-CSRF-TOKEN"] = token };
        if (forwardedFor is not null)
        {
            headers["X-Forwarded-For"] = forwardedFor;
        }

        var response = await session.SendAsync(
            HttpMethod.Post,
            "/api/v1/auth/login",
            """{"email":"yok@example.test","password":"Yanlis-parola-1"}""",
            extraHeaders: headers);
        return response.StatusCode;
    }

    private async Task<(TestSession Session, string Token)> NewSessionAsync(WebApplicationFactory<Program> factory)
    {
        var session = fixture.NewSession(factory);
        return (session, await session.FetchCsrfAsync());
    }

    [Fact]
    public async Task Behind_a_trusted_loopback_proxy_different_clients_get_separate_rate_limit_buckets()
    {
        await using var factory = LimitedFactory(credentialsLimit: 3);
        var (session, token) = await NewSessionAsync(factory);

        var clientA = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            clientA.Add(await LoginAttemptAsync(session, token, "127.0.0.1", "203.0.113.10"));
        }

        var clientB = await LoginAttemptAsync(session, token, "127.0.0.1", "203.0.113.11");

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], clientA);
        Assert.Equal(HttpStatusCode.Unauthorized, clientB); // başka istemci A'nın kovasından etkilenmez (hepsi 127.0.0.1'den gelse de)
    }

    [Fact]
    public async Task Without_forwarding_all_clients_behind_the_proxy_share_one_bucket_which_is_why_the_header_matters()
    {
        await using var factory = LimitedFactory(credentialsLimit: 3);
        var (session, token) = await NewSessionAsync(factory);

        for (var i = 0; i < 3; i++)
        {
            await LoginAttemptAsync(session, token, "127.0.0.1", forwardedFor: null);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAttemptAsync(session, token, "127.0.0.1", forwardedFor: null));
    }

    [Fact]
    public async Task A_forged_forwarded_for_from_an_untrusted_source_cannot_dodge_the_limit()
    {
        await using var factory = LimitedFactory(credentialsLimit: 3);
        var (session, token) = await NewSessionAsync(factory);
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 6; i++)
        {
            // Saldırgan (güvenilmeyen 198.51.100.7) her istekte başka bir "istemci adresi" uydurur.
            statuses.Add(await LoginAttemptAsync(session, token, "198.51.100.7", $"203.0.113.{100 + i}"));
        }

        Assert.Equal(
            [HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests],
            statuses);
    }

    [Fact]
    public async Task The_rightmost_forwarded_address_is_used_so_spoofed_left_entries_do_not_help_a_client()
    {
        await using var factory = LimitedFactory(credentialsLimit: 3);
        var (session, token) = await NewSessionAsync(factory);
        var sameRealClient = new List<HttpStatusCode>();

        // Aynı gerçek istemci (sağdaki, proxy'nin eklediği 203.0.113.50), soldaki uydurma adresleri döndürüyor.
        for (var i = 0; i < 4; i++)
        {
            sameRealClient.Add(await LoginAttemptAsync(session, token, "127.0.0.1", $"9.9.9.{i}, 203.0.113.50"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, sameRealClient[3]);

        // Ters yön: sol sabit, sağ farklı => farklı gerçek istemciler, birbirinin kovasını doldurmaz.
        var different = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            different.Add(await LoginAttemptAsync(session, token, "127.0.0.1", $"9.9.9.9, 203.0.113.{60 + i}"));
        }

        Assert.All(different, status => Assert.Equal(HttpStatusCode.Unauthorized, status));
    }

    [Fact]
    public async Task A_configured_trusted_proxy_is_honoured_and_the_same_address_is_ignored_when_not_configured()
    {
        await using var trusted = LimitedFactory(3, new Dictionary<string, string?> { ["Security:TrustedProxies:0"] = "10.0.0.5" });
        var (session, token) = await NewSessionAsync(trusted);
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAttemptAsync(session, token, "10.0.0.5", "203.0.113.1"));
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await LoginAttemptAsync(session, token, "10.0.0.5", $"203.0.113.{10 + i}")); // farklı istemciler
        }

        await using var untrusted = LimitedFactory(3);
        var (session2, token2) = await NewSessionAsync(untrusted);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add(await LoginAttemptAsync(session2, token2, "10.0.0.5", $"203.0.113.{10 + i}"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[3]); // yapılandırılmamış proxy'nin XFF'i yok sayılır
    }

    // ---------- Çıkışta iptal başarısızsa başarı DÖNÜLMEZ ----------

    [Fact]
    public async Task Logout_returns_503_logout_failed_when_server_side_revocation_fails_and_still_deletes_the_browser_cookie()
    {
        await using var factory = ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            services =>
            {
                services.RemoveAll<ISessionRevoker>();
                services.AddScoped<ISessionRevoker, ThrowingSessionRevoker>();
            },
            AuthApiFixture.HighLimits);
        var session = fixture.NewSession(factory);
        await session.RegisterAsync(AuthApiFixture.NewEmail(), AuthApiFixture.NewPassword());

        var logout = await session.LogoutAsync();
        var raw = await logout.TextAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, logout.StatusCode);
        Assert.Equal("application/problem+json", logout.Content.Headers.ContentType?.MediaType);
        using var json = System.Text.Json.JsonDocument.Parse(raw);
        Assert.Equal("logout_failed", json.RootElement.GetProperty("code").GetString());
        Assert.Contains(TestSession.ParseSetCookies(logout), c => c.Name == "ssc.auth" && c.IsDeletion); // tarayıcı cookie'si yine silinir
        Assert.DoesNotContain("hunter2", raw);
        Assert.DoesNotContain("gizli-baglanti", raw);
        Assert.DoesNotContain("InvalidOperationException", raw);
    }

    [Fact]
    public async Task A_failed_revocation_is_logged_with_the_user_id_but_without_the_exception_message_reaching_the_client()
    {
        var logs = new List<string>();
        await using var factory = ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            services =>
            {
                services.RemoveAll<ISessionRevoker>();
                services.AddScoped<ISessionRevoker, ThrowingSessionRevoker>();
                services.AddSingleton<ILoggerProvider>(new ListLogger(logs));
            },
            AuthApiFixture.HighLimits);
        var session = fixture.NewSession(factory);
        await session.RegisterAsync(AuthApiFixture.NewEmail(), AuthApiFixture.NewPassword());

        await session.LogoutAsync();

        Assert.Contains(logs, l => l.Contains("iptal edilemedi", StringComparison.Ordinal));
    }

    private sealed class ListLogger(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Sink(sink);

        public void Dispose()
        {
        }

        private sealed class Sink(List<string> list) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (list)
                {
                    list.Add(formatter(state, exception));
                }
            }
        }
    }
}

using System.Net;
using Microsoft.Extensions.Logging;
using SSCommerce.Api.Tests.Auth;
using static SSCommerce.Api.Tests.Observability.ObservabilityKit;

namespace SSCommerce.Api.Tests.Observability;

/// <summary>
/// Faz 9 B2/B3: istek tamamlanma günlüğü ve sızıntı testi. Kavram: "log hijyeni": günlük dosyaları yedeklenir, aranır, ekiplerle paylaşılır;
/// içine sızan bir parola ya da oturum çerezi, veritabanındakinden çok daha geniş kitleye ulaşır.
/// </summary>
[Collection(AuthApiCollection.Name)]
public class RequestLoggingTests(AuthApiFixture fixture)
{
    private static readonly Dictionary<string, string?> Limits = new()
    {
        ["Security:RateLimit:CredentialsPermitLimit"] = "1000",
        ["Security:RateLimit:GeneralPermitLimit"] = "1000",
    };

    [Fact]
    public async Task Login_leaves_one_completion_line_and_no_email_password_cookie_token_query_or_body_in_any_log_output()
    {
        var logs = new CapturingLogs();
        await using var factory = ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            services => AddCapturingLogs(services, logs),
            Limits);
        var session = fixture.NewSession(factory);
        var localPart = "sizinti" + Guid.NewGuid().ToString("N");
        var email = $"{localPart}@example.test";
        var password = AuthApiFixture.NewPassword();
        var queryMarker = "SORGU" + Guid.NewGuid().ToString("N");
        var bodyMarker = "GOVDE" + Guid.NewGuid().ToString("N");

        await session.RegisterAsync(email, password);
        await session.LogoutAsync();
        var csrf = await session.FetchCsrfAsync();
        var login = await session.PostJsonAsync($"/api/v1/auth/login?x={queryMarker}", new { email, password, ekstra = bodyMarker }, csrf);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var authCookie = session.Cookies["ssc.auth"];
        await session.GetAsync("/api/v1/auth/me", new Dictionary<string, string> { ["X-Request-Id"] = "izlenebilir-istek-1" });

        var text = logs.AllText;
        Assert.NotEmpty(logs.Entries); // günlük gerçekten toplandı ("temiz" sanılmasın)
        foreach (var secret in new[] { email, localPart, password, authCookie, csrf, queryMarker, bodyMarker, "ssc.auth" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }

        // Tamamlanma satırı: yöntem, route ŞABLONU, durum, süre, requestId.
        var completions = logs.Entries.Where(e => e.Message.StartsWith("İstek tamamlandı", StringComparison.Ordinal)).ToList();
        var loginLine = Assert.Single(completions, e => Equals(e.State.GetValueOrDefault("Route"), "/api/v1/auth/login") && Equals(e.State.GetValueOrDefault("Method"), "POST"));
        Assert.Equal(200, loginLine.State["StatusCode"]);
        Assert.True(Convert.ToInt64(loginLine.State["ElapsedMs"]) >= 0);
        Assert.Matches("^[0-9a-f]{32}$", (string)loginLine.State["RequestId"]!);
        Assert.Equal(LogLevel.Information, loginLine.Level);
        Assert.Equal(["Method", "Route", "StatusCode", "ElapsedMs", "RequestId"], loginLine.State.Keys.Where(k => k != "{OriginalFormat}"));

        // Kapsamda RequestId var; gelen geçerli başlık aynen kullanılır.
        var meLine = Assert.Single(completions, e => Equals(e.State.GetValueOrDefault("Route"), "/api/v1/auth/me"));
        Assert.Equal("izlenebilir-istek-1", meLine.State["RequestId"]);
        Assert.Contains(meLine.Scopes, scope => scope.TryGetValue("RequestId", out var id) && Equals(id, "izlenebilir-istek-1"));
    }

    [Fact]
    public async Task With_the_default_log_level_health_probes_leave_no_completion_line()
    {
        var logs = new CapturingLogs();
        await using var factory = ApiFactory.Create("Development", fixture.Database.ConnectionString, services => AddCapturingLogs(services, logs), Limits);
        using var client = factory.CreateClient();

        await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logs.Entries, e => e.Message.StartsWith("İstek tamamlandı", StringComparison.Ordinal) && e.Message.Contains("/health/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Health_probes_are_logged_at_Debug_not_Information_and_other_logs_inside_a_request_carry_the_RequestId_scope()
    {
        var logs = new CapturingLogs();
        await using var factory = ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            services => AddCapturingLogs(services, logs),
            new Dictionary<string, string?>(Limits) { ["Logging:LogLevel:SSCommerce.Api.Observability"] = "Debug" }); // Debug'ı açıkça isteyen yapılandırma
        using var client = factory.CreateClient();

        await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        var failedLogin = fixture.NewSession(factory);
        var csrf = await failedLogin.FetchCsrfAsync();
        await failedLogin.PostJsonAsync("/api/v1/auth/login", new { email = AuthApiFixture.NewEmail(), password = "Yanlis-parola-1" }, csrf);

        var probe = Assert.Single(logs.Entries, e => e.Message.Contains("/health/live", StringComparison.Ordinal) && e.Message.StartsWith("İstek tamamlandı", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Debug, probe.Level);
        // Kimlik doğrulama günlükleri (istek içinde yazılanlar) aynı kapsam sayesinde RequestId taşır.
        // (ASP.NET'in kendi RequestId kapsamı "0HN...:0000000N" biçimindedir; bizimki 32 onaltılık karakterdir.)
        var ours = new System.Text.RegularExpressions.Regex("^[0-9a-f]{32}$");
        var inside = logs.Entries
            .Where(e => !e.Message.StartsWith("İstek tamamlandı", StringComparison.Ordinal))
            .Where(e => e.Scopes.Any(scope => scope.TryGetValue("RequestId", out var id) && id is string text && ours.IsMatch(text)))
            .ToList();
        Assert.NotEmpty(inside);
    }
}

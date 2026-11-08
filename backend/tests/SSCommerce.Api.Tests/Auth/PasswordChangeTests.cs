using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SSCommerce.Api.Tests.Auth;

/// <summary>
/// MD-2: <c>GET /auth/me</c> → <c>createdAt</c> ve <c>POST /auth/password</c> (D-090). Gerçek PostgreSQL, tam HTTP hattı.
/// Öncelik sırası para/yetki/eşzamanlılık: oturum iptali (eski oturum 401, değiştiren oturum açık), yanlış parola → kilit sayacı,
/// yazma ucu korumaları (anonim, CSRF, Origin, hız sınırı), parola sızıntısı, yarış.
/// </summary>
[Collection(AuthApiCollection.Name)]
public class PasswordChangeTests(AuthApiFixture fixture)
{
    private const string Url = "/api/v1/auth/password";

    private static IReadOnlyDictionary<string, string?> Limits(int credentials, int general = 1000, int windowSeconds = 600) =>
        new Dictionary<string, string?>
        {
            ["Security:RateLimit:CredentialsPermitLimit"] = credentials.ToString(),
            ["Security:RateLimit:GeneralPermitLimit"] = general.ToString(),
            ["Security:RateLimit:WindowSeconds"] = windowSeconds.ToString(),
        };

    /// <summary>Yeni kullanıcı kaydeder ve oturumunu (cookie + taze CSRF) hazır verir.</summary>
    private async Task<(TestSession Session, string Email, string Password)> NewUserAsync(TestSession? session = null)
    {
        session ??= fixture.Dev();
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        var register = await session.RegisterAsync(email, password);
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        return (session, email, password);
    }

    private static async Task<HttpResponseMessage> ChangeAsync(TestSession session, string current, string next, string? origin = null)
    {
        var token = await session.FetchCsrfAsync();
        return await session.PostJsonAsync(Url, new { currentPassword = current, newPassword = next }, token, origin);
    }

    private static async Task AssertValidationAsync(HttpResponseMessage response, string field, string? message = null)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = await response.JsonAsync();
        Assert.Equal("validation_failed", json.RootElement.GetProperty("code").GetString());
        var errors = json.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty(field, out var list), $"errors.{field} yok: {errors.GetRawText()}");
        Assert.NotEmpty(list.EnumerateArray());
        if (message is not null)
        {
            Assert.Equal(message, list[0].GetString());
        }
    }

    private async Task<long> FailedCountAsync(string email) =>
        await fixture.Database.ScalarAsync<long>($"SELECT \"AccessFailedCount\" FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", TestContext.Current.CancellationToken);

    private async Task AssertLoginAsync(string email, string password, HttpStatusCode expected) =>
        Assert.Equal(expected, (await fixture.Dev().LoginAsync(email, password)).StatusCode);

    // ===================== GET /auth/me createdAt =====================

    [Fact]
    public async Task Me_returns_createdAt_as_a_UTC_timestamp_ending_in_Z_matching_the_database_row()
    {
        var (session, email, _) = await NewUserAsync();

        var response = await session.GetAsync("/api/v1/auth/me");
        using var json = await response.JsonAsync();
        var createdAt = json.RootElement.GetProperty("createdAt").GetString()!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$"), createdAt); // sözleşme örneği: 2026-11-08T10:13:20Z
        var parsed = DateTimeOffset.Parse(createdAt, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.InRange((DateTimeOffset.UtcNow - parsed).TotalMinutes, -1, 10);
        var storedEpoch = await fixture.Database.ScalarAsync<double>($"SELECT extract(epoch from \"CreatedAt\")::float8 FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", TestContext.Current.CancellationToken);
        Assert.InRange(Math.Abs(storedEpoch - parsed.ToUnixTimeSeconds()), 0, 1); // saniyeye kırpıldı
        // Mevcut alanlar yerinde (sözleşme: başka alan değişmez).
        Assert.Equal(["capabilities", "createdAt", "email", "emailConfirmed", "id", "roles"], json.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    // ===================== Happy path ve oturum davranışı (D-090) =====================

    [Fact]
    public async Task Change_password_returns_204_with_an_empty_no_store_body_and_a_refreshed_session_cookie()
    {
        var (session, _, password) = await NewUserAsync();
        var oldCookie = session.Cookies["ssc.auth"];

        var response = await ChangeAsync(session, password, AuthApiFixture.NewPassword());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, await response.TextAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var cookies = TestSession.ParseSetCookies(response);
        var auth = Assert.Single(cookies, c => c.Name == "ssc.auth");
        Assert.False(auth.IsDeletion);
        Assert.True(auth.Has("httponly"));
        Assert.NotEqual(oldCookie, auth.Value); // yeni stamp ile yeniden imzalandı
    }

    [Fact]
    public async Task Other_session_of_the_same_user_is_closed_while_the_changing_session_stays_open_and_can_keep_writing()
    {
        var (sessionA, email, password) = await NewUserAsync();
        var sessionB = fixture.Dev();
        Assert.Equal(HttpStatusCode.OK, (await sessionB.LoginAsync(email, password)).StatusCode); // ayrı cihaz/cookie
        var bystander = (await NewUserAsync()).Session;                                           // başka kullanıcı
        Assert.Equal(HttpStatusCode.OK, (await sessionB.GetAsync("/api/v1/auth/me")).StatusCode);
        var replayOfOldCookie = sessionA.CloneWithCookies();                                      // eski cookie'nin "çalınmış" kopyası
        var newPassword = AuthApiFixture.NewPassword();

        var change = await ChangeAsync(sessionA, password, newPassword);

        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await sessionB.GetAsync("/api/v1/auth/me")).StatusCode);          // diğer oturum kapandı
        Assert.Equal(HttpStatusCode.Unauthorized, (await replayOfOldCookie.GetAsync("/api/v1/auth/me")).StatusCode); // eski cookie tekrar oynatılamaz
        Assert.Equal(HttpStatusCode.OK, (await sessionA.GetAsync("/api/v1/auth/me")).StatusCode);                    // değiştiren oturum açık
        Assert.Equal(HttpStatusCode.OK, (await bystander.GetAsync("/api/v1/auth/me")).StatusCode);                   // başkasının oturumu etkilenmedi

        // Sonraki CSRF'li yazma çalışıyor: önceki (yenilenmemiş) token ve yeni cookie ile ikinci değişiklik.
        var again = await sessionA.PostJsonAsync(Url, new { currentPassword = newPassword, newPassword = AuthApiFixture.NewPassword() }, useStoredCsrf: true);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sessionA.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task New_password_logs_in_and_the_old_one_no_longer_does()
    {
        var (session, email, password) = await NewUserAsync();
        var newPassword = AuthApiFixture.NewPassword();

        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(session, password, newPassword)).StatusCode);

        await AssertLoginAsync(email, newPassword, HttpStatusCode.OK);
        await AssertLoginAsync(email, password, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Production_environment_changes_the_password_and_the_refreshed_cookie_keeps_the_login_cookie_attributes()
    {
        var session = fixture.Prod();
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        var register = await session.RegisterAsync(email, password);
        var loginCookie = Assert.Single(TestSession.ParseSetCookies(register), c => c.Name.EndsWith("ssc.auth", StringComparison.Ordinal)); // Production: __Host- önekli

        var response = await ChangeAsync(session, password, AuthApiFixture.NewPassword(), AuthApiFixture.ProductionOrigin);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var refreshed = Assert.Single(TestSession.ParseSetCookies(response), c => c.Name == loginCookie.Name);
        Assert.Equal(loginCookie.Has("secure"), refreshed.Has("secure"));
        Assert.Equal(loginCookie.Has("httponly"), refreshed.Has("httponly"));
        Assert.Equal(loginCookie.Get("samesite"), refreshed.Get("samesite"));
        Assert.Equal(loginCookie.Get("path"), refreshed.Get("path"));
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    // ===================== Mevcut parola yanlış + kilit sayacı =====================

    [Fact]
    public async Task Wrong_current_password_is_400_currentPassword_with_a_generic_message_and_changes_nothing()
    {
        var (session, email, password) = await NewUserAsync();
        var wrong = "Yanlis-" + Guid.NewGuid().ToString("N");

        var response = await ChangeAsync(session, wrong, AuthApiFixture.NewPassword());
        var body = await response.TextAsync();

        using var json = JsonDocument.Parse(body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(["Mevcut parola hatalı."], json.RootElement.GetProperty("errors").GetProperty("currentPassword").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.DoesNotContain(wrong, body);
        Assert.DoesNotContain(password, body);
        await AssertLoginAsync(email, password, HttpStatusCode.OK);                    // parola değişmedi
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/auth/me")).StatusCode); // oturum etkilenmedi
    }

    [Fact]
    public async Task Wrong_current_password_counts_towards_the_same_lockout_as_login_so_a_stolen_session_cannot_brute_force_it()
    {
        var (session, email, password) = await NewUserAsync();

        for (var i = 0; i < 5; i++)
        {
            await AssertValidationAsync(await ChangeAsync(session, "Yanlis-" + Guid.NewGuid().ToString("N"), AuthApiFixture.NewPassword()), "currentPassword");
        }

        // Beşinci yanlıştan sonra hesap kilitli: DOĞRU mevcut parola da artık kabul edilmez (login ile aynı davranış)...
        await AssertValidationAsync(await ChangeAsync(session, password, AuthApiFixture.NewPassword()), "currentPassword", "Mevcut parola hatalı.");
        await AssertLoginAsync(email, password, HttpStatusCode.Unauthorized);
        // ...ama sahibinin zaten açık olan oturumu kapanmaz.
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_successful_change_after_earlier_wrong_attempts_works_and_resets_the_failure_counter_regression_for_stale_concurrency_stamp()
    {
        var (session, email, password) = await NewUserAsync();
        await ChangeAsync(session, "Yanlis-1-abc", AuthApiFixture.NewPassword());
        await ChangeAsync(session, "Yanlis-2-abc", AuthApiFixture.NewPassword());
        Assert.Equal(2, await FailedCountAsync(email));
        var newPassword = AuthApiFixture.NewPassword();

        // Doğrulayıcı sayacı sıfırlarken ConcurrencyStamp'i yazar; bellekteki kullanıcı bayatlar. Yeniden yükleme olmasaydı bu 409 olurdu.
        var response = await ChangeAsync(session, password, newPassword);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await FailedCountAsync(email));
        await AssertLoginAsync(email, newPassword, HttpStatusCode.OK);
    }

    // ===================== Yeni parola politikası =====================

    [Theory]
    [InlineData("kisa1")]                                   // 10 karakterden kısa
    [InlineData("12345678901234")]                           // harf yok
    [InlineData("sadeceharflerdenolusan")]                   // rakam yok
    [InlineData("")]                                         // boş
    public async Task Weak_or_empty_new_password_is_400_newPassword_and_never_touches_the_lockout_counter(string weak)
    {
        var (session, email, password) = await NewUserAsync();

        var response = await ChangeAsync(session, password, weak);

        await AssertValidationAsync(response, "newPassword");
        Assert.Equal(0, await FailedCountAsync(email));
        await AssertLoginAsync(email, password, HttpStatusCode.OK);
    }

    [Fact]
    public async Task Policy_messages_are_the_same_as_registration_and_never_echo_the_password()
    {
        var (session, _, password) = await NewUserAsync();
        var weak = "kisa";

        var change = await ChangeAsync(session, password, weak);
        var registration = await fixture.Dev().RegisterAsync(AuthApiFixture.NewEmail(), weak);

        using var changeJson = await change.JsonAsync();
        using var registrationJson = await registration.JsonAsync();
        Assert.Equal(
            registrationJson.RootElement.GetProperty("errors").GetProperty("password").GetRawText(),
            changeJson.RootElement.GetProperty("errors").GetProperty("newPassword").GetRawText());
    }

    [Fact]
    public async Task New_password_over_the_maximum_length_is_400_newPassword_without_hashing()
    {
        var (session, email, password) = await NewUserAsync();
        var tooLong = "A1" + new string('x', 127); // 129 karakter (kayıtla aynı üst sınır: 128)

        var response = await ChangeAsync(session, password, tooLong);

        await AssertValidationAsync(response, "newPassword");
        Assert.DoesNotContain(tooLong, await response.TextAsync());
        Assert.Equal(0, await FailedCountAsync(email));
    }

    [Fact]
    public async Task New_password_at_exactly_the_maximum_length_is_accepted()
    {
        var (session, email, password) = await NewUserAsync();
        var longest = "A1" + new string('x', 126); // 128

        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(session, password, longest)).StatusCode);
        await AssertLoginAsync(email, longest, HttpStatusCode.OK);
    }

    [Fact]
    public async Task New_password_equal_to_the_current_one_is_400_newPassword_and_the_hash_is_untouched()
    {
        var (session, email, password) = await NewUserAsync();
        var hashBefore = await fixture.Database.ScalarAsync<string>($"SELECT \"PasswordHash\" FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", TestContext.Current.CancellationToken);

        var response = await ChangeAsync(session, password, password);

        await AssertValidationAsync(response, "newPassword", "Yeni parola mevcut parolayla aynı olamaz.");
        var hashAfter = await fixture.Database.ScalarAsync<string>($"SELECT \"PasswordHash\" FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", TestContext.Current.CancellationToken);
        Assert.Equal(hashBefore, hashAfter);
        Assert.Equal(0, await FailedCountAsync(email));
    }

    // ---- "Parola tahmin kanalı yok": yanıt, mevcut parolanın doğruluğunu yeni parola hatası üzerinden sızdırmaz ----

    [Fact]
    public async Task New_equals_current_gives_the_identical_response_whether_the_current_password_is_right_or_wrong()
    {
        var (session, email, password) = await NewUserAsync();
        var wrong = "Yanlis-" + Guid.NewGuid().ToString("N");

        var withWrongCurrent = await ChangeAsync(session, wrong, wrong);
        var withRightCurrent = await ChangeAsync(session, password, password);

        Assert.Equal(withRightCurrent.StatusCode, withWrongCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withWrongCurrent.StatusCode);
        using var a = await withWrongCurrent.JsonAsync();
        using var b = await withRightCurrent.JsonAsync();
        Assert.Equal(a.RootElement.GetProperty("errors").GetRawText(), b.RootElement.GetProperty("errors").GetRawText());
        Assert.Equal(0, await FailedCountAsync(email)); // iki denemede de sayaç artmadı: hiçbiri parola tahmini yapmadı
    }

    [Fact]
    public async Task Wrong_current_with_a_policy_violating_new_password_reports_only_newPassword_and_leaves_the_counter_at_zero()
    {
        var (session, email, _) = await NewUserAsync();

        var response = await ChangeAsync(session, "Yanlis-" + Guid.NewGuid().ToString("N"), "kisa");

        await AssertValidationAsync(response, "newPassword");
        using var json = await response.JsonAsync();
        Assert.False(json.RootElement.GetProperty("errors").TryGetProperty("currentPassword", out _), "currentPassword hatası mevcut parolanın yanlışlığını sızdırırdı.");
        Assert.Equal(0, await FailedCountAsync(email));
    }

    [Fact]
    public async Task Wrong_current_with_a_valid_new_password_reports_only_currentPassword_and_counts_exactly_one_failure()
    {
        var (session, email, _) = await NewUserAsync();

        var response = await ChangeAsync(session, "Yanlis-" + Guid.NewGuid().ToString("N"), AuthApiFixture.NewPassword());

        await AssertValidationAsync(response, "currentPassword", "Mevcut parola hatalı.");
        using var json = await response.JsonAsync();
        Assert.False(json.RootElement.GetProperty("errors").TryGetProperty("newPassword", out _));
        Assert.Equal(1, await FailedCountAsync(email));
    }

    [Fact]
    public async Task Over_long_current_password_is_400_currentPassword_without_touching_the_counter()
    {
        var (session, email, _) = await NewUserAsync();

        var response = await ChangeAsync(session, new string('x', 129), AuthApiFixture.NewPassword());

        await AssertValidationAsync(response, "currentPassword", "Mevcut parola hatalı.");
        Assert.Equal(0, await FailedCountAsync(email));
    }

    // ===================== Girdi biçimi =====================

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"currentPassword":"","newPassword":""}""")]
    [InlineData("""{"currentPassword":"abc"}""")]
    [InlineData("""{"newPassword":"abc12345678"}""")]
    [InlineData("""{"currentPassword":null,"newPassword":null}""")]
    [InlineData("""{"currentPassword":1,"newPassword":2}""")]     // yanlış tip
    [InlineData("""["a","b"]""")]                                  // nesne yerine dizi
    [InlineData("{")]                                              // bozuk JSON
    [InlineData("null")]
    [InlineData("")]
    public async Task Missing_empty_mistyped_or_broken_body_is_400_validation_failed_and_changes_nothing(string rawBody)
    {
        var (session, email, password) = await NewUserAsync();
        var token = await session.FetchCsrfAsync();

        var response = await session.SendAsync(HttpMethod.Post, Url, rawBody, csrf: token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = await response.JsonAsync();
        Assert.Equal("validation_failed", json.RootElement.GetProperty("code").GetString());
        await AssertLoginAsync(email, password, HttpStatusCode.OK);
        Assert.Equal(0, await FailedCountAsync(email));
    }

    [Fact]
    public async Task Non_json_content_type_is_415_and_oversized_body_is_400()
    {
        var (session, _, _) = await NewUserAsync();
        var token = await session.FetchCsrfAsync();

        var text = await session.SendAsync(HttpMethod.Post, Url, "currentPassword=a&newPassword=b", contentType: "text/plain", csrf: token);
        var huge = await session.SendAsync(HttpMethod.Post, Url, JsonSerializer.Serialize(new { currentPassword = "a", newPassword = new string('x', 10_000) }), csrf: token);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode); // genel hata modeli: 415 unsupported_media_type
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
    }

    // ===================== Kimlik ve istek korumaları =====================

    [Fact]
    public async Task Anonymous_is_401_unauthenticated()
    {
        var anonymous = fixture.Dev();
        var token = await anonymous.FetchCsrfAsync();

        var response = await anonymous.PostJsonAsync(Url, new { currentPassword = "a", newPassword = "b" }, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var json = await response.JsonAsync();
        Assert.Equal("unauthenticated", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_logged_out_cookie_cannot_change_the_password()
    {
        var (session, email, password) = await NewUserAsync();
        var stolen = session.CloneWithCookies();
        Assert.Equal(HttpStatusCode.NoContent, (await session.LogoutAsync()).StatusCode);

        var response = await ChangeAsync(stolen, password, AuthApiFixture.NewPassword());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertLoginAsync(email, password, HttpStatusCode.OK);
    }

    [Fact]
    public async Task Missing_or_wrong_csrf_token_is_rejected_and_the_password_stays()
    {
        var (session, email, password) = await NewUserAsync();
        var body = new { currentPassword = password, newPassword = AuthApiFixture.NewPassword() };

        var missing = await session.PostJsonAsync(Url, body, csrf: null);
        var wrong = await session.PostJsonAsync(Url, body, csrf: "sahte-token");

        foreach (var response in new[] { missing, wrong })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var json = await response.JsonAsync();
            Assert.Equal("csrf_invalid", json.RootElement.GetProperty("code").GetString());
        }

        await AssertLoginAsync(email, password, HttpStatusCode.OK);
        Assert.Equal(0, await FailedCountAsync(email));
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    public async Task Foreign_origin_is_403_even_with_a_valid_csrf_token(string origin)
    {
        var (session, email, password) = await NewUserAsync();

        var response = await ChangeAsync(session, password, AuthApiFixture.NewPassword(), origin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var json = await response.JsonAsync();
        Assert.Equal("origin_not_allowed", json.RootElement.GetProperty("code").GetString());
        await AssertLoginAsync(email, password, HttpStatusCode.OK);
    }

    [Fact]
    public async Task Csrf_token_of_another_user_is_rejected()
    {
        var (victim, victimEmail, victimPassword) = await NewUserAsync();
        var (attacker, _, _) = await NewUserAsync();
        var attackerToken = await attacker.FetchCsrfAsync();

        var response = await victim.PostJsonAsync(Url, new { currentPassword = victimPassword, newPassword = AuthApiFixture.NewPassword() }, attackerToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertLoginAsync(victimEmail, victimPassword, HttpStatusCode.OK);
    }

    // ===================== Hız sınırı =====================

    [Fact]
    public async Task Over_the_credentials_limit_is_429_with_Retry_After_and_throttled_requests_do_not_count_as_password_guesses()
    {
        await using var factory = ApiFactory.Create("Development", fixture.Database.ConnectionString, extraConfiguration: Limits(credentials: 3));
        var (email, password, _) = await AuthApiFixture.CreateUserWithRoleAsync(factory, "Customer");
        var session = fixture.NewSession(factory);
        Assert.Equal(HttpStatusCode.OK, (await session.LoginAsync(email, password)).StatusCode); // credentials hakkı: 1/3

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++)
        {
            last = await ChangeAsync(session, "Yanlis-" + Guid.NewGuid().ToString("N"), AuthApiFixture.NewPassword()); // 2/3, 3/3, sonra 429, 429
            statuses.Add(last.StatusCode);
        }

        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal("application/problem+json", last!.Content.Headers.ContentType?.MediaType);
        using var json = await last.JsonAsync();
        Assert.Equal("rate_limited", json.RootElement.GetProperty("code").GetString());
        Assert.True(last.Headers.RetryAfter?.Delta is { } delta && delta.TotalSeconds is > 0 and <= 600, "Retry-After saniye cinsinden olmalı.");
        Assert.Equal(2, await FailedCountAsync(email)); // yalnız işlenen iki istek sayıldı; 429'lar sayaca ulaşmadı
    }

    // ===================== Eşzamanlılık =====================

    [Fact]
    public async Task Two_simultaneous_changes_with_the_same_current_password_leave_exactly_one_winner()
    {
        var (sessionA, email, password) = await NewUserAsync();
        var sessionB = fixture.Dev();
        Assert.Equal(HttpStatusCode.OK, (await sessionB.LoginAsync(email, password)).StatusCode);
        var tokenA = await sessionA.FetchCsrfAsync();
        var tokenB = await sessionB.FetchCsrfAsync();
        var passwordA = AuthApiFixture.NewPassword();
        var passwordB = AuthApiFixture.NewPassword();

        var results = await Task.WhenAll(
            sessionA.PostJsonAsync(Url, new { currentPassword = password, newPassword = passwordA }, tokenA),
            sessionB.PostJsonAsync(Url, new { currentPassword = password, newPassword = passwordB }, tokenB));

        var winners = results.Count(r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Equal(1, winners);
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.NoContent), r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Conflict, HttpStatusCode.Unauthorized }));
        var winnerPassword = results[0].StatusCode == HttpStatusCode.NoContent ? passwordA : passwordB;
        var loserPassword = results[0].StatusCode == HttpStatusCode.NoContent ? passwordB : passwordA;
        await AssertLoginAsync(email, winnerPassword, HttpStatusCode.OK);
        await AssertLoginAsync(email, loserPassword, HttpStatusCode.Unauthorized);
        await AssertLoginAsync(email, password, HttpStatusCode.Unauthorized);
    }

    // ===================== Sızıntı: yanıt ve günlükler =====================

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
                var fields = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(' ', pairs.Select(p => $"{p.Key}={p.Value}"))
                    : string.Empty;
                lines.Enqueue($"{category} {logLevel} {formatter(state, exception)} {fields} {exception}");
            }
        }
    }

    [Fact]
    public async Task Passwords_cookies_and_emails_never_appear_in_responses_or_logs_but_the_event_is_logged_with_the_user_id()
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
        var weak = "zayif" + Guid.NewGuid().ToString("N")[..3];
        var newPassword = AuthApiFixture.NewPassword();
        await session.RegisterAsync(email, password);
        var oldCookie = session.Cookies["ssc.auth"];

        var responses = new[]
        {
            await ChangeAsync(session, wrong, newPassword),        // 400 currentPassword
            await ChangeAsync(session, password, weak),             // 400 newPassword
            await ChangeAsync(session, password, password),         // 400 aynı parola
            await ChangeAsync(session, password, newPassword),      // 204
        };
        var texts = string.Join('\n', await Task.WhenAll(responses.Select(r => r.TextAsync())));
        var newCookie = session.Cookies["ssc.auth"];
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.NewSession(factory).LoginAsync(email, wrong)).StatusCode); // girişte yanlış parola

        Assert.Equal(HttpStatusCode.NoContent, responses[^1].StatusCode);
        var logText = string.Join('\n', logs.Lines);
        Assert.NotEmpty(logs.Lines);
        Assert.Contains("Parola değiştirildi", logText);
        // Gözlemlenebilirlik: parola değiştirme ucundaki yanlış mevcut parola girişten AYRI iletiyle loglanır (her biri tam bir kez).
        Assert.Equal(1, logs.Lines.Count(l => l.Contains("Başarısız parola değiştirme denemesi")));
        Assert.Equal(1, logs.Lines.Count(l => l.Contains("Başarısız giriş denemesi")));
        Assert.DoesNotContain(logs.Lines, l => l.Contains("Başarısız parola değiştirme denemesi") && l.Contains("Başarısız giriş denemesi"));
        foreach (var secret in new[] { password, wrong, weak, newPassword, oldCookie, newCookie, session.Csrf! })
        {
            Assert.DoesNotContain(secret, logText);
            Assert.DoesNotContain(secret, texts);
        }

        Assert.DoesNotContain(localPart, logText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test", logText, StringComparison.OrdinalIgnoreCase);
    }
}

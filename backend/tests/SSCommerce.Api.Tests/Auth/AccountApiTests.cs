using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SSCommerce.Api.Tests.Catalog;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Auth;

/// <summary>
/// Integration + API testleri: kayıt, giriş, kilitleme, çıkış, /me, rol politikaları. Gerçek PostgreSQL (izole DB), tam HTTP hattı
/// (rate limit → authentication → authorization → CSRF/Origin filtresi → Identity → EF). Her test kendi rastgele kullanıcısını kurar.
/// </summary>
[Collection(AuthApiCollection.Name)]
public class AccountApiTests(AuthApiFixture fixture)
{
    private static readonly string[] SensitiveKeys =
        ["passwordhash", "password", "securitystamp", "concurrencystamp", "token", "accesstoken", "refreshtoken", "normalizedemail", "lockoutend", "accessfailedcount"];

    private static async Task AssertProblem(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = await response.JsonAsync();
        Assert.Equal(status, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        Assert.Null(response.Headers.Location); // yönlendirme yok
    }

    private static void AssertNoSensitiveKeys(JsonElement element)
    {
        var found = CatalogJson.AllPropertyNames(element).Where(n => SensitiveKeys.Contains(n.ToLowerInvariant())).ToArray();
        Assert.Empty(found);
    }

    // ===================== Kayıt =====================

    [Fact]
    public async Task Register_creates_a_Customer_signs_in_and_returns_the_account()
    {
        var session = fixture.Dev();
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();

        var response = await session.RegisterAsync(email, password);
        var raw = await response.TextAsync();
        using var json = JsonDocument.Parse(raw);
        var root = json.RootElement;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(["capabilities", "email", "emailConfirmed", "id", "roles"], root.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(email, root.GetProperty("email").GetString());
        Assert.False(root.GetProperty("emailConfirmed").GetBoolean()); // D-036: e-posta doğrulaması yok, "doğrulandı" denmez
        Assert.Equal(["Customer"], root.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray());
        Assert.False(root.GetProperty("capabilities").GetProperty("admin").GetBoolean());
        Assert.False(root.GetProperty("capabilities").GetProperty("warehouse").GetBoolean());
        AssertNoSensitiveKeys(root);
        Assert.DoesNotContain(password, raw);

        // Oturum açıldı: aynı "tarayıcı" /me ile kendini görür.
        var me = await session.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // Veritabanı: bir kullanıcı, yalnız Customer rolü, EmailConfirmed false.
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(1, await fixture.Database.ScalarAsync<long>($"SELECT count(*) FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", ct));
        Assert.False(await fixture.Database.ScalarAsync<bool>($"SELECT \"EmailConfirmed\" FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", ct));
        Assert.Equal(
            "Customer",
            await fixture.Database.ScalarAsync<string>(
                $"SELECT string_agg(r.\"Name\", ',') FROM \"AspNetUserRoles\" ur JOIN \"AspNetRoles\" r ON r.\"Id\" = ur.\"RoleId\" JOIN \"AspNetUsers\" u ON u.\"Id\" = ur.\"UserId\" WHERE u.\"Email\" = '{email}'", ct));
        Assert.True(await fixture.Database.ScalarAsync<bool>($"SELECT \"LockoutEnabled\" FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", ct)); // yeni kullanıcıda da kilitleme açık
    }

    [Fact]
    public async Task Register_with_the_same_email_in_a_different_case_is_rejected_with_409()
    {
        var first = fixture.Dev();
        var second = fixture.Dev();
        var email = $"MixedCase{Guid.NewGuid():N}@Example.Test";

        var created = await first.RegisterAsync(email, AuthApiFixture.NewPassword());
        var duplicate = await second.RegisterAsync(email.ToLowerInvariant(), AuthApiFixture.NewPassword());
        var duplicateUpper = await second.RegisterAsync(email.ToUpperInvariant(), AuthApiFixture.NewPassword());

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await AssertProblem(duplicate, 409, "email_unavailable");
        await AssertProblem(duplicateUpper, 409, "email_unavailable");
        Assert.DoesNotContain(second.Cookies.Keys, k => k.Contains("auth")); // reddedilen kayıt oturum açmaz
        Assert.Equal(
            1,
            await fixture.Database.ScalarAsync<long>(
                $"SELECT count(*) FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = '{email.ToUpperInvariant()}'",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Password_is_stored_only_as_an_Identity_V3_hash()
    {
        var session = fixture.Dev();
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        await session.RegisterAsync(email, password);

        var hash = await fixture.Database.ScalarAsync<string>(
            $"SELECT \"PasswordHash\" FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", TestContext.Current.CancellationToken);

        Assert.NotNull(hash);
        Assert.NotEqual(password, hash);
        Assert.DoesNotContain(password, hash);
        Assert.StartsWith("AQAAAA", hash, StringComparison.Ordinal); // ASP.NET Identity V3 (PBKDF2, base64 üstbilgi)
        Assert.True(hash.Length > 60);
    }

    [Theory]
    [InlineData("Ab1")]                       // çok kısa
    [InlineData("abcdefghijkl")]              // rakam yok
    [InlineData("1234567890123")]             // harf yok
    [InlineData("kısa1a")]                    // 6 karakter
    [InlineData("")]                          // boş
    public async Task Weak_password_returns_400_validation_failed_without_echoing_it(string password)
    {
        var session = fixture.Dev();
        var email = AuthApiFixture.NewEmail();

        var response = await session.RegisterAsync(email, password);
        var raw = await response.TextAsync();

        await AssertProblem(response, 400, "validation_failed");
        using var json = JsonDocument.Parse(raw);
        var messages = json.RootElement.GetProperty("errors").GetProperty("password").EnumerateArray().Select(m => m.GetString()).ToArray();
        Assert.NotEmpty(messages);
        if (password.Length > 0)
        {
            Assert.DoesNotContain(password, raw); // parola hata gövdesinde yankılanmaz
        }

        Assert.Equal(0, await fixture.Database.ScalarAsync<long>($"SELECT count(*) FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Pw1-1234567890", true)]       // 10+ karakter, harf + rakam
    [InlineData("abcdefghi1", true)]           // tam 10 karakter
    [InlineData("abcdefgh1", false)]           // 9 karakter
    [InlineData("şifreşifre1", true)]          // Türkçe harf de harftir
    public async Task Password_policy_boundaries(string password, bool accepted)
    {
        var session = fixture.Dev();

        var response = await session.RegisterAsync(AuthApiFixture.NewEmail(), password);

        Assert.Equal(accepted ? HttpStatusCode.Created : HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Password_longer_than_128_characters_is_rejected_with_400()
    {
        var response = await fixture.Dev().RegisterAsync(AuthApiFixture.NewEmail(), "a1" + new string('x', 200));

        await AssertProblem(response, 400, "validation_failed");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("a b@example.test")]
    [InlineData("@example.test")]
    [InlineData("user@")]
    public async Task Invalid_email_returns_400_with_email_error(string email)
    {
        var response = await fixture.Dev().RegisterAsync(email, AuthApiFixture.NewPassword());
        var raw = await response.TextAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(raw);
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("email", out _));
    }

    [Fact]
    public async Task Over_long_email_returns_400()
    {
        var response = await fixture.Dev().RegisterAsync(new string('a', 250) + "@example.test", AuthApiFixture.NewPassword());

        await AssertProblem(response, 400, "validation_failed");
    }

    [Theory]
    [InlineData("not json at all", "application/json")]
    [InlineData("{\"email\": 123, \"password\": true}", "application/json")]  // yanlış tip
    [InlineData("[]", "application/json")]                                       // dizi, nesne değil
    [InlineData("null", "application/json")]
    [InlineData("", "application/json")]
    public async Task Malformed_body_returns_400_validation_failed_not_500(string body, string contentType)
    {
        var session = fixture.Dev();
        var token = await session.FetchCsrfAsync();

        var response = await session.SendAsync(HttpMethod.Post, "/api/v1/auth/register", body, contentType, csrf: token);

        await AssertProblem(response, 400, "validation_failed");
    }

    [Theory]
    [InlineData("email=a&password=b", "application/x-www-form-urlencoded")]
    [InlineData("{\"email\":\"a@b.c\"}", "text/plain")]
    public async Task Non_json_content_type_returns_415_problem_json(string body, string contentType)
    {
        var session = fixture.Dev();
        var token = await session.FetchCsrfAsync();

        var response = await session.SendAsync(HttpMethod.Post, "/api/v1/auth/register", body, contentType, csrf: token);

        await AssertProblem(response, 415, "unsupported_media_type");
        Assert.DoesNotContain(session.Cookies.Keys, k => k.Contains("auth"));
    }

    [Fact]
    public async Task Oversized_body_returns_400()
    {
        var session = fixture.Dev();
        var token = await session.FetchCsrfAsync();
        var body = "{\"email\":\"a@b.c\",\"password\":\"" + new string('x', 10_000) + "\"}";

        var response = await session.SendAsync(HttpMethod.Post, "/api/v1/auth/register", body, csrf: token);

        await AssertProblem(response, 400, "validation_failed");
    }

    [Fact]
    public async Task Unknown_role_fields_in_register_body_are_ignored_and_the_user_stays_Customer()
    {
        var session = fixture.Dev();
        var email = AuthApiFixture.NewEmail();

        var response = await session.RegisterAsync(
            email,
            AuthApiFixture.NewPassword(),
            new { role = "Admin", roles = new[] { "Admin", "Warehouse" }, isAdmin = true, Role = "Admin", userName = "admin" });
        using var json = await response.JsonAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(["Customer"], json.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray());
        Assert.False(json.RootElement.GetProperty("capabilities").GetProperty("admin").GetBoolean());

        // Admin ucu 403 (oturum açık ama yetkisiz); veritabanında yalnız Customer rolü var.
        var admin = await session.GetAsync("/api/v1/admin/me");
        await AssertProblem(admin, 403, "forbidden");
        Assert.Equal(
            1,
            await fixture.Database.ScalarAsync<long>(
                $"SELECT count(*) FROM \"AspNetUserRoles\" ur JOIN \"AspNetUsers\" u ON u.\"Id\" = ur.\"UserId\" WHERE u.\"Email\" = '{email}'",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Login_body_cannot_assign_roles_either()
    {
        var session = fixture.Dev();
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        await session.RegisterAsync(email, password);
        var other = fixture.Dev();
        var token = await other.FetchCsrfAsync();

        var response = await other.PostJsonAsync("/api/v1/auth/login", new { email, password, role = "Admin", roles = new[] { "Admin" } }, token);
        using var json = await response.JsonAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Customer"], json.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray());
    }

    [Fact]
    public async Task Concurrent_registrations_of_the_same_email_produce_one_account_and_no_server_error()
    {
        var email = AuthApiFixture.NewEmail();
        var sessions = Enumerable.Range(0, 4).Select(_ => fixture.Dev()).ToArray();
        var tokens = new List<string>();
        foreach (var s in sessions)
        {
            tokens.Add(await s.FetchCsrfAsync());
        }

        var responses = await Task.WhenAll(sessions.Select((s, i) =>
            s.PostJsonAsync("/api/v1/auth/register", new { email, password = AuthApiFixture.NewPassword() }, tokens[i])));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, $"Beklenmeyen {(int)r.StatusCode}"));
        Assert.Equal(1, await fixture.Database.ScalarAsync<long>($"SELECT count(*) FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", TestContext.Current.CancellationToken));
    }

    // ===================== Giriş =====================

    [Fact]
    public async Task Login_with_correct_credentials_returns_account_and_session_cookie()
    {
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        await fixture.Dev().RegisterAsync(email, password);
        var session = fixture.Dev();

        var response = await session.LoginAsync(email, password);
        var raw = await response.TextAsync();
        using var json = JsonDocument.Parse(raw);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(email, json.RootElement.GetProperty("email").GetString());
        AssertNoSensitiveKeys(json.RootElement);
        Assert.DoesNotContain(password, raw);
        Assert.Contains(session.Cookies.Keys, k => k == "ssc.auth");

        var me = await session.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Login_is_case_insensitive_for_the_email()
    {
        var email = $"CaseUser{Guid.NewGuid():N}@Example.Test";
        var password = AuthApiFixture.NewPassword();
        await fixture.Dev().RegisterAsync(email, password);

        var response = await fixture.Dev().LoginAsync(email.ToLowerInvariant(), password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_unknown_user_and_locked_account_get_the_same_401_response()
    {
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        await fixture.Dev().RegisterAsync(email, password);
        var lockedEmail = AuthApiFixture.NewEmail();
        var lockedPassword = AuthApiFixture.NewPassword();
        await fixture.Dev().RegisterAsync(lockedEmail, lockedPassword);
        var locked = fixture.Dev();
        for (var i = 0; i < 5; i++)
        {
            await locked.LoginAsync(lockedEmail, "Yanlis-parola-1");
        }

        var wrong = await fixture.Dev().LoginAsync(email, "Yanlis-parola-1");
        var unknown = await fixture.Dev().LoginAsync(AuthApiFixture.NewEmail(), "Yanlis-parola-1");
        var lockedCorrect = await fixture.Dev().LoginAsync(lockedEmail, lockedPassword);

        foreach (var response in new[] { wrong, unknown, lockedCorrect })
        {
            await AssertProblem(response, 401, "invalid_credentials");
            Assert.DoesNotContain(TestSession.ParseSetCookies(response), c => c.Name.Contains("auth"));
        }

        // Aynı gövde (traceId hariç) ve aynı başlık kümesi: hesap varlığı/kilit durumu dışarıdan ayırt edilemez.
        var bodies = await Task.WhenAll(wrong.BodyWithoutTraceIdAsync(), unknown.BodyWithoutTraceIdAsync(), lockedCorrect.BodyWithoutTraceIdAsync());
        Assert.Single(bodies.Distinct());
        Assert.Single(new[] { wrong, unknown, lockedCorrect }.Select(HeaderFingerprint).Distinct());
    }

    private static string HeaderFingerprint(HttpResponseMessage response) =>
        string.Join(
            "|",
            response.Headers.Select(h => h.Key).Concat(response.Content.Headers.Select(h => h.Key)).Order().Where(n => !n.Equals("Date", StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_and_the_correct_password_is_then_rejected_too()
    {
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        await fixture.Dev().RegisterAsync(email, password);
        var session = fixture.Dev();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await session.LoginAsync(email, "Yanlis-parola-1")).StatusCode);
        }

        var correct = await session.LoginAsync(email, password);

        await AssertProblem(correct, 401, "invalid_credentials");
        var remainingSeconds = await fixture.Database.ScalarAsync<double>(
            $"SELECT extract(epoch FROM (\"LockoutEnd\" - now())) FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", TestContext.Current.CancellationToken);
        var remaining = TimeSpan.FromSeconds(remainingSeconds);
        Assert.InRange(remaining.TotalMinutes, 13, 16); // ~15 dakika
    }

    [Fact]
    public async Task Four_wrong_passwords_do_not_lock_and_a_correct_login_resets_the_counter()
    {
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        await fixture.Dev().RegisterAsync(email, password);
        var session = fixture.Dev();
        for (var i = 0; i < 4; i++)
        {
            await session.LoginAsync(email, "Yanlis-parola-1");
        }

        var ok = await session.LoginAsync(email, password);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(0, await fixture.Database.ScalarAsync<long>($"SELECT \"AccessFailedCount\" FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("a@b.c", null)]
    [InlineData("", "")]
    public async Task Login_with_missing_fields_returns_400(string? email, string? password)
    {
        var session = fixture.Dev();
        var token = await session.FetchCsrfAsync();

        var response = await session.PostJsonAsync("/api/v1/auth/login", new { email, password }, token);

        await AssertProblem(response, 400, "validation_failed");
    }

    [Fact]
    public async Task Login_with_an_over_long_password_is_rejected_cleanly()
    {
        var email = AuthApiFixture.NewEmail();
        await fixture.Dev().RegisterAsync(email, AuthApiFixture.NewPassword());

        var response = await fixture.Dev().LoginAsync(email, new string('x', 300));

        await AssertProblem(response, 401, "invalid_credentials");
    }

    [Fact]
    public async Task Login_and_register_never_mark_the_email_confirmed()
    {
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        await fixture.Dev().RegisterAsync(email, password);
        var session = fixture.Dev();
        await session.LoginAsync(email, password);

        using var me = await (await session.GetAsync("/api/v1/auth/me")).JsonAsync();

        Assert.False(me.RootElement.GetProperty("emailConfirmed").GetBoolean());
        Assert.False(await fixture.Database.ScalarAsync<bool>($"SELECT \"EmailConfirmed\" FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", TestContext.Current.CancellationToken));
    }

    // ===================== Çıkış ve sunucu tarafı iptal =====================

    [Fact]
    public async Task Logout_returns_204_deletes_the_cookie_and_revokes_the_old_cookie_on_the_server()
    {
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        var session = fixture.Dev();
        await session.RegisterAsync(email, password);
        var stolen = session.CloneWithCookies(); // çıkıştan ÖNCE kopyalanmış cookie (çalınmış gibi)
        var stolenCookie = session.Cookies["ssc.auth"];

        var logout = await session.LogoutAsync();

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var setCookies = TestSession.ParseSetCookies(logout);
        Assert.Contains(setCookies, c => c.Name == "ssc.auth" && c.IsDeletion); // tarayıcıdaki cookie silinir
        Assert.DoesNotContain("ssc.auth", session.Cookies.Keys);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/v1/auth/me")).StatusCode);

        // REPLAY: eski cookie elle tekrar gönderilir. Cookie imzası ve süresi geçerli, ama security stamp değişti => sunucu reddeder.
        var replay = fixture.Dev();
        replay.Cookies["ssc.auth"] = stolenCookie;
        var replayed = await replay.GetAsync("/api/v1/auth/me");
        await AssertProblem(replayed, 401, "unauthenticated");
        Assert.Equal(HttpStatusCode.Unauthorized, (await stolen.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/api/v1/admin/me")).StatusCode);
    }

    [Fact]
    public async Task Logout_invalidates_the_users_other_sessions_too_and_a_new_login_works()
    {
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        await fixture.Dev().RegisterAsync(email, password);
        var phone = fixture.Dev();
        var laptop = fixture.Dev();
        await phone.LoginAsync(email, password);
        await laptop.LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/v1/auth/me")).StatusCode);

        await phone.LogoutAsync();

        // Belgelenen davranış (D-033): stamp kullanıcı başına olduğundan çıkış TÜM cihazlardan çıkarır.
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/v1/auth/me")).StatusCode);
        var again = fixture.Dev();
        Assert.Equal(HttpStatusCode.OK, (await again.LoginAsync(email, password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Logout_without_a_session_returns_401_even_with_a_valid_csrf_token()
    {
        var session = fixture.Dev();
        var token = await session.FetchCsrfAsync();

        var response = await session.PostJsonAsync("/api/v1/auth/logout", null, token);

        await AssertProblem(response, 401, "unauthenticated");
    }

    // ===================== /me =====================

    [Fact]
    public async Task Me_is_401_problem_json_for_anonymous_with_no_store_and_no_redirect()
    {
        var response = await fixture.Dev().GetAsync("/api/v1/auth/me");

        await AssertProblem(response, 401, "unauthenticated");
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Me_returns_the_account_with_no_store_when_signed_in()
    {
        var session = fixture.Dev();
        var email = AuthApiFixture.NewEmail();
        await session.RegisterAsync(email, AuthApiFixture.NewPassword());

        var response = await session.GetAsync("/api/v1/auth/me");
        using var json = await response.JsonAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(email, json.RootElement.GetProperty("email").GetString());
        AssertNoSensitiveKeys(json.RootElement);
    }

    [Theory]
    [InlineData(new[] { "Customer" }, false, false)]
    [InlineData(new[] { "Warehouse" }, false, true)]
    [InlineData(new[] { "Admin" }, true, true)]
    [InlineData(new[] { "Customer", "Warehouse" }, false, true)]
    public async Task Me_capabilities_follow_the_roles(string[] roles, bool admin, bool warehouse)
    {
        var (email, password, _) = await AuthApiFixture.CreateUserWithRoleAsync(fixture.DevelopmentFactory, roles);
        var session = fixture.Dev();
        await session.LoginAsync(email, password);

        using var json = await (await session.GetAsync("/api/v1/auth/me")).JsonAsync();

        Assert.Equal(roles.Order(StringComparer.Ordinal).ToArray(), json.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray());
        Assert.Equal(admin, json.RootElement.GetProperty("capabilities").GetProperty("admin").GetBoolean());
        Assert.Equal(warehouse, json.RootElement.GetProperty("capabilities").GetProperty("warehouse").GetBoolean());
    }

    // ===================== Rol politikaları =====================

    [Fact]
    public async Task Admin_me_anonymous_is_401_problem_json_without_redirect_even_for_browser_like_requests()
    {
        var browserHeaders = new Dictionary<string, string>
        {
            ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            ["Sec-Fetch-Mode"] = "navigate",
        };

        var response = await fixture.Dev().GetAsync("/api/v1/admin/me", browserHeaders);

        await AssertProblem(response, 401, "unauthenticated");
        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(response.Headers.Contains("Location"));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Admin_me_for_a_customer_is_403_problem_json()
    {
        var session = fixture.Dev();
        await session.RegisterAsync(AuthApiFixture.NewEmail(), AuthApiFixture.NewPassword());

        var response = await session.GetAsync("/api/v1/admin/me");

        await AssertProblem(response, 403, "forbidden");
        Assert.False(response.Headers.Contains("Location"));
    }

    [Fact]
    public async Task Admin_me_for_a_warehouse_user_is_403()
    {
        var (email, password, _) = await AuthApiFixture.CreateUserWithRoleAsync(fixture.DevelopmentFactory, AppRoles.Warehouse);
        var session = fixture.Dev();
        await session.LoginAsync(email, password);

        await AssertProblem(await session.GetAsync("/api/v1/admin/me"), 403, "forbidden");
    }

    [Fact]
    public async Task Admin_me_for_an_admin_is_200_with_id_email_and_roles_only()
    {
        var (email, password, id) = await AuthApiFixture.CreateUserWithRoleAsync(fixture.DevelopmentFactory, AppRoles.Admin);
        var session = fixture.Dev();
        await session.LoginAsync(email, password);

        var response = await session.GetAsync("/api/v1/admin/me");
        using var json = await response.JsonAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["email", "id", "roles"], json.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(id.ToString(), json.RootElement.GetProperty("id").GetString());
        Assert.Equal(email, json.RootElement.GetProperty("email").GetString());
        Assert.Equal(["Admin"], json.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray());
        AssertNoSensitiveKeys(json.RootElement);
    }

    [Fact]
    public async Task A_role_granted_after_login_takes_effect_on_the_next_request_and_revoking_it_takes_it_back()
    {
        // Güvenlik damgası aralığı 0 olduğundan principal her istekte DB'den yeniden kurulur: eski cookie "eski rolü" taşımaz.
        var session = fixture.Dev();
        var email = AuthApiFixture.NewEmail();
        await session.RegisterAsync(email, AuthApiFixture.NewPassword());
        Assert.Equal(HttpStatusCode.Forbidden, (await session.GetAsync("/api/v1/admin/me")).StatusCode);

        await SetRoleAsync(email, AppRoles.Admin, grant: true);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/admin/me")).StatusCode);

        await SetRoleAsync(email, AppRoles.Admin, grant: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await session.GetAsync("/api/v1/admin/me")).StatusCode);
    }

    private async Task SetRoleAsync(string email, string role, bool grant)
    {
        using var scope = fixture.DevelopmentFactory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AppUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        var result = grant ? await users.AddToRoleAsync(user, role) : await users.RemoveFromRoleAsync(user, role);
        Assert.True(result.Succeeded);
    }

    // ===================== Oturum izolasyonu =====================

    [Fact]
    public async Task Two_users_with_separate_cookie_jars_never_see_each_others_account()
    {
        var emailA = AuthApiFixture.NewEmail();
        var emailB = AuthApiFixture.NewEmail();
        var a = fixture.Dev();
        var b = fixture.Dev();
        await a.RegisterAsync(emailA, AuthApiFixture.NewPassword());
        await b.RegisterAsync(emailB, AuthApiFixture.NewPassword());

        for (var i = 0; i < 3; i++) // art arda, karışık sırayla
        {
            using var meB = await (await b.GetAsync("/api/v1/auth/me")).JsonAsync();
            using var meA = await (await a.GetAsync("/api/v1/auth/me")).JsonAsync();
            Assert.Equal(emailA, meA.RootElement.GetProperty("email").GetString());
            Assert.Equal(emailB, meB.RootElement.GetProperty("email").GetString());
            Assert.NotEqual(meA.RootElement.GetProperty("id").GetGuid(), meB.RootElement.GetProperty("id").GetGuid());
        }

        // A çıkış yapınca B etkilenmez.
        await a.LogoutAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Two_users_requesting_in_parallel_get_their_own_accounts()
    {
        var users = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            var session = fixture.Dev();
            var email = AuthApiFixture.NewEmail();
            await session.RegisterAsync(email, AuthApiFixture.NewPassword());
            return (session, email);
        }));

        var results = await Task.WhenAll(users.SelectMany(u => Enumerable.Range(0, 3).Select(async _ =>
        {
            using var json = await (await u.session.GetAsync("/api/v1/auth/me")).JsonAsync();
            return (Expected: u.email, Actual: json.RootElement.GetProperty("email").GetString());
        })));

        Assert.All(results, r => Assert.Equal(r.Expected, r.Actual));
    }

    // ===================== Anonim katalog ve yan etkisiz GET =====================

    [Fact]
    public async Task Public_catalog_stays_anonymous()
    {
        var session = fixture.Dev();

        var categories = await session.GetAsync("/api/v1/categories");
        var products = await session.GetAsync("/api/v1/products");
        var missing = await session.GetAsync("/api/v1/products/yok-boyle-urun");

        Assert.Equal(HttpStatusCode.OK, categories.StatusCode);
        Assert.Equal(HttpStatusCode.OK, products.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode); // 401 değil
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Get_endpoints_do_not_change_user_state()
    {
        var email = AuthApiFixture.NewEmail();
        var (adminEmail, adminPassword, _) = await AuthApiFixture.CreateUserWithRoleAsync(fixture.DevelopmentFactory, AppRoles.Admin);
        var session = fixture.Dev();
        await session.RegisterAsync(email, AuthApiFixture.NewPassword());
        var admin = fixture.Dev();
        await admin.LoginAsync(adminEmail, adminPassword);
        var ct = TestContext.Current.CancellationToken;
        const string Snapshot = "SELECT count(*) || '|' || coalesce(string_agg(\"SecurityStamp\" || '/' || \"ConcurrencyStamp\" || '/' || \"AccessFailedCount\" || '/' || coalesce(\"LockoutEnd\"::text, '-'), ',' ORDER BY \"Id\"), '') FROM \"AspNetUsers\"";
        var before = await fixture.Database.ScalarAsync<string>(Snapshot, ct);

        foreach (var url in new[] { "/api/v1/auth/me", "/api/v1/auth/csrf", "/api/v1/categories", "/api/v1/products", "/health/live" })
        {
            await session.GetAsync(url);
        }

        await admin.GetAsync("/api/v1/admin/me");
        await fixture.Dev().GetAsync("/api/v1/admin/me");

        Assert.Equal(before, await fixture.Database.ScalarAsync<string>(Snapshot, ct));
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/auth/me")).StatusCode); // GET'ler oturumu bozmadı
    }
}

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Database;
using SSCommerce.Application.Showcase;

namespace SSCommerce.Api.Tests.Showcase;

/// <summary>
/// <c>GET/POST /api/v1/auth/demo</c> (VT-2, D-092): parolasız oturum açan, bu yüzden projenin EN RİSKLİ ucu. Gerçek PostgreSQL, tam HTTP hattı.
/// Sınıf ortak, tohumlanmış tek veritabanını paylaşır (<see cref="SeededShowcaseFixture"/>); oturum/kilit/rol durumunu değiştiren testler kendi
/// boş veritabanını açar. Önce: kapalıyken uç YOK, sonra yetki (rol, hedeflenemezlik, CSRF/Origin, hız), en sonda mutlu yol ayrıntıları.
/// </summary>
public class DemoSignInApiTests(SeededShowcaseFixture fixture) : IClassFixture<SeededShowcaseFixture>
{
    private const string DemoUrl = "/api/v1/auth/demo";
    private const string AuthCookie = "ssc.auth";

    private static async Task AssertProblem(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = await response.JsonAsync();
        Assert.Equal(status, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
    }

    private static void AssertNoSession(TestSession session, HttpResponseMessage response)
    {
        Assert.DoesNotContain(session.Cookies.Keys, k => k == AuthCookie);
        Assert.DoesNotContain(TestSession.ParseSetCookies(response), c => c.Name == AuthCookie && !c.IsDeletion);
    }

    private static async Task<HttpResponseMessage> DemoAsync(TestSession session, object? body, string? origin = null)
    {
        var token = await session.FetchCsrfAsync();
        return await session.PostJsonAsync(DemoUrl, body, token, origin);
    }

    private static async Task<HttpResponseMessage> DemoRawAsync(TestSession session, string? raw)
    {
        var token = await session.FetchCsrfAsync();
        return await session.SendAsync(HttpMethod.Post, DemoUrl, raw, csrf: token);
    }

    // ===================== Showcase KAPALI: uç hiç kayıtlı değil (Negative / API) =====================

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task Negative_when_showcase_is_off_both_demo_routes_are_404_even_with_accounts_seeded(string method)
    {
        // Hesaplar VAR (tohumlanmış veritabanı): kapalı bayrak tek başına yeterli olmalı. 404 = yönlendirme düzeyi; ne 400 ne 401 ne 409.
        await using var factory = ShowcaseKit.Factory(fixture.Database, enabled: false);
        var session = ShowcaseKit.NewSession(factory);
        var token = await session.FetchCsrfAsync();

        var response = method == "GET"
            ? await session.GetAsync(DemoUrl)
            : await session.PostJsonAsync(DemoUrl, new { account = "customer" }, token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertNoSession(session, response);
    }

    [Fact]
    public async Task Negative_when_showcase_is_off_the_flag_defaults_to_closed_and_openapi_does_not_mention_the_route()
    {
        await using var closed = ShowcaseKit.Factory(fixture.Database, enabled: false);
        await using var open = ShowcaseKit.Factory(fixture.Database, enabled: true);
        using var closedClient = closed.CreateClient();
        using var openClient = open.CreateClient();

        var closedDoc = await (await closedClient.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var openDoc = await (await openClient.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("auth/demo", closedDoc); // varlığı bile sızmaz
        Assert.Contains("/api/v1/auth/demo", openDoc); // pozitif kontrol: açıkken gerçekten var
    }

    [Fact]
    public async Task Negative_without_any_showcase_setting_the_routes_do_not_exist()
    {
        // Anahtar HİÇ verilmemiş (varsayılan false): üretim dağıtım dosyaları anahtarı hiç açmaz.
        await using var factory = ApiFactory.Create("Development", fixture.Database.ConnectionString, extraConfiguration: AuthApiFixture.HighLimits);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(DemoUrl, TestContext.Current.CancellationToken)).StatusCode);
    }

    // ===================== Showcase AÇIK: mutlu yol (API) =====================

    [Fact]
    public async Task Api_get_lists_the_three_account_keys_in_order_to_anonymous_visitors()
    {
        var session = ShowcaseKit.NewSession(fixture.Factory);

        var response = await session.GetAsync(DemoUrl);
        using var json = await response.JsonAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["accounts"], json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["customer", "admin", "warehouse"], json.RootElement.GetProperty("accounts").EnumerateArray().Select(a => a.GetString()!).ToArray());
        Assert.Empty(session.Cookies); // listelemek oturum açmaz
    }

    [Theory]
    [InlineData("customer", ShowcaseAccounts.CustomerEmail, "Customer", false, false)]
    [InlineData("admin", ShowcaseAccounts.AdminEmail, "Admin", true, true)]
    [InlineData("warehouse", ShowcaseAccounts.WarehouseEmail, "Warehouse", false, true)]
    public async Task Api_post_signs_in_the_fixed_account_with_the_right_role_and_the_cookie_works_on_me(
        string key, string email, string role, bool isAdmin, bool isWarehouse)
    {
        var session = ShowcaseKit.NewSession(fixture.Factory);

        var response = await DemoAsync(session, new { account = key });
        var raw = await response.TextAsync();
        using var json = JsonDocument.Parse(raw);
        var root = json.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(email, root.GetProperty("email").GetString());
        Assert.Equal([role], root.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray());
        Assert.Equal(isAdmin, root.GetProperty("capabilities").GetProperty("admin").GetBoolean());
        Assert.Equal(isWarehouse, root.GetProperty("capabilities").GetProperty("warehouse").GetBoolean());
        Assert.DoesNotContain("passwordhash", raw, StringComparison.OrdinalIgnoreCase);

        // Cookie normal girişle AYNI: HttpOnly, SameSite, kalıcı değil (oturum cookie'si).
        var cookie = Assert.Single(TestSession.ParseSetCookies(response), c => c.Name == AuthCookie);
        Assert.True(cookie.Has("httponly"));
        Assert.True(cookie.Has("samesite"));
        Assert.False(cookie.Has("expires")); // isPersistent: false
        Assert.False(cookie.Has("max-age"));

        // Dönen cookie ile /me AYNI kullanıcı.
        using var me = await (await session.GetAsync("/api/v1/auth/me")).JsonAsync();
        Assert.Equal(root.GetProperty("id").GetGuid(), me.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(email, me.RootElement.GetProperty("email").GetString());
    }

    [Theory]
    [InlineData("customer", 403)]
    [InlineData("warehouse", 403)]
    [InlineData("admin", 200)]
    public async Task Api_demo_sessions_get_exactly_the_authority_of_their_role_on_an_admin_only_route(string key, int expectedStatus)
    {
        // Yetki yükselmesi kontrolü: "parolasız oturum" yalnız rolün izin verdiği kapıları açar.
        var session = ShowcaseKit.NewSession(fixture.Factory);
        Assert.Equal(HttpStatusCode.OK, (await DemoAsync(session, new { account = key })).StatusCode);

        var response = await session.GetAsync("/api/v1/admin/categories");

        Assert.Equal((HttpStatusCode)expectedStatus, response.StatusCode);
    }

    [Fact]
    public async Task Api_repeating_the_demo_sign_in_is_harmless_and_returns_the_same_account()
    {
        // Idempotency: aynı istek iki kez (çift tıklama); ikisi de 200 ve aynı kimlik.
        var session = ShowcaseKit.NewSession(fixture.Factory);

        var first = await DemoAsync(session, new { account = "customer" });
        var second = await DemoAsync(session, new { account = "customer" });
        using var a = await first.JsonAsync();
        using var b = await second.JsonAsync();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(a.RootElement.GetProperty("id").GetGuid(), b.RootElement.GetProperty("id").GetGuid());
    }

    // ===================== Girdi doğrulaması (Negative / Edge) =====================

    [Theory]
    [InlineData("{}")]                                              // account eksik
    [InlineData("{\"account\":null}")]
    [InlineData("{\"account\":\"\"}")]                              // boş
    [InlineData("{\"account\":\"root\"}")]                          // bilinmeyen
    [InlineData("{\"account\":\"Customer\"}")]                      // büyük harf varyantı: tam eşleşme
    [InlineData("{\"account\":\"ADMIN\"}")]
    [InlineData("{\"account\":\" customer\"}")]                     // kırpma YOK
    [InlineData("{\"account\":\"customer \"}")]
    [InlineData("{\"account\":\"demo-yonetici@sscommerce.example\"}")] // e-posta anahtar yerine geçmez
    [InlineData("{\"email\":\"demo-yonetici@sscommerce.example\"}")]    // yalnız e-posta (hedefleme denemesi), account yok
    [InlineData("{\"account\":123}")]                               // yanlış tip
    [InlineData("{\"account\":[\"customer\"]}")]
    [InlineData("[]")]                                              // nesne yerine dizi
    [InlineData("null")]
    [InlineData("bozuk json")]
    [InlineData("")]                                                // boş gövde
    public async Task Negative_missing_empty_unknown_or_malformed_account_is_400_validation_failed_and_no_session(string rawBody)
    {
        var session = ShowcaseKit.NewSession(fixture.Factory);

        var response = await DemoRawAsync(session, rawBody);

        await AssertProblem(response, 400, "validation_failed");
        AssertNoSession(session, response);
    }

    [Fact]
    public async Task Negative_an_unknown_account_names_only_the_allowed_keys_in_the_error()
    {
        var session = ShowcaseKit.NewSession(fixture.Factory);

        var response = await DemoAsync(session, new { account = "root" });
        var text = await response.TextAsync();

        Assert.Contains("account", text);
        Assert.Contains("customer, admin, warehouse", text);
        Assert.DoesNotContain("@", text); // sabit e-postalar hata iletisinde görünmez
    }

    [Fact]
    public async Task Negative_oversized_body_is_rejected_with_400_not_read_to_the_end()
    {
        var session = ShowcaseKit.NewSession(fixture.Factory);

        var response = await DemoRawAsync(session, "{\"account\":\"customer\",\"pad\":\"" + new string('x', 64 * 1024) + "\"}");

        await AssertProblem(response, 400, "validation_failed");
        AssertNoSession(session, response);
    }

    // ===================== Başka kullanıcı hedeflenemez (Negative — yetki) =====================

    [Fact]
    public async Task Negative_extra_email_userId_and_role_fields_cannot_retarget_the_sign_in()
    {
        // Saldırgan "customer" anahtarıyla gider ama gövdeye yönetici e-postasını/kimliğini/rolünü koyar: fazlası YOK SAYILIR.
        var adminId = await ShowcaseKit.Sql<Guid>(fixture.Database, $"select \"Id\" from \"AspNetUsers\" where \"Email\" = '{ShowcaseAccounts.AdminEmail}'");
        var session = ShowcaseKit.NewSession(fixture.Factory);

        var response = await DemoRawAsync(
            session,
            $$"""
            {"account":"customer","email":"{{ShowcaseAccounts.AdminEmail}}","userId":"{{adminId}}","id":"{{adminId}}","role":"Admin","roles":["Admin"],"isAdmin":true}
            """);
        using var json = await response.JsonAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ShowcaseAccounts.CustomerEmail, json.RootElement.GetProperty("email").GetString());
        Assert.Equal(["Customer"], json.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray());
        Assert.NotEqual(adminId, json.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await session.GetAsync("/api/v1/admin/categories")).StatusCode);
    }

    [Fact]
    public async Task Negative_a_normal_registered_user_cannot_be_signed_in_through_the_demo_route()
    {
        // Gerçek bir müşteri kayıtlıyken bile demo uç yalnız üç sabit e-postayı bilir.
        var victimEmail = AuthApiFixture.NewEmail();
        var victim = ShowcaseKit.NewSession(fixture.Factory);
        Assert.Equal(HttpStatusCode.Created, (await victim.RegisterAsync(victimEmail, AuthApiFixture.NewPassword())).StatusCode);
        var attacker = ShowcaseKit.NewSession(fixture.Factory);

        var response = await DemoRawAsync(attacker, $$"""{"account":"{{victimEmail}}","email":"{{victimEmail}}"}""");

        await AssertProblem(response, 400, "validation_failed");
        AssertNoSession(attacker, response);
    }

    // ===================== Hesap durumu: tohum yok / yanlış rol / kilit (KENDİ boş veritabanı) =====================

    [Theory]
    [InlineData("customer")]
    [InlineData("admin")]
    [InlineData("warehouse")]
    public async Task Negative_before_the_seed_has_run_every_account_is_409_showcase_not_seeded(string key)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = ShowcaseKit.Factory(database, withClock: false);
        var session = ShowcaseKit.NewSession(factory);

        var response = await DemoAsync(session, new { account = key });

        await AssertProblem(response, 409, "showcase_not_seeded");
        AssertNoSession(session, response);
        Assert.Equal(0, await ShowcaseKit.Sql<long>(database, "select count(*) from \"AspNetUsers\"")); // demo uç hesap OLUŞTURMAZ
    }

    [Theory]
    [InlineData("admin", ShowcaseAccounts.AdminEmail, "Customer")]
    [InlineData("warehouse", ShowcaseAccounts.WarehouseEmail, "Customer")]
    [InlineData("customer", ShowcaseAccounts.CustomerEmail, "Admin")]
    public async Task Negative_a_showcase_email_registered_earlier_with_another_role_gains_no_authority(string key, string email, string existingRole)
    {
        // Saldırı: biri vitrin yöneticisi e-postasını müşteri olarak kaydeder, sonra "admin" anahtarını ister. Yetki kazanmamalı, oturum açılmamalı.
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = ShowcaseKit.Factory(database, withClock: false);
        await ShowcaseKit.CreateUserAsync(factory, email, existingRole);
        var session = ShowcaseKit.NewSession(factory);

        var response = await DemoAsync(session, new { account = key });

        await AssertProblem(response, 409, "showcase_not_seeded");
        AssertNoSession(session, response);
        Assert.Equal(existingRole, await ShowcaseKit.RolesOfAsync(database, email)); // rol değişmedi
    }

    [Fact]
    public async Task Negative_a_manually_locked_showcase_account_is_409_showcase_account_locked_and_opens_no_session()
    {
        // Seed kilidi kapattığı için seed'li hesaplar kilitlenemez; kilidi AÇIK bir hesap (CreateUserAsync = seed'siz, LockoutEnabled=true) kilitliyse demo uç
        // kilidi AŞMAZ. Kilit 5 yanlış parola (MaxFailedAccessAttempts) ile gerçek yoldan kurulur.
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = ShowcaseKit.Factory(database, withClock: false);
        await ShowcaseKit.CreateUserAsync(factory, ShowcaseAccounts.CustomerEmail, "Customer");
        var attacker = ShowcaseKit.NewSession(factory);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.LoginAsync(ShowcaseAccounts.CustomerEmail, "Yanlis-parola-1")).StatusCode);
        }

        var session = ShowcaseKit.NewSession(factory);
        var response = await DemoAsync(session, new { account = "customer" });

        await AssertProblem(response, 409, "showcase_account_locked");
        AssertNoSession(session, response);
        Assert.True(await ShowcaseKit.Sql<long>(database, $"select count(*) from \"AspNetUsers\" where \"Email\" = '{ShowcaseAccounts.CustomerEmail}' and \"LockoutEnd\" > now()") == 1);
    }

    [Theory]
    [InlineData(ShowcaseAccounts.CustomerEmail, "customer")]
    [InlineData(ShowcaseAccounts.AdminEmail, "admin")]
    [InlineData(ShowcaseAccounts.WarehouseEmail, "warehouse")]
    public async Task Regression_wrong_passwords_against_a_seeded_showcase_email_cannot_lock_the_demo_sign_in(string email, string key)
    {
        // Kilitleme DoS: sabit e-postaya /auth/login ile 5+ yanlış parola eskiden hesabı 15 dk kilitleyip demo girişi kapatıyordu.
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = ShowcaseKit.Factory(database);
        Assert.Equal(0, (await ShowcaseKit.SeedAsync(factory)).ExitCode);
        var attacker = ShowcaseKit.NewSession(factory);

        for (var i = 0; i < 7; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.LoginAsync(email, "Yanlis-parola-1")).StatusCode);
        }

        var response = await DemoAsync(ShowcaseKit.NewSession(factory), new { account = key });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            0,
            await ShowcaseKit.Sql<long>(database, $"select count(*) from \"AspNetUsers\" where \"Email\" = '{email}' and (\"LockoutEnd\" is not null or \"AccessFailedCount\" <> 0)"));
    }

    [Fact]
    public async Task Edge_a_lock_that_has_expired_no_longer_blocks_the_demo_sign_in()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = ShowcaseKit.Factory(database, withClock: false);
        await ShowcaseKit.CreateUserAsync(factory, ShowcaseAccounts.WarehouseEmail, "Warehouse");
        await database.ExecuteAsync(
            $"update \"AspNetUsers\" set \"LockoutEnd\" = now() - interval '1 minute', \"AccessFailedCount\" = 0 where \"Email\" = '{ShowcaseAccounts.WarehouseEmail}'",
            TestContext.Current.CancellationToken);
        var session = ShowcaseKit.NewSession(factory);

        var response = await DemoAsync(session, new { account = "warehouse" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Negative_the_lock_of_one_showcase_account_does_not_affect_the_other_two()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = ShowcaseKit.Factory(database, withClock: false);
        await ShowcaseKit.CreateUserAsync(factory, ShowcaseAccounts.CustomerEmail, "Customer");
        await ShowcaseKit.CreateUserAsync(factory, ShowcaseAccounts.AdminEmail, "Admin");
        await database.ExecuteAsync(
            $"update \"AspNetUsers\" set \"LockoutEnd\" = now() + interval '1 hour' where \"Email\" = '{ShowcaseAccounts.CustomerEmail}'", TestContext.Current.CancellationToken);

        await AssertProblem(await DemoAsync(ShowcaseKit.NewSession(factory), new { account = "customer" }), 409, "showcase_account_locked");
        Assert.Equal(HttpStatusCode.OK, (await DemoAsync(ShowcaseKit.NewSession(factory), new { account = "admin" })).StatusCode);
    }

    // ===================== CSRF + Origin (login ile aynı kurallar) =====================

    [Fact]
    public async Task Negative_post_without_a_csrf_token_is_400_csrf_invalid_and_opens_no_session()
    {
        var session = ShowcaseKit.NewSession(fixture.Factory);
        await session.FetchCsrfAsync(); // antiforgery cookie'si var (tarayıcı cookie'yi kendisi yollar), başlık yok

        var response = await session.PostJsonAsync(DemoUrl, new { account = "customer" }, csrf: null);

        await AssertProblem(response, 400, "csrf_invalid");
        AssertNoSession(session, response);
    }

    [Theory]
    [InlineData("bu-gecerli-bir-token-degil")]
    [InlineData("")]
    public async Task Negative_post_with_a_garbage_csrf_token_is_400_csrf_invalid(string token)
    {
        var session = ShowcaseKit.NewSession(fixture.Factory);
        await session.FetchCsrfAsync();

        var response = await session.PostJsonAsync(DemoUrl, new { account = "customer" }, token);

        await AssertProblem(response, 400, "csrf_invalid");
        AssertNoSession(session, response);
    }

    [Fact]
    public async Task Negative_a_valid_token_without_its_antiforgery_cookie_is_rejected()
    {
        var session = ShowcaseKit.NewSession(fixture.Factory);
        var token = await session.FetchCsrfAsync();
        session.Cookies.Remove(session.Cookies.Keys.Single(k => k.Contains("csrf")));

        var response = await session.PostJsonAsync(DemoUrl, new { account = "customer" }, token);

        await AssertProblem(response, 400, "csrf_invalid");
        AssertNoSession(session, response);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:3001")]
    [InlineData("null")]
    public async Task Negative_a_foreign_origin_is_403_even_with_a_valid_csrf_token(string origin)
    {
        var session = ShowcaseKit.NewSession(fixture.Factory);

        var response = await DemoAsync(session, new { account = "customer" }, origin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertNoSession(session, response);
    }

    [Fact]
    public async Task Api_the_allowed_development_origin_is_accepted()
    {
        var session = ShowcaseKit.NewSession(fixture.Factory);

        var response = await DemoAsync(session, new { account = "customer" }, "http://localhost:3000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ===================== Hız sınırı (Negative) =====================

    [Fact]
    public async Task Negative_the_demo_sign_in_shares_the_credentials_rate_limit_and_429_opens_no_session()
    {
        await using var factory = ShowcaseKit.Factory(
            fixture.Database,
            extra: new Dictionary<string, string?>
            {
                ["Security:RateLimit:CredentialsPermitLimit"] = "2",
                ["Security:RateLimit:WindowSeconds"] = "600", // test sırasında pencere dolmaz (flaky olmaz)
            });
        var session = ShowcaseKit.NewSession(factory);
        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++)
        {
            var attempt = ShowcaseKit.NewSession(factory);
            var attemptToken = await attempt.FetchCsrfAsync();
            last = await attempt.PostJsonAsync(DemoUrl, new { account = "admin" }, attemptToken);
            statuses.Add(last.StatusCode);
            if (i >= 2)
            {
                AssertNoSession(attempt, last);
            }
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
        await AssertProblem(last!, 429, "rate_limited");
        Assert.True(last!.Headers.RetryAfter?.Delta is { } delta && delta.TotalSeconds is > 0 and <= 600);
    }

    [Fact]
    public async Task Edge_the_credentials_limit_does_not_throttle_the_read_only_account_list()
    {
        await using var factory = ShowcaseKit.Factory(
            fixture.Database, extra: new Dictionary<string, string?> { ["Security:RateLimit:CredentialsPermitLimit"] = "1" });
        var session = ShowcaseKit.NewSession(factory);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await session.GetAsync(DemoUrl)).StatusCode);
        }
    }

    // ===================== Günlük (Negative) =====================

    [Fact]
    public async Task Negative_signing_in_logs_the_account_key_but_never_the_email_or_secrets()
    {
        var logs = new ShowcaseKit.MemoryLogs();
        await using var factory = ShowcaseKit.Factory(fixture.Database, withClock: false, logs: logs);
        var session = ShowcaseKit.NewSession(factory);

        Assert.Equal(HttpStatusCode.OK, (await DemoAsync(session, new { account = "customer" })).StatusCode);

        var hash = await ShowcaseKit.Sql<string>(fixture.Database, $"select \"PasswordHash\" from \"AspNetUsers\" where \"Email\" = '{ShowcaseAccounts.CustomerEmail}'");
        var stamp = await ShowcaseKit.Sql<string>(fixture.Database, $"select \"SecurityStamp\" from \"AspNetUsers\" where \"Email\" = '{ShowcaseAccounts.CustomerEmail}'");
        var lines = logs.Lines.ToArray();
        var joined = string.Join('\n', lines);

        Assert.Contains(lines, l => l.Contains("Vitrin hesabıyla giriş yapıldı") && l.Contains("customer")); // pozitif kontrol
        Assert.DoesNotContain(ShowcaseAccounts.CustomerEmail, joined);
        Assert.DoesNotContain("sscommerce.example", joined);
        Assert.DoesNotContain(hash!, joined);
        Assert.DoesNotContain(stamp!, joined);
        Assert.DoesNotContain(session.Cookies[AuthCookie], joined); // oturum cookie'si günlüğe girmez
    }
}

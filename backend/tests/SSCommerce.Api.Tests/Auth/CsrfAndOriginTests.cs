using System.Net;
using System.Text.Json;

namespace SSCommerce.Api.Tests.Auth;

/// <summary>
/// CSRF (antiforgery) ve Origin savunması. .NET antiforgery servisinin yalnız KAYITLI olması yetmez (GOAL F4.2): burada
/// token'ın gerçekten doğrulandığı gösterilir: yok / yanlış / başka kullanıcıya ait / eski oturuma ait token reddedilir, doğrusu işler.
/// Reddedilme biçimi: 400 <c>csrf_invalid</c> (403 yalnız yetki ve Origin için).
/// </summary>
[Collection(AuthApiCollection.Name)]
public class CsrfAndOriginTests(AuthApiFixture fixture)
{
    private static async Task AssertCsrfRejected(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = await response.JsonAsync();
        Assert.Equal("csrf_invalid", json.RootElement.GetProperty("code").GetString());
    }

    // Üç uç da aynı kuralı paylaşır: (uç, oturum kurulumu, gövde)
    public static TheoryData<string> Endpoints => new() { "register", "login", "logout" };

    private async Task<(TestSession Session, Func<string?, Task<HttpResponseMessage>> Send, Func<Task<string>> GoodToken)> ArrangeAsync(string endpoint)
    {
        var session = fixture.Dev();
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        object? body;
        Func<Task<string>> goodToken;

        switch (endpoint)
        {
            case "register":
                body = new { email, password };
                goodToken = session.FetchCsrfAsync; // anonim token
                break;
            case "login":
                await fixture.Dev().RegisterAsync(email, password);
                body = new { email, password };
                goodToken = session.FetchCsrfAsync;
                break;
            default: // logout: önce oturum aç; token oturumdaki kullanıcıya bağlı
                await session.RegisterAsync(email, password);
                body = null;
                goodToken = session.FetchCsrfAsync;
                break;
        }

        return (session, token => session.PostJsonAsync($"/api/v1/auth/{endpoint}", body, token), goodToken);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Request_without_any_token_is_rejected(string endpoint)
    {
        var (_, send, _) = await ArrangeAsync(endpoint);

        await AssertCsrfRejected(await send(null));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Request_with_a_garbage_token_is_rejected(string endpoint)
    {
        var (session, send, goodToken) = await ArrangeAsync(endpoint);
        await goodToken(); // antiforgery cookie'si var, ama başlıktaki token sahte

        await AssertCsrfRejected(await send("bu-gecerli-bir-token-degil"));
        await AssertCsrfRejected(await send(string.Empty));
        await AssertCsrfRejected(await send(session.Csrf + "x")); // geçerli token'ın bozulmuş hali
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Token_without_its_antiforgery_cookie_is_rejected(string endpoint)
    {
        var (session, _, goodToken) = await ArrangeAsync(endpoint);
        var token = await goodToken();
        var csrfCookie = session.Cookies.Keys.Single(k => k.Contains("csrf"));
        session.Cookies.Remove(csrfCookie); // başlık var, eşi olan cookie yok (başka site token'ı yazdırabilse bile cookie'yi yazdıramaz)

        var response = await session.PostJsonAsync($"/api/v1/auth/{endpoint}", endpoint == "logout" ? null : new { email = "a@b.c", password = "x" }, token);

        await AssertCsrfRejected(response);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Cookie_without_the_header_is_rejected(string endpoint)
    {
        var (session, send, goodToken) = await ArrangeAsync(endpoint);
        await goodToken();
        Assert.Contains(session.Cookies.Keys, k => k.Contains("csrf"));

        await AssertCsrfRejected(await send(null)); // cookie otomatik gider (CSRF saldırısının yaptığı), başlık gitmez
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Request_with_a_valid_token_succeeds(string endpoint)
    {
        var (_, send, goodToken) = await ArrangeAsync(endpoint);
        var token = await goodToken();

        var response = await send(token);

        Assert.True(
            response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"{endpoint}: {(int)response.StatusCode}");
    }

    [Fact]
    public async Task A_rejected_csrf_request_has_no_side_effects()
    {
        var session = fixture.Dev();
        var email = AuthApiFixture.NewEmail();

        await session.FetchCsrfAsync();
        await session.PostJsonAsync("/api/v1/auth/register", new { email, password = AuthApiFixture.NewPassword() }, "sahte");

        Assert.Equal(0, await fixture.Database.ScalarAsync<long>($"SELECT count(*) FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", TestContext.Current.CancellationToken));
        Assert.DoesNotContain(session.Cookies.Keys, k => k.Contains("auth"));
    }

    // ---------- Token kullanıcıya bağlıdır ----------

    [Fact]
    public async Task Token_fetched_before_login_is_rejected_for_the_authenticated_user_until_refetched()
    {
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        await fixture.Dev().RegisterAsync(email, password);
        var session = fixture.Dev();
        var anonymousToken = await session.FetchCsrfAsync();

        var login = await session.PostJsonAsync("/api/v1/auth/login", new { email, password }, anonymousToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Anonim token, artık oturumlu kullanıcıyla eşleşmez.
        await AssertCsrfRejected(await session.PostJsonAsync("/api/v1/auth/logout", null, anonymousToken));

        var fresh = await session.FetchCsrfAsync();
        Assert.NotEqual(anonymousToken, fresh);
        Assert.Equal(HttpStatusCode.NoContent, (await session.PostJsonAsync("/api/v1/auth/logout", null, fresh)).StatusCode);
    }

    [Fact]
    public async Task Token_of_user_A_is_rejected_in_user_Bs_session()
    {
        var a = fixture.Dev();
        var b = fixture.Dev();
        await a.RegisterAsync(AuthApiFixture.NewEmail(), AuthApiFixture.NewPassword());
        await b.RegisterAsync(AuthApiFixture.NewEmail(), AuthApiFixture.NewPassword());
        var tokenA = await a.FetchCsrfAsync();
        var tokenB = await b.FetchCsrfAsync();

        // A'nın token'ı B'nin cookie'leriyle (B'nin antiforgery cookie'si dahil) çıkış yapamaz.
        await AssertCsrfRejected(await b.PostJsonAsync("/api/v1/auth/logout", null, tokenA));
        // B'nin oturumu hâlâ ayakta; kendi token'ı işler.
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await b.PostJsonAsync("/api/v1/auth/logout", null, tokenB)).StatusCode);
    }

    [Fact]
    public async Task Token_of_user_A_with_As_antiforgery_cookie_in_Bs_authenticated_session_is_rejected()
    {
        var a = fixture.Dev();
        var b = fixture.Dev();
        await a.RegisterAsync(AuthApiFixture.NewEmail(), AuthApiFixture.NewPassword());
        await b.RegisterAsync(AuthApiFixture.NewEmail(), AuthApiFixture.NewPassword());
        var tokenA = await a.FetchCsrfAsync();

        // Saldırgan A'nın hem token'ını hem antiforgery cookie'sini yazdırabildiğini varsay; B'nin AUTH cookie'si yine B'dir.
        var csrfName = a.Cookies.Keys.Single(k => k.Contains("csrf"));
        b.Cookies[csrfName] = a.Cookies[csrfName];

        await AssertCsrfRejected(await b.PostJsonAsync("/api/v1/auth/logout", null, tokenA));
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Token_issued_before_logout_cannot_be_reused_after_the_next_login_as_another_user()
    {
        var emailA = AuthApiFixture.NewEmail();
        var passwordA = AuthApiFixture.NewPassword();
        var session = fixture.Dev();
        await session.RegisterAsync(emailA, passwordA);
        var tokenOfA = await session.FetchCsrfAsync();
        await session.PostJsonAsync("/api/v1/auth/logout", null, tokenOfA);

        // Aynı tarayıcıda başka bir kullanıcı girer; A'nın eski token'ı artık işe yaramaz.
        var emailB = AuthApiFixture.NewEmail();
        var passwordB = AuthApiFixture.NewPassword();
        await fixture.Dev().RegisterAsync(emailB, passwordB);
        await session.LoginAsync(emailB, passwordB);

        await AssertCsrfRejected(await session.PostJsonAsync("/api/v1/auth/logout", null, tokenOfA));
    }

    // ---------- /auth/csrf ----------

    [Fact]
    public async Task Csrf_endpoint_returns_a_token_with_no_store_and_sets_the_antiforgery_cookie()
    {
        var session = fixture.Dev();

        var response = await session.GetAsync("/api/v1/auth/csrf");
        using var json = await response.JsonAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(["token"], json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("token").GetString()));
        Assert.Contains(TestSession.ParseSetCookies(response), c => c.Name == "ssc.csrf");
        Assert.DoesNotContain(TestSession.ParseSetCookies(response), c => c.Name == "ssc.auth"); // oturum açmaz
    }

    [Fact]
    public async Task Csrf_endpoint_is_available_to_anonymous_and_does_not_require_a_token_itself()
    {
        var response = await fixture.Dev().GetAsync("/api/v1/auth/csrf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Csrf_token_endpoint_answers_to_the_same_cookie_with_working_tokens_each_time()
    {
        var session = fixture.Dev();
        var first = await session.FetchCsrfAsync();
        var second = await session.FetchCsrfAsync();
        var email = AuthApiFixture.NewEmail();

        // İkisi de aynı cookie çiftiyle geçerli (JS her istekte yenilese de önceki token bozulmaz).
        var withFirst = await session.PostJsonAsync("/api/v1/auth/register", new { email, password = AuthApiFixture.NewPassword() }, first);

        Assert.Equal(HttpStatusCode.Created, withFirst.StatusCode);
        Assert.False(string.IsNullOrEmpty(second));
    }

    // ---------- Origin ----------

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:3001")]       // port farkı
    [InlineData("http://localhost")]            // portsuz
    [InlineData("https://localhost:3000")]      // şema farkı
    [InlineData("null")]                        // sandbox / gizlilik modu
    [InlineData("http://localhost:3000.evil.example")]
    public async Task Foreign_origin_is_rejected_with_403_even_when_the_csrf_token_is_valid(string origin)
    {
        var session = fixture.Dev();
        var email = AuthApiFixture.NewEmail();
        var token = await session.FetchCsrfAsync();

        var response = await session.PostJsonAsync("/api/v1/auth/register", new { email, password = AuthApiFixture.NewPassword() }, token, origin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var json = await response.JsonAsync();
        Assert.Equal("origin_not_allowed", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, await fixture.Database.ScalarAsync<long>($"SELECT count(*) FROM \"AspNetUsers\" WHERE \"Email\" = '{email}'", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("http://localhost:3200")]
    [InlineData("HTTP://LOCALHOST:3000")]       // şema/host büyük-küçük harf duyarsız
    [InlineData("http://localhost:3000/")]
    public async Task Allowed_origin_passes(string origin)
    {
        var session = fixture.Dev();
        var token = await session.FetchCsrfAsync();

        var response = await session.PostJsonAsync("/api/v1/auth/register", new { email = AuthApiFixture.NewEmail(), password = AuthApiFixture.NewPassword() }, token, origin);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Missing_origin_still_requires_the_csrf_token()
    {
        var session = fixture.Dev();
        await session.FetchCsrfAsync();

        var withoutToken = await session.PostJsonAsync("/api/v1/auth/register", new { email = AuthApiFixture.NewEmail(), password = AuthApiFixture.NewPassword() }, csrf: null, origin: null);

        await AssertCsrfRejected(withoutToken);
    }

    [Fact]
    public async Task Allowed_origin_does_not_replace_the_csrf_token()
    {
        var session = fixture.Dev();
        await session.FetchCsrfAsync();

        var response = await session.PostJsonAsync("/api/v1/auth/register", new { email = AuthApiFixture.NewEmail(), password = AuthApiFixture.NewPassword() }, csrf: "sahte", origin: "http://localhost:3000");

        await AssertCsrfRejected(response);
    }

    [Fact]
    public async Task Production_uses_only_the_configured_origin_and_rejects_development_origins()
    {
        var session = fixture.Prod();
        var token = await session.FetchCsrfAsync();

        var allowed = await session.PostJsonAsync("/api/v1/auth/register", new { email = AuthApiFixture.NewEmail(), password = AuthApiFixture.NewPassword() }, token, AuthApiFixture.ProductionOrigin);
        var session2 = fixture.Prod();
        var token2 = await session2.FetchCsrfAsync();
        var dev = await session2.PostJsonAsync("/api/v1/auth/register", new { email = AuthApiFixture.NewEmail(), password = AuthApiFixture.NewPassword() }, token2, "http://localhost:3000");

        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, dev.StatusCode);
    }

    [Fact]
    public async Task Origin_check_applies_to_logout_and_login_too()
    {
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        var session = fixture.Dev();
        await session.RegisterAsync(email, password);
        var token = await session.FetchCsrfAsync();

        var logout = await session.PostJsonAsync("/api/v1/auth/logout", null, token, "https://evil.example");
        var login = await fixture.Dev().PostJsonAsync("/api/v1/auth/login", new { email, password }, await session.FetchCsrfAsync(), "https://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/auth/me")).StatusCode); // reddedilen çıkış oturumu bozmadı
    }

    // ---------- CORS kapalı ----------

    [Theory]
    [InlineData("GET", "/api/v1/products")]
    [InlineData("GET", "/api/v1/auth/me")]
    [InlineData("GET", "/api/v1/auth/csrf")]
    [InlineData("OPTIONS", "/api/v1/auth/login")]
    [InlineData("OPTIONS", "/api/v1/products")]
    public async Task Cors_is_not_enabled_no_cross_origin_response_headers(string method, string url)
    {
        var session = fixture.Dev();
        var headers = new Dictionary<string, string>
        {
            ["Origin"] = "https://evil.example",
            ["Access-Control-Request-Method"] = "POST",
            ["Access-Control-Request-Headers"] = "x-csrf-token,content-type",
        };

        var response = await session.SendAsync(new HttpMethod(method), url, extraHeaders: headers);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Headers"));
    }

    // ---------- Yazma yöntemleri korumasız kalmasın ----------

    [Fact]
    public async Task Existing_write_methods_on_unrouted_paths_do_not_bypass_protection()
    {
        // Kataloğa yazma ucu yok (405). Grup düzeyindeki CSRF filtresi ileride eklenecek uçlar için de geçerlidir; şimdilik 405 doğrulanır.
        var session = fixture.Dev();
        var token = await session.FetchCsrfAsync();

        var response = await session.PostJsonAsync("/api/v1/products", new { name = "x" }, token);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Csrf_failure_body_is_problem_json_with_status_and_trace_id_and_no_internals()
    {
        var session = fixture.Dev();
        var response = await session.PostJsonAsync("/api/v1/auth/login", new { email = "a@b.c", password = "x" }, "sahte");
        var raw = await response.TextAsync();
        using var json = JsonDocument.Parse(raw);

        Assert.Equal(400, json.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("Antiforgery", raw);
        Assert.DoesNotContain("   at ", raw);
    }
}

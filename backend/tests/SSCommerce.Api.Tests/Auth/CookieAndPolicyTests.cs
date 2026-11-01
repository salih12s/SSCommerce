using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SSCommerce.Api.Security;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Auth;

/// <summary>Cookie bayrakları (ortama göre), Identity ayarları, politikalar ve 401/403 olayları.</summary>
[Collection(AuthApiCollection.Name)]
public class CookieAndPolicyTests(AuthApiFixture fixture)
{
    // ---------- Cookie bayrakları: gerçek HTTP yanıtından ----------

    [Fact]
    public async Task Development_auth_cookie_flags()
    {
        var session = fixture.Dev();

        var response = await session.RegisterAsync(AuthApiFixture.NewEmail(), AuthApiFixture.NewPassword());
        var cookie = Assert.Single(TestSession.ParseSetCookies(response), c => c.Name == "ssc.auth");

        Assert.True(cookie.Has("httponly"));                 // JS okuyamaz
        Assert.Equal("lax", cookie.Get("samesite")?.ToLowerInvariant());
        Assert.Equal("/", cookie.Get("path"));
        Assert.False(cookie.Has("secure"));                  // HTTP localhost çalışsın (SameAsRequest)
        Assert.False(cookie.Has("domain"));
        Assert.False(cookie.Has("expires"));                 // IsPersistent=false: oturum cookie'si, tarayıcı kapanınca biter
        Assert.False(cookie.Has("max-age"));
    }

    [Fact]
    public async Task Production_auth_cookie_flags_include_Secure_and_the_Host_prefix()
    {
        var session = fixture.Prod();

        var response = await session.RegisterAsync(AuthApiFixture.NewEmail(), AuthApiFixture.NewPassword());
        var cookie = Assert.Single(TestSession.ParseSetCookies(response), c => c.Name == "__Host-ssc.auth");

        Assert.True(cookie.Has("secure"));
        Assert.True(cookie.Has("httponly"));
        Assert.Equal("lax", cookie.Get("samesite")?.ToLowerInvariant());
        Assert.Equal("/", cookie.Get("path"));
        Assert.False(cookie.Has("domain")); // __Host- öneki Domain'i yasaklar
        Assert.False(cookie.Has("expires"));
        Assert.DoesNotContain(TestSession.ParseSetCookies(response), c => c.Name == "ssc.auth");
    }

    [Fact]
    public async Task Production_session_works_end_to_end_with_the_prefixed_cookie_and_logout_deletes_it()
    {
        var email = AuthApiFixture.NewEmail();
        var password = AuthApiFixture.NewPassword();
        var session = fixture.Prod();
        await session.RegisterAsync(email, password);

        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/auth/me")).StatusCode);
        var logout = await session.LogoutAsync();

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var deletion = Assert.Single(TestSession.ParseSetCookies(logout), c => c.Name == "__Host-ssc.auth");
        Assert.True(deletion.IsDeletion);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Antiforgery_cookie_flags_in_both_environments()
    {
        var dev = TestSession.ParseSetCookies(await fixture.Dev().GetAsync("/api/v1/auth/csrf"));
        var prod = TestSession.ParseSetCookies(await fixture.Prod().GetAsync("/api/v1/auth/csrf"));

        var devCookie = Assert.Single(dev, c => c.Name == "ssc.csrf");
        Assert.True(devCookie.Has("httponly"));
        Assert.Equal("strict", devCookie.Get("samesite")?.ToLowerInvariant());
        Assert.Equal("/", devCookie.Get("path"));
        Assert.False(devCookie.Has("secure"));

        var prodCookie = Assert.Single(prod, c => c.Name == "__Host-ssc.csrf");
        Assert.True(prodCookie.Has("httponly"));
        Assert.Equal("strict", prodCookie.Get("samesite")?.ToLowerInvariant());
        Assert.Equal("/", prodCookie.Get("path"));
        Assert.True(prodCookie.Has("secure"));
        Assert.False(prodCookie.Has("domain"));
    }

    // ---------- Ortam -> bayrak eşlemesi (saf fonksiyon) ----------

    [Theory]
    [InlineData("Development", "ssc.auth", CookieSecurePolicy.SameAsRequest)]
    [InlineData("Production", "__Host-ssc.auth", CookieSecurePolicy.Always)]
    [InlineData("Staging", "__Host-ssc.auth", CookieSecurePolicy.Always)]
    [InlineData("Testing", "__Host-ssc.auth", CookieSecurePolicy.Always)]
    public void Environment_decides_cookie_name_and_secure_policy(string environmentName, string expectedName, CookieSecurePolicy expectedPolicy)
    {
        var environment = new FakeEnvironment(environmentName);
        var options = new CookieAuthenticationOptions();

        AuthCookieSettings.Apply(options, environment);

        Assert.Equal(expectedName, options.Cookie.Name);
        Assert.Equal(expectedPolicy, options.Cookie.SecurePolicy);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
        Assert.Equal("/", options.Cookie.Path);
        Assert.Equal(TimeSpan.FromHours(8), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
        Assert.Null(options.Cookie.Domain);
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = ".";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    // ---------- Kayıtlı Identity / cookie / politika ayarları ----------

    [Fact]
    public void Identity_options_follow_the_security_decisions()
    {
        using var scope = fixture.ProductionFactory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value;

        Assert.Equal(10, identity.Password.RequiredLength);                 // D-035
        Assert.True(identity.Password.RequireDigit);
        Assert.True(identity.User.RequireUniqueEmail);
        Assert.True(identity.Lockout.AllowedForNewUsers);                    // yeni kullanıcıda da kilitleme açık
        Assert.Equal(5, identity.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), identity.Lockout.DefaultLockoutTimeSpan);
        Assert.False(identity.SignIn.RequireConfirmedEmail);                 // D-036: e-posta sağlayıcısı yok
        Assert.False(identity.SignIn.RequireConfirmedAccount);
    }

    [Fact]
    public void Security_stamp_is_validated_on_every_request()
    {
        using var scope = fixture.ProductionFactory.Services.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value;

        Assert.Equal(TimeSpan.Zero, options.ValidationInterval); // logout sonrası eski cookie hemen düşer
    }

    [Fact]
    public void Registered_application_cookie_options_match_the_environment()
    {
        var dev = fixture.DevelopmentFactory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var prod = fixture.ProductionFactory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);

        Assert.Equal("ssc.auth", dev.Cookie.Name);
        Assert.Equal(CookieSecurePolicy.SameAsRequest, dev.Cookie.SecurePolicy);
        Assert.Equal("__Host-ssc.auth", prod.Cookie.Name);
        Assert.Equal(CookieSecurePolicy.Always, prod.Cookie.SecurePolicy);
        Assert.NotNull(prod.Events.OnValidatePrincipal); // security stamp doğrulaması korunuyor (olaylar ezilmedi)
    }

    [Theory]
    [InlineData(new string[0], false, false)]
    [InlineData(new[] { "Customer" }, false, false)]
    [InlineData(new[] { "Warehouse" }, false, true)]
    [InlineData(new[] { "Admin" }, true, true)]
    [InlineData(new[] { "Customer", "Warehouse" }, false, true)]
    public async Task Policies_AdminOnly_and_WarehouseOrAdmin_evaluate_roles(string[] roles, bool adminOnly, bool warehouseOrAdmin)
    {
        var authorization = fixture.ProductionFactory.Services.GetRequiredService<IAuthorizationService>();
        var identity = new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)).Append(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())), "test");
        var user = new ClaimsPrincipal(identity);

        Assert.Equal(adminOnly, (await authorization.AuthorizeAsync(user, null, AuthPolicies.AdminOnly)).Succeeded);
        Assert.Equal(warehouseOrAdmin, (await authorization.AuthorizeAsync(user, null, AuthPolicies.WarehouseOrAdmin)).Succeeded);
    }

    [Fact]
    public async Task Policies_reject_an_anonymous_principal()
    {
        var authorization = fixture.ProductionFactory.Services.GetRequiredService<IAuthorizationService>();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False((await authorization.AuthorizeAsync(anonymous, null, AuthPolicies.AdminOnly)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(anonymous, null, AuthPolicies.WarehouseOrAdmin)).Succeeded);
        // Rol claim'i var ama kimlik doğrulanmamış (AuthenticationType yok) => reddedilir.
        var fake = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")]));
        Assert.False((await authorization.AuthorizeAsync(fake, null, AuthPolicies.AdminOnly)).Succeeded);
    }

    // ---------- Cookie olayları: endpoint metadata'sı OLMADAN da yönlendirme yok ----------

    [Fact]
    public async Task RedirectToLogin_event_writes_401_problem_json_and_never_a_Location_header()
    {
        var (http, body) = NewHttpContext();
        var context = NewRedirectContext(http, "/Account/Login?ReturnUrl=%2Fadmin");

        await CookieEvents.RedirectToLogin(context);

        Assert.Equal(401, http.Response.StatusCode);
        Assert.False(http.Response.Headers.ContainsKey("Location"));
        using var json = ReadBody(body);
        Assert.Equal("unauthenticated", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(401, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("application/problem+json", http.Response.ContentType?.Split(';')[0]);
    }

    [Fact]
    public async Task RedirectToAccessDenied_event_writes_403_problem_json_and_never_a_Location_header()
    {
        var (http, body) = NewHttpContext();
        var context = NewRedirectContext(http, "/Account/AccessDenied");

        await CookieEvents.RedirectToAccessDenied(context);

        Assert.Equal(403, http.Response.StatusCode);
        Assert.False(http.Response.Headers.ContainsKey("Location"));
        using var json = ReadBody(body);
        Assert.Equal("forbidden", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Both_events_are_wired_into_the_real_cookie_options()
    {
        var options = fixture.ProductionFactory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var (http, _) = NewHttpContext();

        await options.Events.OnRedirectToLogin(NewRedirectContext(http, "/x"));
        Assert.Equal(401, http.Response.StatusCode);

        var (http2, _) = NewHttpContext();
        await options.Events.OnRedirectToAccessDenied(NewRedirectContext(http2, "/x"));
        Assert.Equal(403, http2.Response.StatusCode);
    }

    private static (DefaultHttpContext Http, MemoryStream Body) NewHttpContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails(o => o.CustomizeProblemDetails = SSCommerce.Api.Errors.ApiProblemDetails.Customize);
        var body = new MemoryStream();
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Response.Body = body;
        return (http, body);
    }

    private static RedirectContext<CookieAuthenticationOptions> NewRedirectContext(HttpContext http, string redirectUri) =>
        new(http, new AuthenticationScheme("test", null, typeof(CookieAuthenticationHandler)), new CookieAuthenticationOptions(), new AuthenticationProperties(), redirectUri);

    private static JsonDocument ReadBody(MemoryStream body)
    {
        body.Position = 0;
        return JsonDocument.Parse(body);
    }

    // ---------- OpenAPI ----------

    [Fact]
    public async Task OpenApi_lists_the_auth_and_admin_endpoints_with_their_methods_and_request_body()
    {
        var response = await fixture.Dev().GetAsync("/openapi/v1.json");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var paths = json.RootElement.GetProperty("paths");

        Assert.Equal(["get"], Methods(paths, "/api/v1/auth/csrf"));
        Assert.Equal(["post"], Methods(paths, "/api/v1/auth/register"));
        Assert.Equal(["post"], Methods(paths, "/api/v1/auth/login"));
        Assert.Equal(["post"], Methods(paths, "/api/v1/auth/logout"));
        Assert.Equal(["get"], Methods(paths, "/api/v1/auth/me"));
        Assert.Equal(["get"], Methods(paths, "/api/v1/admin/me"));

        var register = paths.GetProperty("/api/v1/auth/register").GetProperty("post");
        Assert.True(register.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _));
        Assert.True(register.GetProperty("responses").TryGetProperty("201", out _));
        Assert.True(register.GetProperty("responses").TryGetProperty("409", out _));
        Assert.True(paths.GetProperty("/api/v1/auth/login").GetProperty("post").GetProperty("responses").TryGetProperty("401", out _));
        Assert.True(paths.GetProperty("/api/v1/admin/me").GetProperty("get").GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(paths.GetProperty("/api/v1/auth/logout").GetProperty("post").GetProperty("responses").TryGetProperty("204", out _));

        var schemas = json.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal(["capabilities", "email", "emailConfirmed", "id", "roles"], Props(schemas, "AccountDto"));
        Assert.Equal(["email", "password"], Props(schemas, "CredentialsRequest")); // rol alanı sözleşmede YOK
        Assert.Equal(["token"], Props(schemas, "CsrfTokenDto"));
    }

    private static string[] Methods(JsonElement paths, string path) =>
        paths.GetProperty(path).EnumerateObject().Select(p => p.Name).ToArray();

    private static string[] Props(JsonElement schemas, string name) =>
        schemas.GetProperty(name).GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray();
}

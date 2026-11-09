using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;

namespace SSCommerce.Api.Security;

/// <summary>
/// Cookie bayrakları ortama göre (D-033). Saf fonksiyon: hangi ortam hangi bayrağı verir, testle doğrulanır.
/// <list type="bullet">
/// <item>Development: HTTP localhost çalışır. Ad <c>ssc.auth</c>, <c>Secure</c> yalnız istek HTTPS ise (SameAsRequest).</item>
/// <item>Diğer her ortam (Production, Staging...): ad <c>__Host-ssc.auth</c> (tarayıcı Secure + Path=/ + Domain'siz olmasını zorunlu kılar), Secure HER ZAMAN.</item>
/// </list>
/// Ortak: HttpOnly, SameSite=Lax, Path=/, kayan 8 saat, kalıcı değil (tarayıcı kapanınca biter).
/// </summary>
public static class AuthCookieSettings
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    public const string DevelopmentAuthCookieName = "ssc.auth";
    public const string SecureAuthCookieName = "__Host-ssc.auth";
    public const string DevelopmentCsrfCookieName = "ssc.csrf";
    public const string SecureCsrfCookieName = "__Host-ssc.csrf";
    public const string CsrfHeaderName = "X-CSRF-TOKEN";

    public static bool RequiresSecureCookies(IHostEnvironment environment) => !environment.IsDevelopment();

    public static string AuthCookieName(IHostEnvironment environment) =>
        RequiresSecureCookies(environment) ? SecureAuthCookieName : DevelopmentAuthCookieName;

    public static void Apply(CookieAuthenticationOptions options, IHostEnvironment environment)
    {
        var secure = RequiresSecureCookies(environment);
        options.Cookie.Name = AuthCookieName(environment);
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        options.Cookie.SecurePolicy = secure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        options.Cookie.IsEssential = true;
        options.ExpireTimeSpan = SessionLifetime;
        options.SlidingExpiration = true;
    }

    public static void Apply(AntiforgeryOptions options, IHostEnvironment environment)
    {
        var secure = RequiresSecureCookies(environment);
        options.HeaderName = CsrfHeaderName;
        options.Cookie.Name = secure ? SecureCsrfCookieName : DevelopmentCsrfCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
        options.Cookie.SecurePolicy = secure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        options.Cookie.IsEssential = true;
    }
}

using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SSCommerce.Api.Errors;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Security;

/// <summary>Kimlik doğrulama, yetkilendirme, antiforgery ve rate limit kaydı (F4.2, D-033..D-035).</summary>
public static class ApiSecurityExtensions
{
    public static IServiceCollection AddApiSecurity(this IServiceCollection services)
    {
        services.AddOptions<SecurityOptions>().BindConfiguration(SecurityOptions.SectionName);
        services.AddKeyPersistence();

        // --- Cookie oturumu: Identity'nin uygulama cookie'si ---
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<IHostEnvironment>((options, environment) =>
            {
                AuthCookieSettings.Apply(options, environment);
                // Olay nesnesi AddIdentityCookies'in OnValidatePrincipal (security stamp) kaydını taşır: ezme, yalnız yönlendirmeleri değiştir.
                options.Events.OnRedirectToLogin = CookieEvents.RedirectToLogin;
                options.Events.OnRedirectToAccessDenied = CookieEvents.RedirectToAccessDenied;
            });

        // Logout'ta stamp yenilenir (UserManager.UpdateSecurityStampAsync); aralık 0 olduğundan eski cookie bir sonraki istekte hemen reddedilir (D-033).
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);

        // --- Politikalar ---
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.AdminOnly, policy => policy.RequireAuthenticatedUser().RequireRole(AppRoles.Admin))
            .AddPolicy(AuthPolicies.WarehouseOrAdmin, policy => policy.RequireAuthenticatedUser().RequireRole(AppRoles.Warehouse, AppRoles.Admin));

        // --- Antiforgery (CSRF) ---
        services.AddAntiforgery();
        services.AddOptions<AntiforgeryOptions>().Configure<IHostEnvironment>(AuthCookieSettings.Apply);

        // --- Rate limit: IP başına sabit pencere ---
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimitPolicies.Credentials, context =>
                PartitionByIp(context, settings => settings.CredentialsPermitLimit));
            options.AddPolicy(RateLimitPolicies.General, context =>
                PartitionByIp(context, settings => settings.GeneralPermitLimit));
            options.AddPolicy(RateLimitPolicies.Mutation, context =>
                PartitionByIp(context, settings => settings.MutationPermitLimit));
            options.OnRejected = async (context, _) =>
            {
                var http = context.HttpContext;
                var window = http.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.RateLimit.WindowSeconds;
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var span)
                    ? (int)Math.Ceiling(span.TotalSeconds)
                    : window;
                http.Response.Headers.RetryAfter = Math.Max(1, retryAfter).ToString(CultureInfo.InvariantCulture);
                await CookieEvents.WriteAsync(
                    http,
                    StatusCodes.Status429TooManyRequests,
                    "Çok fazla istek. Lütfen biraz sonra tekrar deneyin",
                    ApiErrorCodes.RateLimited);
            };
        });

        // --- Proxy arkasında gerçek istemci IP'si (rate limit için) ---
        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<SecurityOptions>>((options, security) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            foreach (var proxy in security.Value.TrustedProxies)
            {
                if (IPAddress.TryParse(proxy, out var address))
                {
                    options.KnownProxies.Add(address);
                }
            }
        });

        return services;
    }

    /// <summary>Pipeline sırası önemlidir: forwarded → (hata modeli Program.cs'te) → no-store → rate limit → authentication → authorization.</summary>
    public static WebApplication UseApiSecurityBeforeErrors(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseMiddleware<AssumeHttpsMiddleware>();
        return app;
    }

    public static WebApplication UseApiSecurity(this WebApplication app)
    {
        app.UseMiddleware<NoStoreMiddleware>();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    /// <summary>
    /// <c>/api/v1</c> grubu. Origin + CSRF filtreleri GRUP düzeyindedir: bu gruba eklenen her POST/PUT/PATCH/DELETE ucu
    /// (Faz 6-8 dahil) otomatik korunur; GET/HEAD/OPTIONS/TRACE'e dokunulmaz.
    /// </summary>
    public static RouteGroupBuilder MapApiV1(this IEndpointRouteBuilder app) =>
        app.MapGroup("/api/v1")
            .AddEndpointFilter<OriginCheckFilter>()
            .AddEndpointFilter<CsrfProtectionFilter>()
            // Filtrelerin bu grupta olduğunu uç noktadan okunabilir kılan işaret: mimari test, grup DIŞINA düşen yazma uçlarını yakalar.
            .WithMetadata(RequestProtectionMetadata.Instance);

    private static RateLimitPartition<string> PartitionByIp(HttpContext context, Func<RateLimitSettings, int> permitLimit)
    {
        var settings = context.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.RateLimit;
        var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, permitLimit(settings)),
            Window = TimeSpan.FromSeconds(Math.Max(1, settings.WindowSeconds)),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }
}

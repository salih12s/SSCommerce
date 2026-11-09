using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // Üretim fail-fast (Faz 9, B5): Production/Staging'de eksik/güvensiz yapılandırma açılışta uygulamayı durdurur.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ProductionStartupCheck>, ProductionConfigurationValidator>());
        services.AddOptions<ProductionStartupCheck>().ValidateOnStart();

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

    /// <summary>
    /// Hız sınırı kovası anahtarı (Faz 9, güvenlik denetimi B1). IPv4-mapped IPv6 (`::ffff:a.b.c.d`) IPv4'e indirilir; IPv6 adresleri /64 önekiyle gruplanır:
    /// bir istemci/abone tipik olarak tüm /64 bloğunu elinde tutar ve blok içinde adres döndürerek tam-adres kovasını sınırsız çoğaltabilirdi (parola deneme sınırını aşmak).
    /// </summary>
    public static string RateLimitKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes).ToString() + "/64";
    }

    private static RateLimitPartition<string> PartitionByIp(HttpContext context, Func<RateLimitSettings, int> permitLimit)
    {
        var settings = context.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.RateLimit;
        var key = RateLimitKey(context.Connection.RemoteIpAddress);
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, permitLimit(settings)),
            Window = TimeSpan.FromSeconds(Math.Max(1, settings.WindowSeconds)),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }
}

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using SSCommerce.Api.Errors;

namespace SSCommerce.Api.Security;

/// <summary>
/// Uç noktanın Origin + CSRF filtrelerinin arkasında olduğunu belirtir (<see cref="ApiSecurityExtensions.MapApiV1"/> ekler).
/// Endpoint filtreleri çalışma zamanında okunamadığı için bu işaret mimari testin tek dayanağıdır; filtreleri ve işareti
/// birlikte, tek yerde eklemek zorunludur.
/// </summary>
public sealed class RequestProtectionMetadata
{
    public static RequestProtectionMetadata Instance { get; } = new();

    private RequestProtectionMetadata()
    {
    }
}

internal static class RequestMethods
{
    /// <summary>Durum değiştirmeyen yöntemler (RFC 9110 "safe"). Bunların dışındakiler CSRF/Origin korumasına tabidir.</summary>
    public static bool IsSafe(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) ||
        HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method);
}

/// <summary>
/// Savunma derinliği (D-034): değiştiren istekte <c>Origin</c> başlığı VARSA izinli listede olmalıdır, yoksa 403 <c>origin_not_allowed</c>.
/// Başlık yoksa geçilir (bazı istemciler göndermez) ama <see cref="CsrfProtectionFilter"/> token'ı yine şart koşar; bu filtre CSRF'in yerine geçmez.
/// <c>Origin: null</c> (sandbox/gizlilik) listede olmadığı için reddedilir. CORS açılmaz.
/// </summary>
public sealed class OriginCheckFilter(IOptionsMonitor<SecurityOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (RequestMethods.IsSafe(request) || !request.Headers.TryGetValue("Origin", out var origin) || origin.Count == 0)
        {
            return await next(context);
        }

        var allowed = options.CurrentValue.AllowedOrigins.Select(Normalize);
        if (origin.Count == 1 && allowed.Contains(Normalize(origin[0]!), StringComparer.OrdinalIgnoreCase))
        {
            return await next(context);
        }

        return ApiResults.Problem(
            StatusCodes.Status403Forbidden,
            "İstek kaynağına (Origin) izin verilmiyor",
            ApiErrorCodes.OriginNotAllowed);
    }

    private static string Normalize(string origin) => origin.Trim().TrimEnd('/');
}

/// <summary>
/// CSRF koruması (D-034): değiştiren her istekte <see cref="IAntiforgery.ValidateRequestAsync"/>. Token <c>X-CSRF-TOKEN</c>
/// başlığında gelir, eşi antiforgery cookie'sindedir. Token oturumdaki kullanıcıya bağlıdır: login/logout sonrası istemci
/// <c>GET /auth/csrf</c> ile yeniden almalıdır. Hata: 400 <c>csrf_invalid</c> (403 yetki hatası için ayrılmıştır).
/// Grup düzeyinde eklenir: ileride gelen tüm POST/PUT/PATCH/DELETE uçları otomatik korunur.
/// </summary>
public sealed class CsrfProtectionFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (RequestMethods.IsSafe(context.HttpContext.Request))
        {
            return await next(context);
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return ApiResults.Problem(
                StatusCodes.Status400BadRequest,
                "CSRF doğrulaması başarısız",
                ApiErrorCodes.CsrfInvalid);
        }

        return await next(context);
    }
}

/// <summary>Bkz. <see cref="SecurityOptions.AssumeHttps"/>: proxy arkasında düz HTTP dinleyen API'de istek şemasını https yapar.</summary>
public sealed class AssumeHttpsMiddleware(RequestDelegate next, IOptions<SecurityOptions> options, IHostEnvironment environment)
{
    private readonly bool _enabled = options.Value.AssumeHttps ?? !environment.IsDevelopment();

    public Task InvokeAsync(HttpContext context)
    {
        if (_enabled)
        {
            context.Request.Scheme = Uri.UriSchemeHttps;
        }

        return next(context);
    }
}

/// <summary>
/// Kimlik/oturum ve kullanıcıya özel uçların (<c>/api/v1/auth</c>, <c>/admin</c>, <c>/cart</c>, <c>/addresses</c>, <c>/checkout</c>, <c>/orders</c>)
/// yanıtları, 401/403/429 dahil, hiçbir önbellekte tutulmaz. Kişisel veri (sipariş teslimat adresi dahil) paylaşılan önbelleğe ya da
/// başka kullanıcıya sızmasın. Personel sipariş uçları <c>/admin</c> önekinin altındadır.
/// </summary>
public sealed class NoStoreMiddleware(RequestDelegate next)
{
    private static readonly string[] Prefixes =
    [
        "/api/v1/auth",
        "/api/v1/admin",
        "/api/v1/cart",
        "/api/v1/addresses",
        "/api/v1/checkout",
        "/api/v1/orders",
    ];

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (Prefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Task.CompletedTask;
            });
        }

        return next(context);
    }
}

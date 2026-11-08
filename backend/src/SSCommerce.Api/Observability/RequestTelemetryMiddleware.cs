using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;

namespace SSCommerce.Api.Observability;

/// <summary>
/// Güvenlik başlıkları (Faz 9, B1). API her yanıtı (başarılı, 401, 404, 429, 503 ...) bu başlıklarla yollar; API JSON döner, hiçbir zaman
/// sayfa/kaynak yüklemez, bu yüzden CSP <c>default-src 'none'</c>'dır. Başlıklar <c>OnStarting</c>'de yazılır: hata işleyicisinin
/// <c>Response.Clear()</c> çağrısı ya da sonradan çalışan middleware (rate limiter, 429) onları silemez. <c>Cache-Control</c> bu sınıfın
/// işi DEĞİLDİR (<c>NoStoreMiddleware</c>); çakışma yoktur. Kestrel <c>Server</c> başlığı Program.cs'te <c>AddServerHeader=false</c> ile kapatılır.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var headers = ((HttpContext)state).Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Frame-Options"] = "DENY";
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            return Task.CompletedTask;
        }, context);

        return next(context);
    }
}

/// <summary>
/// İstek başına korelasyon, telemetri ve tamamlanma günlüğü (Faz 9, B2/B3). EN DIŞTAKİ uygulama middleware'idir (hata işleyicisinin dışında):
/// <list type="bullet">
/// <item><b>X-Request-Id</b>: gelen başlık yalnız <c>^[A-Za-z0-9._-]{8,64}$</c> ise (tek değer) kabul edilir; yoksa/uymuyorsa GUID (N) üretilir.
/// Yanıta aynen yazılır ve <see cref="ILogger"/> kapsamına <c>RequestId</c> olarak girer. ProblemDetails <c>traceId</c> alanı DEĞİŞMEZ.</item>
/// <item><b>Metrikler</b>: <c>http_server_requests_total</c> + süre histogramı. <c>route</c> uç noktanın ROUTE ŞABLONUdur (ham yol asla);
/// uç nokta eşleşmediyse <c>unmatched</c>.</item>
/// <item><b>Günlük</b>: tek satır; yalnız yöntem, route şablonu, durum, süre (ms), requestId. Sorgu dizesi, çerez, başlık ve gövde değerleri LOGLANMAZ.
/// <c>/health/*</c> yoklamaları Debug seviyesindedir (gürültü).</item>
/// </list>
/// </summary>
public sealed partial class RequestTelemetryMiddleware(RequestDelegate next, SscMetrics metrics, ILogger<RequestTelemetryMiddleware> logger)
{
    public const string HeaderName = "X-Request-Id";
    public const string Unmatched = "unmatched";

    [GeneratedRegex("^[A-Za-z0-9._-]{8,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidId();

    /// <summary>Gelen başlık geçerliyse kendisi, değilse yeni GUID (N). Başlık birden çok değer taşıyorsa yok sayılır.</summary>
    public static string ResolveRequestId(HttpRequest request)
    {
        if (request.Headers.TryGetValue(HeaderName, out var values) && values.Count == 1 && values[0] is { } candidate && ValidId().IsMatch(candidate))
        {
            return candidate;
        }

        return Guid.NewGuid().ToString("N");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = ResolveRequestId(context.Request);
        context.Response.OnStarting(static state =>
        {
            var (response, id) = ((HttpResponse, string))state;
            response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        }, (context.Response, requestId));

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = requestId });
        var started = Stopwatch.GetTimestamp();
        var failed = false;
        try
        {
            await next(context);
        }
        catch
        {
            // Hata işleyicisini de aşan istisna: yine de sayılır; istemci 500 görür.
            failed = true;
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            var status = failed ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
            var route = RouteTemplate(context);
            var method = SscMetrics.NormalizeMethod(context.Request.Method);
            metrics.RecordRequest(method, route, status, elapsed.TotalSeconds);

            var level = route.StartsWith("/health/", StringComparison.Ordinal) ? LogLevel.Debug : LogLevel.Information;
            if (logger.IsEnabled(level))
            {
                logger.Log(level, "İstek tamamlandı {Method} {Route} {StatusCode} {ElapsedMs}ms {RequestId}", method, route, status, (long)elapsed.TotalMilliseconds, requestId);
            }
        }
    }

    internal static string RouteTemplate(HttpContext context) =>
        context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { Length: > 0 } template } ? template : Unmatched;
}

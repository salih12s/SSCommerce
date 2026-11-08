using Microsoft.Extensions.Options;

namespace SSCommerce.Api.Observability;

/// <summary>
/// <c>Metrics</c> yapılandırma bölümü. <c>/metrics</c> YALNIZ ayrı bir iç dinleyicide sunulur; ana dinleyicide (8080) 404'tür,
/// böylece Next.js <c>/api/backend/*</c> rewrite'ı ile internete sızmaz. Sır içermez.
/// </summary>
public sealed class MetricsOptions
{
    public const string SectionName = "Metrics";

    /// <summary>
    /// İç metrik dinleyicisinin portu. <c>0</c> (varsayılan) = kapalı: <c>/metrics</c> hiçbir yerde sunulmaz.
    /// Ayarlıysa uygulama ana dinleyicilerine EK olarak <c>http://{BindAddress}:{Port}</c> dinler (bkz. <see cref="MetricsListener"/>);
    /// bu portu ASLA internete/kamuya yayımlama (yalnız iç ağ ya da toplayıcı sidecar).
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// İç dinleyicinin bağlanacağı adres. Varsayılan <c>127.0.0.1</c> (yalnız aynı makine). Konteynerde toplayıcı başka bir konteynerdeyse
    /// <c>0.0.0.0</c> verilir; bu durumda portu ana makineye YAYIMLAMAYIN (yalnız iç ağ).
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    public string? Validate() =>
        Port is < 0 or > 65535 ? "Metrics:Port 0 ile 65535 arasında olmalıdır."
        : string.IsNullOrWhiteSpace(BindAddress) ? "Metrics:BindAddress boş olamaz."
        : null;
}

/// <summary>Ana dinleyici adreslerine iç metrik dinleyicisini ekler (saf; birim testlidir).</summary>
public static class MetricsListener
{
    public const string DefaultUrl = "http://localhost:5000";

    /// <summary>
    /// Kestrel, kodda <c>Listen*</c> çağrılırsa <c>urls</c>/<c>HTTP_PORTS</c> yapılandırmasını YOK SAYAR; bu yüzden ana dinleyiciler korunarak
    /// birleştirilir: <paramref name="urls"/> doluysa ona eklenir; yoksa <paramref name="httpPorts"/> (<c>http://*:port</c>) ve
    /// <paramref name="httpsPorts"/> (<c>https://*:port</c>) çevrilir; hiçbiri yoksa Kestrel varsayılanı (<see cref="DefaultUrl"/>) korunur.
    /// </summary>
    public static string Compose(string? urls, string? httpPorts, string? httpsPorts, string metricsUrl)
    {
        var main = new List<string>();
        if (!string.IsNullOrWhiteSpace(urls))
        {
            main.AddRange(Split(urls));
        }
        else
        {
            main.AddRange(Split(httpPorts).Select(port => $"http://*:{port}"));
            main.AddRange(Split(httpsPorts).Select(port => $"https://*:{port}"));
        }

        if (main.Count == 0)
        {
            main.Add(DefaultUrl);
        }

        main.Add(metricsUrl);
        return string.Join(';', main);
    }

    public static string MetricsUrl(MetricsOptions options) => $"http://{options.BindAddress.Trim()}:{options.Port}";

    private static IEnumerable<string> Split(string? value) =>
        (value ?? string.Empty).Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// <c>GET /metrics</c> (Prometheus metni). İsteğin <c>LocalPort</c>'u <see cref="MetricsOptions.Port"/> ise (ve port &gt; 0) bu dinleyici İÇ'tir:
/// yalnız <c>/metrics</c> yanıtlanır, başka her şey 404. Diğer portlarda <c>/metrics</c> uygulamaya DÜŞER ve hiçbir uç noktası olmadığından 404 olur.
/// Port karşılaştırması kasıtlı olarak <c>Connection.LocalPort</c>'tur (Host başlığı sahte olabilir). Yanıt etiketleri düşük kardinaliteli kümeden gelir,
/// kişisel veri içermez.
/// </summary>
public sealed class MetricsEndpointMiddleware(RequestDelegate next, IOptions<MetricsOptions> options, PrometheusCollector collector)
{
    private readonly int _port = options.Value.Port;

    public async Task InvokeAsync(HttpContext context)
    {
        if (_port <= 0 || context.Connection.LocalPort != _port)
        {
            await next(context);
            return;
        }

        if (!context.Request.Path.Equals("/metrics", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET, HEAD";
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await context.Response.WriteAsync(collector.Render(), context.RequestAborted);
        }
    }
}

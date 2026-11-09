using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace SSCommerce.Api.Observability;

/// <summary>Faz 9 işletim kaydı: güvenlik başlıkları, korelasyon, telemetri ve (iç dinleyicide) Prometheus sunumu.</summary>
public static class ObservabilityExtensions
{
    public static IServiceCollection AddObservability(this IServiceCollection services)
    {
        services.AddOptions<MetricsOptions>()
            .BindConfiguration(MetricsOptions.SectionName)
            .Validate(options => options.Validate() is null, "Metrics:Port 0 ile 65535 arasında, Metrics:BindAddress dolu olmalıdır.")
            .ValidateOnStart();
        services.TryAddSingleton<SscMetrics>();
        services.TryAddSingleton<PrometheusCollector>();
        return services;
    }

    /// <summary>
    /// EN DIŞ katman (hata işleyicisinden önce): güvenlik başlıkları → (iç dinleyicide) /metrics → korelasyon + telemetri + tamamlanma günlüğü.
    /// Toplayıcı burada, ilk istekten ÖNCE yaratılır (dinleyiciden önceki ölçümler kaybolmasın).
    /// </summary>
    public static WebApplication UseObservability(this WebApplication app)
    {
        _ = app.Services.GetRequiredService<PrometheusCollector>();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<MetricsEndpointMiddleware>();
        app.UseMiddleware<RequestTelemetryMiddleware>();
        return app;
    }

    /// <summary>
    /// <c>Metrics:Port</c> &gt; 0 ise iç metrik dinleyicisini ana adreslere EKLER; Kestrel <c>Server</c> başlığını her durumda kapatır.
    /// Port yapılandırması hatalıysa (aralık dışı) burada dokunulmaz: <see cref="AddObservability"/> doğrulaması açılışta uygulamayı durdurur.
    /// </summary>
    public static void ConfigureHosting(this WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

        var metrics = builder.Configuration.GetSection(MetricsOptions.SectionName).Get<MetricsOptions>() ?? new MetricsOptions();
        if (metrics.Validate() is not null || metrics.Port == 0)
        {
            return;
        }

        var urls = MetricsListener.Compose(
            builder.Configuration["urls"],
            builder.Configuration["HTTP_PORTS"],
            builder.Configuration["HTTPS_PORTS"],
            MetricsListener.MetricsUrl(metrics));
        builder.WebHost.UseUrls(urls);
    }
}

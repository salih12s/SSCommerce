using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SSCommerce.Api.Observability;
using SSCommerce.Application.Orders;
using static SSCommerce.Api.Tests.Observability.ObservabilityKit;

namespace SSCommerce.Api.Tests.Observability;

/// <summary>
/// Faz 9 B3: Prometheus telemetrisi. Kavramlar: "kardinalite" (bir etiketin alabileceği farklı değer sayısı; kullanıcı kimliği/ham yol etiket olursa
/// zaman serisi sayısı patlar ve hem bellek hem maliyet büyür) ve "scrape" (toplayıcının /metrics'i periyodik okuması).
/// TestServer gerçek soket açmadığı için "iç dinleyici" <c>Connection.LocalPort</c> taklidiyle (bkz. <see cref="ObservabilityKit.AddLocalPortSimulation"/>) sınanır;
/// gerçek ikinci Kestrel portu bu turda test EDİLMEDİ (adres birleştirme saf fonksiyon olarak ayrıca sınandı).
/// </summary>
public class MetricsTests
{
    private const string Unreachable = "Host=127.0.0.1;Port=1;Timeout=2;Database=x;Username=x;Password=x";

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory(int metricsPort = MetricsPort, string? connection = null) =>
        ApiFactory.Create(
            "Production",
            connection,
            AddLocalPortSimulation,
            new Dictionary<string, string?> { ["Metrics:Port"] = metricsPort.ToString() });

    [Fact]
    public async Task The_main_listener_answers_404_for_metrics_even_when_the_internal_listener_is_enabled()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var noPort = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);
        var otherPort = await client.SendAsync(MetricsRequest(port: 8080), TestContext.Current.CancellationToken);
        var rewritten = await client.SendAsync(MetricsRequest(port: 8080, path: "/api/backend/metrics"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, noPort.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherPort.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, rewritten.StatusCode);
        Assert.DoesNotContain("http_server_requests_total", await otherPort.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_internal_port_serves_prometheus_text_and_nothing_else()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var metrics = await client.SendAsync(MetricsRequest(), TestContext.Current.CancellationToken);
        var other = await client.SendAsync(MetricsRequest(path: "/health/live"), TestContext.Current.CancellationToken);
        using var post = MetricsRequest();
        post.Method = HttpMethod.Post;
        var posted = await client.SendAsync(post, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        Assert.Equal("text/plain; version=0.0.4; charset=utf-8", metrics.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-store", metrics.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode); // iç dinleyici uygulamayı sunmaz
        Assert.Equal(HttpStatusCode.MethodNotAllowed, posted.StatusCode);
    }

    [Fact]
    public async Task With_Metrics_Port_unset_metrics_are_not_served_anywhere()
    {
        await using var factory = ApiFactory.Create("Production", null, AddLocalPortSimulation); // Metrics:Port varsayılanı 0 = kapalı
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(MetricsRequest(port: 0), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(MetricsRequest(), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/metrics", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Real_requests_increase_the_counter_and_histogram_with_route_templates_never_raw_paths()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var before = await ScrapeAsync(client);
        const string live = "route=\"/health/live\"";
        const string me = "route=\"/api/v1/auth/me\"";
        var garbage = "/api/v1/yok-" + Guid.NewGuid().ToString("N");
        var orderId = Guid.NewGuid();

        await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        await client.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken);
        await client.GetAsync(garbage, TestContext.Current.CancellationToken);
        await client.GetAsync($"/api/v1/orders/{orderId}", TestContext.Current.CancellationToken);
        var after = await ScrapeAsync(client);

        double Delta(string name, params string[] labels) => Value(after, name, labels) - Value(before, name, labels);
        Assert.Equal(2, Delta("http_server_requests_total", "method=\"GET\"", live, "status_class=\"2xx\""));
        Assert.Equal(1, Delta("http_server_requests_total", "method=\"GET\"", me, "status_class=\"4xx\""));
        Assert.Equal(1, Delta("http_server_requests_total", "route=\"unmatched\"", "status_class=\"4xx\""));
        Assert.Equal(1, Delta("http_server_requests_total", "route=\"/api/v1/orders/{id}\"", "status_class=\"4xx\""));
        Assert.Equal(2, Delta("http_server_request_duration_seconds_count", live));
        Assert.True(Value(after, "http_server_request_duration_seconds_sum", live) > 0);
        Assert.Equal(Value(after, "http_server_request_duration_seconds_count", live), Value(after, "http_server_request_duration_seconds_bucket", live, "le=\"+Inf\""));
        Assert.Contains("# TYPE http_server_request_duration_seconds histogram", after);
        Assert.DoesNotContain(garbage, after);
        Assert.DoesNotContain(orderId.ToString(), after);
        Assert.DoesNotContain("/metrics", after); // /metrics isteklerinin kendisi sayılmaz
    }

    [Fact]
    public async Task Metric_labels_carry_no_personal_data_and_only_the_allowed_label_names()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/products?search=ayse%40example.test&page=2");
        request.Headers.Add("X-Forwarded-For", "203.0.113.77");
        request.Headers.Add("X-Request-Id", "gizli-istek-kimligi-777");
        request.Headers.Add("Cookie", "ssc.auth=COOKIE-DEGERI-GIZLI");
        request.Headers.Add("Authorization", "Bearer TOKEN-GIZLI");
        await client.SendAsync(request, TestContext.Current.CancellationToken);

        var text = await ScrapeAsync(client);

        foreach (var secret in new[] { "ayse", "example.test", "203.0.113.77", "gizli-istek-kimligi", "COOKIE-DEGERI", "TOKEN-GIZLI", "search", "page=2" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Subset(new HashSet<string> { "method", "route", "status_class", "le", "outcome", "to" }, LabelNames(text));
    }

    [Fact]
    public async Task Database_unavailable_503s_are_counted_and_readiness_state_drops_to_zero()
    {
        await using var factory = Factory(connection: Unreachable);
        using var client = factory.CreateClient();

        await client.GetAsync("/api/v1/categories", TestContext.Current.CancellationToken);
        await client.GetAsync("/api/v1/products", TestContext.Current.CancellationToken);
        var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var text = await ScrapeAsync(client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(2, Value(text, "ssc_db_unavailable_total"));
        Assert.Equal(0, Value(text, "ssc_ready_state"));
        Assert.Contains("# TYPE ssc_ready_state gauge", text);
        Assert.Equal(2, Value(text, "http_server_requests_total", "route=\"/api/v1/categories\"", "status_class=\"5xx\"") + Value(text, "http_server_requests_total", "route=\"/api/v1/products\"", "status_class=\"5xx\""));
    }

    // ---------- Saf birimler ----------

    private static (SscMetrics Metrics, PrometheusCollector Collector) NewMetrics()
    {
        var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        var collector = new PrometheusCollector(factory); // önce dinleyici, sonra Meter (üretimdeki sıra: UseObservability)
        return (new SscMetrics(factory), collector);
    }

    [Fact]
    public void Readiness_transitions_are_counted_only_when_a_known_state_changes()
    {
        var (metrics, collector) = NewMetrics();

        metrics.RecordReadiness(false); // ilk değerlendirme: geçiş değil
        metrics.RecordReadiness(false);
        var firstState = collector.Render();
        metrics.RecordReadiness(true);  // hazır değil -> hazır
        metrics.RecordReadiness(true);
        metrics.RecordReadiness(false); // hazır -> hazır değil
        var text = collector.Render();

        Assert.Equal(0, Value(firstState, "ssc_ready_transitions_total"));
        Assert.Equal(0, Value(firstState, "ssc_ready_state"));
        Assert.Equal(1, Value(text, "ssc_ready_transitions_total", "to=\"ready\""));
        Assert.Equal(1, Value(text, "ssc_ready_transitions_total", "to=\"not_ready\""));
        Assert.Equal(0, Value(text, "ssc_ready_state"));
        metrics.RecordReadiness(true);
        Assert.Equal(1, Value(collector.Render(), "ssc_ready_state"));
    }

    [Fact]
    public void Order_outcomes_map_every_service_status_to_the_closed_label_set()
    {
        var allowed = new HashSet<string>
        {
            "placed", "replayed", "open_order_limit", "order_daily_limit", "stock_conflict", "busy", "ordering_disabled", "other_error",
        };
        var expected = new Dictionary<OrderOperationStatus, string>
        {
            [OrderOperationStatus.Placed] = "placed",
            [OrderOperationStatus.Replayed] = "replayed",
            [OrderOperationStatus.OpenOrderLimit] = "open_order_limit",
            [OrderOperationStatus.DailyOrderLimit] = "order_daily_limit",
            [OrderOperationStatus.InsufficientStock] = "stock_conflict",
            [OrderOperationStatus.Busy] = "busy",
        };

        foreach (var status in Enum.GetValues<OrderOperationStatus>())
        {
            var outcome = OrderOutcomes.From(new OrderOperationResult(status));
            Assert.Contains(outcome, allowed);
            Assert.Equal(expected.GetValueOrDefault(status, "other_error"), outcome);
        }
    }

    [Theory]
    [InlineData(200, "2xx")]
    [InlineData(404, "4xx")]
    [InlineData(503, "5xx")]
    [InlineData(99, "other")]
    [InlineData(700, "other")]
    public void Status_codes_are_reduced_to_classes(int status, string expected) => Assert.Equal(expected, SscMetrics.StatusClass(status));

    [Theory]
    [InlineData("GET", "GET")]
    [InlineData("POST", "POST")]
    [InlineData("PROPFIND", "OTHER")]
    [InlineData("get", "OTHER")]
    public void Unknown_http_methods_collapse_into_OTHER(string method, string expected) => Assert.Equal(expected, SscMetrics.NormalizeMethod(method));

    [Theory]
    [InlineData("http://localhost:5080", null, null, "http://127.0.0.1:9100", "http://localhost:5080;http://127.0.0.1:9100")]
    [InlineData("http://+:8080;http://+:8081", null, null, "http://127.0.0.1:9100", "http://+:8080;http://+:8081;http://127.0.0.1:9100")]
    [InlineData(null, "8080", null, "http://127.0.0.1:9100", "http://*:8080;http://127.0.0.1:9100")]
    [InlineData(null, "8080;8081", "8443", "http://0.0.0.0:9100", "http://*:8080;http://*:8081;https://*:8443;http://0.0.0.0:9100")]
    [InlineData(null, null, null, "http://127.0.0.1:9100", "http://localhost:5000;http://127.0.0.1:9100")]
    [InlineData("  ", " ", null, "http://127.0.0.1:9100", "http://localhost:5000;http://127.0.0.1:9100")]
    public void The_internal_listener_is_added_to_the_main_addresses_never_replacing_them(string? urls, string? http, string? https, string metrics, string expected) =>
        Assert.Equal(expected, MetricsListener.Compose(urls, http, https, metrics));

    [Theory]
    [InlineData(0, "127.0.0.1", true)]
    [InlineData(9100, "127.0.0.1", true)]
    [InlineData(65535, "0.0.0.0", true)]
    [InlineData(-1, "127.0.0.1", false)]
    [InlineData(65536, "127.0.0.1", false)]
    [InlineData(9100, " ", false)]
    public void Metrics_options_validation(int port, string bind, bool valid) =>
        Assert.Equal(valid, new MetricsOptions { Port = port, BindAddress = bind }.Validate() is null);
}

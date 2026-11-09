using System.Diagnostics;
using System.Diagnostics.Metrics;
using SSCommerce.Application.Orders;

namespace SSCommerce.Api.Observability;

/// <summary>
/// Uygulama telemetrisi (Faz 9). <c>System.Diagnostics.Metrics</c> ile, "SSCommerce" adlı tek <see cref="Meter"/> üzerinde; yeni bağımlılık yoktur.
/// Meter, ana makinenin <see cref="IMeterFactory"/>'sinden üretilir: ölçümler o ana makineye (host) kapsamlıdır, testler birbirini görmez.
/// DÜŞÜK KARDİNALİTE kuralı: etiket değerleri yalnız sabit/küçük kümelerdendir (yöntem, route ŞABLONU, durum sınıfı, sipariş sonucu).
/// Kullanıcı kimliği, e-posta, IP, ürün/sipariş kimliği, ham yol, sorgu dizesi ya da SQL ASLA etiket olmaz.
/// Sunum: <see cref="PrometheusCollector"/> + <see cref="MetricsEndpointMiddleware"/> (yalnız iç dinleyici).
/// </summary>
public sealed class SscMetrics
{
    public const string MeterName = "SSCommerce";

    public const string RequestsName = "http_server_requests_total";
    public const string DurationName = "http_server_request_duration_seconds";
    public const string OrdersName = "ssc_orders_total";
    public const string DbUnavailableName = "ssc_db_unavailable_total";
    public const string ReadyStateName = "ssc_ready_state";
    public const string ReadyTransitionsName = "ssc_ready_transitions_total";

    /// <summary>Süre histogramının üst sınırları (saniye). Prometheus sunumu da bu kümeyi kullanır.</summary>
    public static readonly double[] DurationBuckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    private static readonly HashSet<string> KnownMethods = new(StringComparer.Ordinal) { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

    private readonly Counter<long> _requests;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _orders;
    private readonly Counter<long> _dbUnavailable;
    private readonly Counter<long> _readyTransitions;
    private int _ready = -1; // -1: henüz değerlendirilmedi, 0: hazır değil, 1: hazır

    public SscMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(new MeterOptions(MeterName));
        _requests = meter.CreateCounter<long>(RequestsName, description: "Tamamlanan HTTP istekleri.");
        _duration = meter.CreateHistogram<double>(DurationName, unit: "s", description: "HTTP istek süresi (saniye).");
        _orders = meter.CreateCounter<long>(OrdersName, description: "Sipariş verme (POST /orders) sonuçları.");
        _dbUnavailable = meter.CreateCounter<long>(DbUnavailableName, description: "Veritabanına ulaşılamadığı için dönülen 503 sayısı.");
        _readyTransitions = meter.CreateCounter<long>(ReadyTransitionsName, description: "Hazırlık durumu geçişleri (to=ready|not_ready).");
        meter.CreateObservableGauge(ReadyStateName, () => Math.Max(0, Volatile.Read(ref _ready)), description: "Son hazırlık denetimi: 1 hazır, 0 hazır değil (ya da henüz denetlenmedi).");
    }

    public void RecordRequest(string method, string route, int statusCode, double seconds)
    {
        var tags = new TagList
        {
            { "method", NormalizeMethod(method) },
            { "route", route },
            { "status_class", StatusClass(statusCode) },
        };
        _requests.Add(1, tags);
        _duration.Record(seconds, tags);
    }

    public void RecordOrder(string outcome) => _orders.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void RecordDatabaseUnavailable() => _dbUnavailable.Add(1);

    /// <summary>Readiness sonucu. Yalnız ÖNCEKİ bilinen durumdan farklıysa geçiş sayılır (ilk değerlendirme geçiş değildir).</summary>
    public void RecordReadiness(bool ready)
    {
        var next = ready ? 1 : 0;
        var previous = Interlocked.Exchange(ref _ready, next);
        if (previous != -1 && previous != next)
        {
            _readyTransitions.Add(1, new KeyValuePair<string, object?>("to", ready ? "ready" : "not_ready"));
        }
    }

    public static string NormalizeMethod(string method) => KnownMethods.Contains(method) ? method : "OTHER";

    public static string StatusClass(int status) => status is >= 100 and < 600 ? $"{status / 100}xx" : "other";
}

/// <summary>Sipariş verme sonuç etiketi kümesi (kapalı küme: kardinalite sabit).</summary>
public static class OrderOutcomes
{
    public const string Placed = "placed";
    public const string Replayed = "replayed";
    public const string OpenOrderLimit = "open_order_limit";
    public const string OrderDailyLimit = "order_daily_limit";
    public const string StockConflict = "stock_conflict";
    public const string Busy = "busy";
    public const string OrderingDisabled = "ordering_disabled";
    public const string OtherError = "other_error";

    /// <summary>Servis sonucundan etiket. Servis yeniden yazılmaz; yalnız çıkışta sınıflanır.</summary>
    public static string From(OrderOperationResult result) => result.Status switch
    {
        OrderOperationStatus.Placed => Placed,
        OrderOperationStatus.Replayed => Replayed,
        OrderOperationStatus.OpenOrderLimit => OpenOrderLimit,
        OrderOperationStatus.DailyOrderLimit => OrderDailyLimit,
        OrderOperationStatus.InsufficientStock => StockConflict,
        OrderOperationStatus.Busy => Busy,
        _ => OtherError,
    };
}

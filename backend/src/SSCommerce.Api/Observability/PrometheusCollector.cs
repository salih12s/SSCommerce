using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;

namespace SSCommerce.Api.Observability;

/// <summary>
/// "SSCommerce" Meter'ının ölçümlerini <see cref="MeterListener"/> ile toplayıp Prometheus metin biçiminde (0.0.4) sunar; dış paket yok.
/// Yalnız BU ana makinenin <see cref="IMeterFactory"/>'si ile üretilmiş Meter'lara abone olur (testler arası sızıntı olmaz).
/// Uygulama açılırken yaratılmalıdır: dinleyiciden önceki ölçümler görülmez. Etiketler kaynakta (<see cref="SscMetrics"/>) sınırlıdır;
/// buraya yalnız o etiketler gelir.
/// </summary>
public sealed class PrometheusCollector : IDisposable
{
    private enum Kind { Counter, Histogram, Gauge }

    private sealed record Info(Kind Kind, string Description);

    private sealed class Series
    {
        public readonly object Gate = new();
        public double Value;
        public double Sum;
        public long Count;
        public long[] Buckets = new long[SscMetrics.DurationBuckets.Length];
    }

    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, Info> _instruments = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Name, string Labels), Series> _series = new();

    public PrometheusCollector(IMeterFactory meterFactory)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name != SscMetrics.MeterName || !ReferenceEquals(instrument.Meter.Scope, meterFactory))
            {
                return;
            }

            Kind? kind = instrument switch
            {
                Counter<long> => Kind.Counter,
                Histogram<double> => Kind.Histogram,
                ObservableGauge<int> => Kind.Gauge,
                _ => null,
            };
            if (kind is null)
            {
                return;
            }

            _instruments[instrument.Name] = new Info(kind.Value, instrument.Description ?? string.Empty);
            listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            With(instrument.Name, tags, series => series.Value += value));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
            With(instrument.Name, tags, series => series.Value = value));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            With(instrument.Name, tags, series =>
            {
                series.Sum += value;
                series.Count++;
                var bounds = SscMetrics.DurationBuckets;
                for (var i = 0; i < bounds.Length; i++)
                {
                    if (value <= bounds[i])
                    {
                        series.Buckets[i]++;
                    }
                }
            }));
        _listener.Start();
    }

    private void With(string name, ReadOnlySpan<KeyValuePair<string, object?>> tags, Action<Series> update)
    {
        var series = _series.GetOrAdd((name, LabelString(tags)), _ => new Series());
        lock (series.Gate)
        {
            update(series);
        }
    }

    /// <summary>Gözlemlenen göstergeleri (gauge) günceller ve tüm seriyi Prometheus metin biçiminde döner.</summary>
    public string Render()
    {
        _listener.RecordObservableInstruments();
        var text = new StringBuilder();
        foreach (var (name, info) in _instruments.OrderBy(i => i.Key, StringComparer.Ordinal))
        {
            text.Append("# HELP ").Append(name).Append(' ').Append(EscapeHelp(info.Description)).Append('\n');
            text.Append("# TYPE ").Append(name).Append(' ').Append(info.Kind switch
            {
                Kind.Counter => "counter",
                Kind.Histogram => "histogram",
                _ => "gauge",
            }).Append('\n');

            foreach (var entry in _series.Where(s => s.Key.Name == name).OrderBy(s => s.Key.Labels, StringComparer.Ordinal))
            {
                var labels = entry.Key.Labels;
                var series = entry.Value;
                lock (series.Gate)
                {
                    if (info.Kind == Kind.Histogram)
                    {
                        RenderHistogram(text, name, labels, series);
                    }
                    else
                    {
                        text.Append(name).Append(Braces(labels)).Append(' ').Append(Number(series.Value)).Append('\n');
                    }
                }
            }
        }

        return text.ToString();
    }

    private static void RenderHistogram(StringBuilder text, string name, string labels, Series series)
    {
        var bounds = SscMetrics.DurationBuckets;
        for (var i = 0; i < bounds.Length; i++)
        {
            text.Append(name).Append("_bucket").Append(Braces(Join(labels, $"le=\"{Number(bounds[i])}\"")))
                .Append(' ').Append(series.Buckets[i].ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        text.Append(name).Append("_bucket").Append(Braces(Join(labels, "le=\"+Inf\"")))
            .Append(' ').Append(series.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append(name).Append("_sum").Append(Braces(labels)).Append(' ').Append(Number(series.Sum)).Append('\n');
        text.Append(name).Append("_count").Append(Braces(labels)).Append(' ').Append(series.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static string Join(string labels, string extra) => labels.Length == 0 ? extra : $"{labels},{extra}";

    private static string Braces(string labels) => labels.Length == 0 ? string.Empty : "{" + labels + "}";

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string LabelString(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0)
        {
            return string.Empty;
        }

        var pairs = new List<(string Key, string Value)>(tags.Length);
        foreach (var tag in tags)
        {
            pairs.Add((tag.Key, Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty));
        }

        pairs.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return string.Join(',', pairs.Select(p => $"{p.Key}=\"{EscapeLabel(p.Value)}\""));
    }

    private static string EscapeLabel(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string EscapeHelp(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    public void Dispose() => _listener.Dispose();
}

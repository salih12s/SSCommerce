using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SSCommerce.Api.Observability;

namespace SSCommerce.Api.Tests.Observability;

/// <summary>Faz 9 test yardımcıları: iç metrik portu taklidi, günlük yakalama, Prometheus metni okuma.</summary>
internal static class ObservabilityKit
{
    public const int MetricsPort = 19090;
    public const string PortHeader = "X-Test-Local-Port";

    /// <summary>
    /// TestServer gerçek bir soket açmaz; <c>Connection.LocalPort</c> 0'dır. Bu başlangıç filtresi, <c>X-Test-Local-Port</c> başlığı varsa isteğin LocalPort'unu
    /// o değere ayarlar (Kestrel'in iç dinleyiciden geleni taklidi). Üretim kodunda böyle bir başlık YOKTUR; başlık yalnız testte okunur.
    /// </summary>
    public static void AddLocalPortSimulation(IServiceCollection services) =>
        services.AddSingleton<IStartupFilter>(new LocalPortFilter());

    private sealed class LocalPortFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use((context, pipeline) =>
                {
                    if (context.Request.Headers.TryGetValue(PortHeader, out var value) && int.TryParse(value, CultureInfo.InvariantCulture, out var port))
                    {
                        context.Connection.LocalPort = port;
                    }

                    return pipeline(context);
                });
                next(app);
            };
    }

    public static HttpRequestMessage MetricsRequest(int port = MetricsPort, string path = "/metrics")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(PortHeader, port.ToString(CultureInfo.InvariantCulture));
        return request;
    }

    public static async Task<string> ScrapeAsync(HttpClient client)
    {
        var response = await client.SendAsync(MetricsRequest(), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Metindeki ilk seriyi (ad + tüm etiket parçaları eşleşen) okur; yoksa 0.</summary>
    public static double Value(string text, string name, params string[] labelParts)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith('#') || !line.StartsWith(name, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = line[name.Length..];
            if (rest.Length == 0 || (rest[0] != '{' && rest[0] != ' '))
            {
                continue; // başka bir ad önekidir (örn. _count)
            }

            if (labelParts.All(part => line.Contains(part, StringComparison.Ordinal)))
            {
                return double.Parse(line[(line.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture);
            }
        }

        return 0;
    }

    /// <summary>Metindeki tüm etiket adları.</summary>
    public static HashSet<string> LabelNames(string text) =>
        Regex.Matches(text, @"(?<=[{,])([A-Za-z_][A-Za-z0-9_]*)=""").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    // ---------- Günlük yakalama (kapsamlar dahil) ----------

    internal sealed record LogEntry(string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> State, IReadOnlyList<IReadOnlyDictionary<string, object?>> Scopes, string Exception)
    {
        /// <summary>Taramada kullanılacak tek metin: ileti + yapısal alanlar + kapsamlar + istisna.</summary>
        public string Everything =>
            $"{Category} {Level} {Message} {string.Join(' ', State.Select(p => $"{p.Key}={p.Value}"))} " +
            $"{string.Join(' ', Scopes.SelectMany(s => s).Select(p => $"{p.Key}={p.Value}"))} {Exception}";
    }

    internal sealed class CapturingLogs : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose()
        {
        }

        public string AllText => string.Join('\n', Entries.Select(e => e.Everything));

        private sealed class Sink(string category, CapturingLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var fields = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? pairs.ToDictionary(p => p.Key, p => p.Value)
                    : new Dictionary<string, object?>();
                var scopes = new List<IReadOnlyDictionary<string, object?>>();
                owner._scopes.ForEachScope(
                    (scope, list) =>
                    {
                        if (scope is IEnumerable<KeyValuePair<string, object>> kv)
                        {
                            list.Add(kv.ToDictionary(p => p.Key, p => (object?)p.Value));
                        }
                        else
                        {
                            list.Add(new Dictionary<string, object?> { ["scope"] = scope?.ToString() });
                        }
                    },
                    scopes);
                owner.Entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), fields, scopes, exception?.ToString() ?? string.Empty));
            }
        }
    }

    public static void AddCapturingLogs(IServiceCollection services, CapturingLogs logs)
    {
        services.AddSingleton<ILoggerProvider>(logs);
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace));
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SSCommerce.Api.Tests.Database;
using SSCommerce.Infrastructure.Health;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Api.Tests;

/// <summary>
/// Hazırlık (readiness) yolunun D-065 kanıtları. Yeniden deneme ve uyku YOKTUR: her durum belirlenimli kurulur
/// (atılan kayıt, elle ilerletilen saat, yakalanan log). Test amaçlı bir "hazır olana kadar bekle" yardımcısı bilerek yoktur.
/// </summary>
public class ReadinessProofTests
{
    private const string SecretHost = "db-secret-host.internal";

    private static async Task<Dictionary<string, (string Status, string Description)>> ReadyChecksAsync(HttpClient client)
    {
        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("checks").EnumerateArray().ToDictionary(
            c => c.GetProperty("name").GetString()!,
            c => (c.GetProperty("status").GetString()!, c.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty));
    }

    [Fact]
    public async Task The_postgres_check_stays_healthy_when_the_ef_context_cannot_even_be_built()
    {
        // postgres kontrolü EF'e HİÇ dokunmaz: DbContext kaydı atsa bile veritabanı sağlıklıysa o kontrol Healthy kalır.
        // migrations kontrolü DbContext'e ihtiyaç duyar => Unhealthy; ısınma hatası da yutulur, uygulama açılır.
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        await using var factory = ApiFactory.Create(
            "Production",
            database.ConnectionString,
            services =>
            {
                services.RemoveAll<SSCommerceDbContext>();
                services.AddScoped<SSCommerceDbContext>(_ => throw new InvalidOperationException($"model kurulamadı {SecretHost}"));
            });
        using var client = factory.CreateClient();

        var checks = await ReadyChecksAsync(client);

        Assert.Equal("Healthy", checks["postgres"].Status);
        Assert.Equal("Unhealthy", checks["migrations"].Status);
        Assert.DoesNotContain(SecretHost, checks["migrations"].Description);
        Assert.True(factory.Services.GetRequiredService<ReadinessWarmUp>().Attempted); // başlangıç düşmedi
    }

    [Fact]
    public async Task The_migrations_check_is_not_cancelled_by_slow_synchronous_work_done_before_its_timer_starts()
    {
        // Yavaş YEREL iş (DbContext çözümü/model kurulumu) taklidi: DbContext fabrikası sahte saati 10 sn (> 3 sn bütçe) ilerletir.
        // Sayaç bu işten SONRA kurulursa hiçbir şey iptal edilmez; sayaç önce kurulsaydı süre dolar, kontrol Unhealthy olurdu.
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        var clock = new ManualTimeProvider();
        await using var factory = ApiFactory.Create(
            "Production",
            database.ConnectionString,
            services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                var original = services.Single(d => d.ServiceType == typeof(SSCommerceDbContext));
                services.Remove(original);
                services.AddScoped(provider =>
                {
                    if (clock.Armed)
                    {
                        clock.Advance(TimeSpan.FromSeconds(10));
                    }

                    return (SSCommerceDbContext)ActivatorUtilities.CreateInstance(provider, typeof(SSCommerceDbContext));
                });
            });
        using var client = factory.CreateClient();
        clock.Armed = true; // başlangıç ısınması saati oynatmasın; yalnızca ölçülen kontrol

        var checks = await ReadyChecksAsync(client);

        Assert.True(clock.TotalAdvanced >= TimeSpan.FromSeconds(10), "Yavaş iş gerçekten taklit edilmedi (test anlamsız).");
        Assert.Equal("Healthy", checks["migrations"].Status);
        Assert.Equal("Healthy", checks["postgres"].Status);
    }

    [Fact]
    public async Task A_failing_warm_up_is_swallowed_and_logs_only_the_exception_type_never_the_message()
    {
        var logger = new ListLogger<ReadinessWarmUp>();
        var services = new ServiceCollection();
        services.AddScoped<SSCommerceDbContext>(_ => throw new InvalidOperationException($"Host={SecretHost};Password=hunter2"));
        await using var provider = services.BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = $"Host={SecretHost};Database=x;Username=x;Password=hunter2" })
            .Build();
        var warmUp = new ReadinessWarmUp(provider.GetRequiredService<IServiceScopeFactory>(), configuration, logger);

        await warmUp.StartAsync(TestContext.Current.CancellationToken); // atmamalı

        Assert.True(warmUp.Attempted);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(nameof(InvalidOperationException), entry.Message);
        Assert.Null(entry.Exception); // istisna nesnesi (yığın, ileti) loga verilmez
        Assert.DoesNotContain(SecretHost, entry.Message);
        Assert.DoesNotContain("hunter2", entry.Message);
    }

    [Fact]
    public async Task An_unconfigured_connection_string_makes_the_warm_up_do_nothing()
    {
        var logger = new ListLogger<ReadinessWarmUp>();
        var scopeFactory = new ThrowingScopeFactory();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = "  " }).Build();
        var warmUp = new ReadinessWarmUp(scopeFactory, configuration, logger);

        await warmUp.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(warmUp.Attempted);
        Assert.False(scopeFactory.Used); // ne kapsam açıldı ne bağlantı denendi
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task The_host_starts_against_an_unreachable_database_and_the_warm_up_log_has_no_connection_details()
    {
        var logs = new ListLoggerProvider();
        await using var factory = ApiFactory.Create(
            "Production",
            "Host=127.0.0.1;Port=1;Timeout=2;Database=x;Username=x;Password=hunter2",
            services => services.AddSingleton<ILoggerProvider>(logs));
        using var client = factory.CreateClient(); // başlangıç düşerse burada atar

        Assert.True(factory.Services.GetRequiredService<ReadinessWarmUp>().Attempted);
        var warmUpEntries = logs.Entries.Where(e => e.Category == typeof(ReadinessWarmUp).FullName).ToList();
        Assert.NotEmpty(warmUpEntries);
        Assert.All(warmUpEntries, e =>
        {
            Assert.DoesNotContain("127.0.0.1", e.Message);
            Assert.DoesNotContain("hunter2", e.Message);
            Assert.Null(e.Exception);
        });
    }

    // ---------- Yardımcılar ----------

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public bool Used { get; private set; }

        public IServiceScope CreateScope()
        {
            Used = true;
            throw new InvalidOperationException("Kapsam açılmamalıydı.");
        }
    }

    internal sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

    private sealed class ListLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<LogEntry> Items { get; } = new();

        public IReadOnlyList<LogEntry> Entries => [.. Items];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Items.Enqueue(new LogEntry(typeof(T).FullName!, logLevel, formatter(state, exception), exception));
    }

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _items = new();

        public IReadOnlyList<LogEntry> Entries => [.. _items];

        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, _items);

        public void Dispose()
        {
        }

        private sealed class Sink(string category, ConcurrentQueue<LogEntry> items) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                items.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception));
        }
    }

    /// <summary>
    /// Elle ilerletilen saat: zamanlayıcılar YALNIZ <see cref="Advance"/> ile tetiklenir (gerçek zaman geçmez, uyku yok).
    /// <see cref="TimeProvider.GetUtcNow"/> gerçek saattir; yalnız zamanlayıcılar (CancellationTokenSource(süre, saat)) elle sürülür.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private TimeSpan _now;

        public bool Armed { get; set; }

        public TimeSpan TotalAdvanced => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += by;
                due = [.. _timers.Where(t => t.DueAt is { } at && at <= _now)];
                foreach (var timer in due)
                {
                    timer.DueAt = null; // tek atımlık
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan? DueAt { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (!owner._timers.Contains(this))
                    {
                        owner._timers.Add(this);
                    }

                    DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                }

                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (owner._gate)
                {
                    owner._timers.Remove(this);
                    DueAt = null;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

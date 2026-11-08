using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using SSCommerce.Api.Tests.Database;
using SSCommerce.Infrastructure.Health;

namespace SSCommerce.Api.Tests;

/// <summary>
/// "3 sn sınırı gerçekten 3 sn mi?" kanıtları (Faz 9). Durdurulmuş Docker konteynerinin DNS adı çözümlenemeyince .NET'in çözümlemesi iptal belirtecini
/// beklemiyor; <see cref="BoundedWork"/> kesin duvar saati sınırı koyar. DNS gecikmesinin KENDİSİ burada taklit EDİLEMEZ (çözümleyici sahte
/// alan adıyla deterministik olarak yavaşlatılamaz); aynı sınıf hata (iptale saygı göstermeyen/asılan işlem) iki yolla kanıtlanır:
/// (1) el sıkışmada asılan gerçek TCP sunucusu, (2) belirteci hiç dinlemeyen iş. Beklenen süreler gerçek saatle ölçülür, toleransları geniş yazılmıştır.
/// </summary>
public class ReadinessBoundTests
{
    // Üst tolerans: sınır 3 sn + zamanlayıcı/iş parçacığı payı. Gevşek tutuldu ki CI'da flaky olmasın; hata (5 sn+) yine kesin yakalanır.
    private static readonly TimeSpan UpperTolerance = TimeSpan.FromSeconds(4);

    private static IConfiguration ConfigFor(string connectionString) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString }).Build();

    /// <summary>Bağlantıyı kabul eden ama hiç bayt göndermeyen sunucu: Npgsql el sıkışmada (SSL/startup yanıtı) bekler.</summary>
    private sealed class SilentServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _clients = [];
        private readonly CancellationTokenSource _stop = new();

        public SilentServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }

        // Varsayılan Npgsql bağlantı zaman aşımı (15 sn) KASITLI olarak korunur: sınır Npgsql'in kendi zaman aşımından değil bizim sınırımızdan gelmeli.
        public string ConnectionString => $"Host=127.0.0.1;Port={Port};Database=x;Username=x;Password=x";

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    lock (_clients)
                    {
                        _clients.Add(client); // açık tut, hiç yazma
                    }
                }
            }
            catch (Exception)
            {
                // dinleyici kapandı
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            lock (_clients)
            {
                foreach (var client in _clients)
                {
                    client.Dispose();
                }
            }

            _stop.Dispose();
        }
    }

    [Fact]
    public async Task The_postgres_check_returns_unhealthy_within_the_bound_when_the_server_accepts_but_never_answers()
    {
        using var server = new SilentServer();
        var check = new PostgresReadinessHealthCheck(ConfigFor(server.ConnectionString), NullLogger<PostgresReadinessHealthCheck>.Instance);

        var watch = Stopwatch.StartNew();
        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        watch.Stop();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(2.5), $"Sınır beklenmeden döndü ({watch.Elapsed.TotalSeconds:F1} sn): gerçek bir asılma taklit edilmedi.");
        Assert.True(watch.Elapsed < UpperTolerance, $"Kontrol {watch.Elapsed.TotalSeconds:F1} sn sürdü; sınır 3 sn (+ tolerans).");
        Assert.Null(result.Exception); // ayrıntı/istisna yanıta çıkmaz
        Assert.DoesNotContain("127.0.0.1", result.Description);
        Assert.DoesNotContain(server.Port.ToString(), result.Description);
    }

    [Fact]
    public async Task Ready_returns_503_over_http_within_the_bound_when_postgres_hangs_and_the_body_leaks_nothing()
    {
        using var server = new SilentServer();
        // Başlangıç ısınması (ReadinessWarmUp) asılan sunucuda kendi 15 sn bütçesini harcar; ölçüm yalnız istek içindir.
        await using var factory = ApiFactory.Create("Production", server.ConnectionString);
        using var client = factory.CreateClient();

        var watch = Stopwatch.StartNew();
        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        watch.Stop();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // İki kontrol paralel ve ikisi de sınırlı: toplam ≈ 3 sn (+ tolerans), 3 + 3 değil.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4.5), $"/health/ready {watch.Elapsed.TotalSeconds:F1} sn sürdü; ≤ ~3,3 sn beklenirdi.");

        using var json = JsonDocument.Parse(body);
        Assert.Equal("Unhealthy", json.RootElement.GetProperty("status").GetString());
        var checks = json.RootElement.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);
        Assert.Equal(["migrations", "postgres"], checks.Keys.Order().ToArray());
        Assert.All(checks.Values, check =>
        {
            Assert.Equal("Unhealthy", check.GetProperty("status").GetString());
            Assert.True(check.GetProperty("durationMs").GetInt64() < UpperTolerance.TotalMilliseconds);
        });
        Assert.DoesNotContain("127.0.0.1", body);
        Assert.DoesNotContain(server.Port.ToString(), body);
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Username", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_postgres_check_is_healthy_and_fast_against_a_reachable_database()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        var check = new PostgresReadinessHealthCheck(ConfigFor(database.ConnectionString), NullLogger<PostgresReadinessHealthCheck>.Instance);

        var watch = Stopwatch.StartNew();
        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        watch.Stop();

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.True(watch.Elapsed < PostgresReadinessHealthCheck.Timeout, $"Sağlıklı kontrol sınıra yakın sürdü: {watch.Elapsed.TotalSeconds:F1} sn.");
    }

    [Fact]
    public async Task Work_that_ignores_cancellation_is_abandoned_at_the_bound_but_still_releases_its_resource_when_it_finishes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Belirteci HİÇ dinlemeyen iş (DNS çözümlemesi gibi): kapıya kadar bekler, sonra atar. Kaynak, işin KENDİ finally'sinde bırakılır.
        var watch = Stopwatch.StartNew();
        var (completed, _) = await BoundedWork.RunAsync<int>(
            async _ =>
            {
                try
                {
                    await gate.Task;
                    throw new InvalidOperationException("iş bittikten sonra patladı");
                }
                finally
                {
                    released.SetResult();
                }
            },
            TimeSpan.FromMilliseconds(200),
            TimeProvider.System,
            TestContext.Current.CancellationToken);
        watch.Stop();

        Assert.False(completed);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), "Sınır, iş bitmeden de uygulanmalı.");
        Assert.False(released.Task.IsCompleted, "İş henüz sürüyordu: kaynak erken bırakılmamalı.");

        gate.SetResult(); // kopan iş şimdi biter (ve atar)
        await released.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_exception_of_an_abandoned_work_is_observed_and_raises_no_unobserved_task_exception()
    {
        var unobserved = new List<Exception>();
        void Handler(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            lock (unobserved)
            {
                unobserved.Add(args.Exception);
            }
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var (completed, _) = await BoundedWork.RunAsync<int>(
                async _ =>
                {
                    try
                    {
                        await gate.Task;
                        throw new InvalidOperationException("kopan işin istisnası");
                    }
                    finally
                    {
                        finished.SetResult();
                    }
                },
                TimeSpan.FromMilliseconds(100),
                TimeProvider.System,
                TestContext.Current.CancellationToken);
            Assert.False(completed);

            gate.SetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken); // finally'den sonra Task'ın hatalı duruma geçmesi için pay

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            lock (unobserved)
            {
                Assert.Empty(unobserved);
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [Fact]
    public async Task Work_that_finishes_inside_the_bound_returns_its_result_and_its_exception_propagates()
    {
        var (completed, result) = await BoundedWork.RunAsync(_ => Task.FromResult(42), TimeSpan.FromSeconds(3), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.True(completed);
        Assert.Equal(42, result);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BoundedWork.RunAsync<int>(_ => throw new InvalidOperationException("x"), TimeSpan.FromSeconds(3), TimeProvider.System, TestContext.Current.CancellationToken));
    }
}

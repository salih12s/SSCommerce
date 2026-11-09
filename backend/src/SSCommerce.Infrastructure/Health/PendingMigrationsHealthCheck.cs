using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Health;

/// <summary>
/// Bekleyen EF migration'ı varsa hazır değildir (D-054): katalog artık stok tablolarını okuduğundan, migration'ı eksik veritabanında
/// "ready 200 + katalog 500" olurdu. Açıklamalar SABİTTİR: migration adı, tablo adı, bağlantı bilgisi yanıta sızmaz; ayrıntı yalnız loglarda
/// ve orada da yalnız bekleyen sayısı / istisna tipidir.
/// </summary>
/// <param name="state">Süreç boyunca tek örnek. Yalnız OLUMLU sonuç (şema güncel) saklanır; olumsuz sonuç her çağrıda yeniden değerlendirilir,
/// bu yüzden sonradan migrate edilen veritabanı yeniden başlatma olmadan hazır olur. Sızdırılan migration adı yoktur: yalnız bir bayrak.</param>
/// <param name="timeProvider">3 sn sayacının saati (D-065). Üretimde sistem saati; test, sayaçtan ÖNCE yapılan yavaş yerel işi sabit saatle
/// (uyku olmadan, belirlenimli) taklit eder ve sayacın o işi saymadığını kanıtlar.</param>
public sealed class PendingMigrationsHealthCheck(
    MigrationReadinessState state,
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<PendingMigrationsHealthCheck> logger,
    TimeProvider timeProvider) : IHealthCheck
{
    public const string Name = "migrations";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Migration'lar uygulama ömrü boyunca geri alınmaz; olumlu sonuç bir kez doğrulandıysa her /health/ready çağrısında
        // yeniden migration geçmişini sorgulamaya gerek yok (sık yoklanan uçta gereksiz DB yükü).
        if (state.IsConfirmedUpToDate)
        {
            return HealthCheckResult.Healthy("Veritabanı şeması güncel.");
        }

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(DependencyInjection.ConnectionStringName)))
        {
            return HealthCheckResult.Unhealthy("Veritabanı bağlantısı yapılandırılmamış.");
        }

        // Kapsam (ve DbContext) işin KENDİSİ bitince bırakılır: sınır dolup iş koparılsa bile sızıntı olmaz. İş başlamadan önce atan yolda burada bırakılır.
        var scope = scopeFactory.CreateAsyncScope();
        var handedOff = false;
        try
        {
            // D-065: kesilemeyen YEREL iş (DbContext çözümü, modelin kurulması) sayaç başlamadan ÖNCE biter. Sayaç yalnız veritabanına sorulan
            // süreyi ölçer; aksi halde EF'in ilk kullanım maliyeti, veritabanı hiç sorulmadan "zaman aşımı" diye raporlanırdı.
            var db = scope.ServiceProvider.GetRequiredService<SSCommerceDbContext>();
            _ = db.Model;

            // Kesin duvar saati sınırı (bkz. BoundedWork): sayaç yukarıdaki yerel işten SONRA kurulur (BoundedWork.RunAsync içinde Task.Delay burada başlar).
            handedOff = true;
            var (completed, pendingCount) = await BoundedWork.RunAsync(
                async token =>
                {
                    try
                    {
                        return (await db.Database.GetPendingMigrationsAsync(token)).Count();
                    }
                    finally
                    {
                        await scope.DisposeAsync();
                    }
                },
                PostgresReadinessHealthCheck.Timeout,
                timeProvider,
                cancellationToken);
            if (!completed)
            {
                logger.LogWarning("Migration denetimi {Seconds} sn içinde tamamlanamadı.", PostgresReadinessHealthCheck.Timeout.TotalSeconds);
                return HealthCheckResult.Unhealthy("Migration durumu okunamadı.");
            }

            var pending = pendingCount;
            if (pending > 0)
            {
                logger.LogWarning("Bekleyen veritabanı migration sayısı: {PendingCount}", pending);
                return HealthCheckResult.Unhealthy("Bekleyen veritabanı migration'ı var.");
            }

            state.ConfirmUpToDate();
            return HealthCheckResult.Healthy("Veritabanı şeması güncel.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Migration denetimi {Seconds} sn içinde tamamlanamadı.", PostgresReadinessHealthCheck.Timeout.TotalSeconds);
            return HealthCheckResult.Unhealthy("Migration durumu okunamadı.");
        }
        catch (Exception ex)
        {
            logger.LogWarning("Migration denetimi başarısız: {ExceptionType}", ex.GetType().Name);
            return HealthCheckResult.Unhealthy("Migration durumu okunamadı.");
        }
        finally
        {
            if (!handedOff)
            {
                await scope.DisposeAsync();
            }
        }
    }
}

/// <summary>"Bekleyen migration yok" sonucunun süreç ömrü boyunca önbelleği (singleton). Olumsuz sonuç asla saklanmaz.</summary>
public sealed class MigrationReadinessState
{
    private volatile bool _confirmed;

    public bool IsConfirmedUpToDate => _confirmed;

    public void ConfirmUpToDate() => _confirmed = true;
}

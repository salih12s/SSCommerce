using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Health;

/// <summary>
/// Soğuk başlangıç ısınması (D-065). <c>/health/ready</c> kontrollerinin 3 sn sayacı, kesilemeyen YEREL işten (EF ilk kullanımı, model kurulumu,
/// migration assembly taraması, JIT) ÖNCE başlıyordu; veritabanına hiç sorulmadan "zaman aşımı" raporlanıyordu. Bu servis o işi bir kez, sayaçlardan
/// ÖNCE yapar: <see cref="IHostedService.StartAsync"/> Kestrel dinlemeye BAŞLAMADAN çalışır (<c>GenericWebHostService</c> son kayıtlı barındırılan
/// servistir), yani hazırlık yoklaması başlangıçta bağlantı reddi görür, yanlış 503 almaz. <c>ApplicationStarted</c> KULLANILMAZ: o olay port açıldıktan sonra gelir.
/// <list type="bullet">
/// <item>Bağlantı dizesi yoksa hiçbir şey yapmaz (uygulama bağlantısız da açılır).</item>
/// <item>Kendi 15 sn sayacıyla sınırlıdır; süre aşılırsa ya da ne olursa olsun hata YUTULUR (başlangıcı asla düşürmez) ve yalnız istisna TİPİ loglanır
/// (Npgsql iletileri host/port içerebilir).</item>
/// <item>Sıra: DbContext çöz, modeli ve migration türlerini yükle, bir <c>GetPendingMigrationsAsync</c>, bir ham Npgsql bağlantısı aç-kapat
/// (<c>postgres</c> kontrolünün kullandığı yol ısınsın).</item>
/// </list>
/// </summary>
public sealed class ReadinessWarmUp(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ReadinessWarmUp> logger) : IHostedService
{
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    /// <summary>Isınma denendi (başarılı ya da yutulmuş hatayla) ve tamamlandı. Başlangıç sırasını doğrulayan test okur.</summary>
    public bool Attempted { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = configuration.GetConnectionString(DependencyInjection.ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Budget);

            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SSCommerceDbContext>();
                _ = db.Model;
                _ = db.GetService<IMigrationsAssembly>().Migrations;
                _ = (await db.Database.GetPendingMigrationsAsync(timeout.Token)).Count();
            }

            await using var connection = new NpgsqlConnection(DependencyInjection.WithPoolLimit(connectionString));
            await connection.OpenAsync(timeout.Token);
            await connection.CloseAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning("Hazırlık ısınması tamamlanamadı (yutuldu): {ExceptionType}", exception.GetType().Name);
        }
        finally
        {
            Attempted = true;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

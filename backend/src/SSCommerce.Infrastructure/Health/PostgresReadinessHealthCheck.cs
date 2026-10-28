using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Health;

/// <summary>
/// PostgreSQL'e gerçekten bağlanıp <c>SELECT 1</c> çalıştırır.
/// Açıklama metinleri sabittir: bağlantı dizesi, host ya da parola asla yanıta sızmaz.
/// Ayrıntı yalnızca sunucu logundadır (ve orada da yalnızca istisna tipi).
/// </summary>
public sealed class PostgresReadinessHealthCheck(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<PostgresReadinessHealthCheck> logger) : IHealthCheck
{
    public const string Name = "postgres";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(DependencyInjection.ConnectionStringName)))
        {
            return HealthCheckResult.Unhealthy("Veritabanı bağlantısı yapılandırılmamış.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            // DbContext scoped; sağlık kontrolü kendi kapsamını açar.
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SSCommerceDbContext>();
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync(timeout.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(timeout.Token);
            return HealthCheckResult.Healthy("PostgreSQL bağlantısı başarılı.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("PostgreSQL hazırlık kontrolü {Seconds} sn içinde tamamlanamadı.", Timeout.TotalSeconds);
            return HealthCheckResult.Unhealthy("PostgreSQL zaman aşımına uğradı.");
        }
        catch (Exception ex)
        {
            logger.LogWarning("PostgreSQL hazırlık kontrolü başarısız: {ExceptionType}", ex.GetType().Name);
            return HealthCheckResult.Unhealthy("PostgreSQL'e bağlanılamadı.");
        }
    }
}

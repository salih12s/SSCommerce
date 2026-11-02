using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace SSCommerce.Infrastructure.Health;

/// <summary>
/// PostgreSQL'e gerçekten bağlanıp <c>SELECT 1</c> çalıştırır. EF Core'a HİÇ dokunmaz (D-065): ham <see cref="NpgsqlConnection"/> kullanır,
/// böylece EF'in ilk kullanım maliyeti (model kurulumu, JIT) ya da DbContext kurulamaması bu kontrolü etkilemez; yalnız VERİTABANININ yaptığını raporlar.
/// 3 sn sayacı bağlantı açma + sorgu içindir. Açıklama metinleri sabittir: bağlantı dizesi, host ya da parola asla yanıta sızmaz.
/// Ayrıntı yalnızca sunucu logundadır (ve orada da yalnızca istisna tipi). Soğuk başlangıç ısınması: <see cref="ReadinessWarmUp"/>.
/// </summary>
public sealed class PostgresReadinessHealthCheck(
    IConfiguration configuration,
    ILogger<PostgresReadinessHealthCheck> logger) : IHealthCheck
{
    public const string Name = "postgres";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString(DependencyInjection.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return HealthCheckResult.Unhealthy("Veritabanı bağlantısı yapılandırılmamış.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            // Uygulama bağlantısıyla AYNI dize ve havuz sınırı; bağlantı havuzdan gelir (ısınma havuzu doldurmuştur).
            await using var connection = new NpgsqlConnection(DependencyInjection.WithPoolLimit(connectionString));
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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace SSCommerce.Infrastructure.Health;

/// <summary>
/// PostgreSQL'e gerçekten bağlanıp <c>SELECT 1</c> çalıştırır. EF Core'a HİÇ dokunmaz (D-065): ham <see cref="NpgsqlConnection"/> kullanır,
/// böylece EF'in ilk kullanım maliyeti (model kurulumu, JIT) ya da DbContext kurulamaması bu kontrolü etkilemez; yalnız VERİTABANININ yaptığını raporlar.
/// 3 sn sınırı bağlantı açma + sorgu içindir ve KESİN duvar saati sınırıdır (<see cref="BoundedWork"/>): DNS çözümlemesi belirteci beklemese de 3 sn'de döner. Açıklama metinleri sabittir: bağlantı dizesi, host ya da parola asla yanıta sızmaz.
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

        try
        {
            // Kesin duvar saati sınırı (bkz. BoundedWork): DNS çözümlemesi iptal belirtecini beklemez. Bağlantı, işin KENDİ içinde
            // `await using` ile kapanır; iş sınırdan sonra bitse bile bağlantı sızmaz.
            // Sınırın saati bilerek sistem saatidir (TimeProvider enjekte edilmez): migrations kontrolünün sahte saati bu kontrolü paralelde tetiklemesin.
            var (completed, _) = await BoundedWork.RunAsync(
                token => ProbeAsync(connectionString, token), Timeout, TimeProvider.System, cancellationToken);
            if (!completed)
            {
                logger.LogWarning("PostgreSQL hazırlık kontrolü {Seconds} sn içinde tamamlanamadı.", Timeout.TotalSeconds);
                return HealthCheckResult.Unhealthy("PostgreSQL zaman aşımına uğradı.");
            }

            return HealthCheckResult.Healthy("PostgreSQL bağlantısı başarılı.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // İş kendi içinde iptal edildi (ör. Npgsql kendi zaman aşımı): zaman aşımı say.
            logger.LogWarning("PostgreSQL hazırlık kontrolü {Seconds} sn içinde tamamlanamadı.", Timeout.TotalSeconds);
            return HealthCheckResult.Unhealthy("PostgreSQL zaman aşımına uğradı.");
        }
        catch (Exception ex)
        {
            logger.LogWarning("PostgreSQL hazırlık kontrolü başarısız: {ExceptionType}", ex.GetType().Name);
            return HealthCheckResult.Unhealthy("PostgreSQL'e bağlanılamadı.");
        }
    }

    private static async Task<bool> ProbeAsync(string connectionString, CancellationToken cancellationToken)
    {
        // Uygulama bağlantısıyla AYNI dize ve havuz sınırı; bağlantı havuzdan gelir (ısınma havuzu doldurmuştur).
        await using var connection = new NpgsqlConnection(DependencyInjection.WithPoolLimit(connectionString));
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(cancellationToken);
        return true;
    }
}

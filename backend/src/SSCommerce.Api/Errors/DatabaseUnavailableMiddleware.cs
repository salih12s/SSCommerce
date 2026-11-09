using System.Net.Sockets;
using Npgsql;
using SSCommerce.Api.Observability;

namespace SSCommerce.Api.Errors;

/// <summary>
/// Bağlantı düzeyindeki veritabanı hatalarının sınıflandırıcısı (saf). "Veritabanına ULAŞILAMIYOR" ile "veritabanı bir isteği reddetti"yi ayırır:
/// yalnız birincisi 503'tür; ikincisi (kısıt ihlali, SQL hatası, kilit zaman aşımı, uygulama hatası) 500 ya da kendi eşlemesinde (stock_busy, busy) kalır.
/// <list type="bullet">
/// <item><see cref="NpgsqlException"/> ve <c>SqlState</c> YOK ve neden zincirinde soket/G-Ç/zaman aşımı hatası var: bağlantı reddi, açılış zaman aşımı,
/// bağlantı havuzu tükenmesi zaman aşımı, kopan bağlantı. Neden zincirinde bilinen bir neden yoksa (protokol hatası vb.) temkinli davranılır: 500.</item>
/// <item><see cref="PostgresException"/>: yalnız bağlantı sınıfı kodlar: <c>08xxx</c> (bağlantı hatası), <c>57P01/02/03</c> (yönetici kapatması,
/// çökme kapanışı, şu an bağlanılamıyor), <c>53300</c> (çok fazla bağlantı). Diğer tüm SQL hataları (23xxx kısıtlar, 42xxx, 22xxx, 55P03 kilit zaman aşımı...) 500.</item>
/// </list>
/// Çıplak <see cref="SocketException"/>/<see cref="TimeoutException"/>, iletisi bağlantıya benzeyen genel istisnalar ve yapılandırma hataları sınıflanmaz.
/// </summary>
public static class DatabaseFailures
{
    public static bool IsUnavailable(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate)
            {
                if (aggregate.InnerExceptions.Any(IsUnavailable))
                {
                    return true;
                }

                continue;
            }

            if (current is PostgresException postgres)
            {
                return IsConnectionClass(postgres.SqlState);
            }

            if (current is NpgsqlException npgsql && npgsql.SqlState is null && HasTransportCause(npgsql))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsConnectionClass(string? sqlState) =>
        sqlState is not null &&
        (sqlState.StartsWith("08", StringComparison.Ordinal) || sqlState is "57P01" or "57P02" or "57P03" or "53300");

    private static bool HasTransportCause(Exception exception)
    {
        for (var cause = exception.InnerException; cause is not null; cause = cause.InnerException)
        {
            if (cause is SocketException or IOException or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Veritabanına ulaşılamadığında (bkz. <see cref="DatabaseFailures"/>) TÜM API yolları için merkezî 503 <c>service_unavailable</c>: sabit başlık,
/// <c>Retry-After</c>, ayrıntı yok. Hata işleyicisinin (UseExceptionHandler) İÇİNDE durur ve kimlik doğrulamayı da sarar; böylece istisna, tam istisna
/// metnini yazan genel hata günlüğüne hiç ulaşmaz. Burada YALNIZ istisna tipi loglanır (Npgsql iletileri host/port içerebilir).
/// </summary>
public sealed class DatabaseUnavailableMiddleware(RequestDelegate next, ILogger<DatabaseUnavailableMiddleware> logger, SscMetrics metrics)
{
    public const int RetryAfterSeconds = 5;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception) when (!context.Response.HasStarted && DatabaseFailures.IsUnavailable(exception))
        {
            logger.LogWarning("Veritabanına ulaşılamadı ({ExceptionType}); 503 dönüldü.", ExceptionType(exception));
            metrics.RecordDatabaseUnavailable();
            context.Response.Clear();
            context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await ApiResults.Problem(
                StatusCodes.Status503ServiceUnavailable,
                "Hizmet şu an kullanılamıyor, lütfen kısa süre sonra tekrar deneyin",
                ApiErrorCodes.ServiceUnavailable).ExecuteAsync(context);
        }
    }

    private static string ExceptionType(Exception exception) => exception.GetType().Name;
}

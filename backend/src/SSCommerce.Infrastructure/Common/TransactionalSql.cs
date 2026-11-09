using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure.Common;

/// <summary>
/// Açık bir EF transaction'ı içinde PARAMETRİK ham SQL çalıştırma yardımcıları (kilitler, koşullu yazmalar). SQL metni sabittir; kullanıcı
/// girdisi yalnız parametre olarak gider, hiçbir yerde birleştirilmez.
/// </summary>
internal static class TransactionalSql
{
    /// <summary>Kilit bekleme üst sınırı (advisory/satır kilidi). Sabit metin.</summary>
    public const string LockTimeoutSql = "SET LOCAL lock_timeout = '5s'";

    public static NpgsqlCommand Create(SSCommerceDbContext db, IDbContextTransaction transaction, string sql)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
        command.CommandTimeout = DependencyInjection.CommandTimeoutSeconds;
        return command;
    }

    public static async Task ExecuteAsync(
        SSCommerceDbContext db,
        IDbContextTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = Create(db, transaction, sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<object?> ScalarAsync(
        SSCommerceDbContext db,
        IDbContextTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = Create(db, transaction, sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>
    /// "Meşgul" durumu: PostgreSQL 55P03 (lock_not_available, kilit bekleme zaman aşımı) ya da 40P01 (deadlock_detected). İkisinde de transaction
    /// geri alınmıştır, hiçbir şey yazılmadı ve istek güvenle tekrar denenebilir (503 busy). Faz 8'den önce yalnız 55P03 sayılırdı.
    /// </summary>
    public static bool IsLockTimeout(Exception exception) => PostgresOf(exception) is
        { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected };

    /// <summary>
    /// İstisnanın kendisi ya da iç istisna zincirindeki ilk PostgreSQL hatasını döndürür. Zincir gerekli: EF <c>DbUpdateException</c> sarar, ayrıca
    /// <c>NpgsqlExecutionStrategy</c> 55P03/40P01 gibi "geçici" hataları kullanıcı transaction'ı içinde yeniden denemeyip
    /// <c>InvalidOperationException("...transient failure")</c> içine sarar (FromSql/okuma sorgularında). Yalnız bir kat açmak 503 busy'yi 500'e düşürürdü.
    /// </summary>
    public static PostgresException? PostgresOf(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }
}

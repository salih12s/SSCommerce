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

    /// <summary>PostgreSQL 55P03 (lock_not_available): kilit bekleme zaman aşımı.</summary>
    public static bool IsLockTimeout(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } ||
        exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } };
}

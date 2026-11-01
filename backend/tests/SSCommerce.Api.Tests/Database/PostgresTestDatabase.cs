using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// İzole gerçek PostgreSQL veritabanı (D-029). <c>SSCOMMERCE_TEST_POSTGRES</c> sunucusunda <c>sscommerce_test_&lt;guid N&gt;</c> adlı
/// YENİ bir veritabanı açar, (istenirse) migration'ları sıfırdan uygular, <see cref="DisposeAsync"/> ile siler.
/// Geliştirme veritabanı <c>sscommerce</c>'e hiçbir yolla dokunmaz: ad önek + desen guard'ından geçmeyen veritabanı silinmez.
/// API testleri için <see cref="ConnectionString"/> doğrudan <c>ApiFactory.Create(env, connectionString)</c>'e verilebilir.
/// </summary>
public sealed partial class PostgresTestDatabase : IAsyncDisposable
{
    public const string EnvironmentVariable = "SSCOMMERCE_TEST_POSTGRES";
    public const string NamePrefix = "sscommerce_test_";

    [GeneratedRegex("^sscommerce_test_[0-9a-f]{32}$")]
    private static partial Regex SafeNameRegex();

    private readonly NpgsqlConnectionStringBuilder _adminConnection;

    private PostgresTestDatabase(NpgsqlConnectionStringBuilder adminConnection, string name, string connectionString)
    {
        _adminConnection = adminConnection;
        Name = name;
        ConnectionString = connectionString;
    }

    public string Name { get; }

    /// <summary>Bu izole veritabanına bağlantı dizesi (havuzlu; silmeden önce havuz temizlenir, kalanları <c>WITH (FORCE)</c> kapatır).</summary>
    public string ConnectionString { get; }

    /// <summary>
    /// Değişken tanımlıysa veritabanını açar. Tanımsızsa: CI'da test KIRMIZI olur, yerelde atlanır (HealthEndpointTests ile aynı kural).
    /// </summary>
    public static async Task<PostgresTestDatabase> CreateAsync(bool migrate = true)
    {
        var database = await TryCreateAsync(migrate);
        if (database is null)
        {
            Assert.Skip($"{EnvironmentVariable} tanımlı değil; gerçek PostgreSQL testi atlandı (geçti sayılmaz).");
        }

        return database;
    }

    public static async Task<PostgresTestDatabase?> TryCreateAsync(bool migrate = true)
    {
        var admin = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(admin))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Fail($"CI=true ama {EnvironmentVariable} tanımlı değil; gerçek PostgreSQL testi atlanamaz. Değişkeni ve postgres servisini kontrol edin.");
            }

            return null;
        }

        // Yönetim bağlantısı bakım veritabanına (postgres) açılır; asıl uygulama veritabanında iş yapılmaz.
        // Havuzlu bağlantı: havuzsuz modda her işlem yeni bir TCP soketi açıyordu; yüzlerce testte Windows'un
        // geçici port aralığı TIME_WAIT soketleriyle doluyor ve bağlantı "WSAEADDRINUSE" ile düşüyordu (flaky).
        var adminBuilder = new NpgsqlConnectionStringBuilder(admin) { Database = "postgres", Pooling = true };
        var name = NamePrefix + Guid.NewGuid().ToString("N");
        EnsureSafeName(name);

        await using (var connection = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync();
        }

        var testBuilder = new NpgsqlConnectionStringBuilder(admin) { Database = name, Pooling = true };
        var database = new PostgresTestDatabase(adminBuilder, name, testBuilder.ConnectionString);

        if (migrate)
        {
            try
            {
                await using var context = database.CreateDbContext();
                await context.Database.MigrateAsync();
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        return database;
    }

    public SSCommerceDbContext CreateDbContext(TimeProvider? timeProvider = null)
    {
        var options = new DbContextOptionsBuilder<SSCommerceDbContext>().UseNpgsql(ConnectionString).Options;
        return new SSCommerceDbContext(options, timeProvider ?? TimeProvider.System);
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public async Task<T?> ScalarAsync<T>(string sql, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    public async Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        // Guard: yalnızca bu sınıfın ürettiği, desene uyan veritabanı silinir.
        EnsureSafeName(Name);

        // Bu veritabanına açık kalan havuz bağlantılarını bırak; silinmiş veritabanına ait ölü bağlantı kalmasın.
        using (var pooled = new NpgsqlConnection(ConnectionString))
        {
            NpgsqlConnection.ClearPool(pooled);
        }

        await using var connection = new NpgsqlConnection(_adminConnection.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private static void EnsureSafeName(string name)
    {
        if (!name.StartsWith(NamePrefix, StringComparison.Ordinal) || !SafeNameRegex().IsMatch(name))
        {
            throw new InvalidOperationException($"Güvensiz test veritabanı adı reddedildi (önek {NamePrefix} ve guid deseni gerekli).");
        }
    }
}

/// <summary>
/// Sınıf başına bir izole, migrate edilmiş veritabanı. Test sınıfı <c>IClassFixture&lt;MigratedDatabaseFixture&gt;</c> alır.
/// Aynı sınıftaki testler veritabanını paylaşır: her test KENDİ benzersiz verisini kurmalıdır (isolation).
/// </summary>
public sealed class MigratedDatabaseFixture : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public PostgresTestDatabase Database =>
        _database ?? throw SkipException();

    public async ValueTask InitializeAsync() => _database = await PostgresTestDatabase.TryCreateAsync(migrate: true);

    public async ValueTask DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private static Exception SkipException()
    {
        Assert.Skip($"{PostgresTestDatabase.EnvironmentVariable} tanımlı değil; gerçek PostgreSQL testi atlandı (geçti sayılmaz).");
        return new InvalidOperationException(); // erişilmez
    }
}

/// <summary>Testlerde saat sabitleme: <c>new Date()</c> kaynaklı flaky testleri önler.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

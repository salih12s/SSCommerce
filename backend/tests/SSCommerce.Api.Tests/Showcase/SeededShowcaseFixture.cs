using Microsoft.AspNetCore.Mvc.Testing;
using SSCommerce.Api.Tests.Database;

namespace SSCommerce.Api.Tests.Showcase;

/// <summary>
/// Sınıf başına TEK izole veritabanı: migration'lar sıfırdan, ardından <c>--seed-showcase</c> komutu İKİ KEZ çalıştırılır.
/// Okuma-yalnız testler (demo giriş, veri şekli, idempotency) bu ortak durumu paylaşır; veriyi değiştiren testler kendi veritabanını açar (isolation).
/// Günlükler (<see cref="Logs"/>) Trace seviyesinde tutulur: sızıntı taraması gerçek günlük çıktısı üzerinde yapılır.
/// </summary>
public sealed class SeededShowcaseFixture : IAsyncLifetime
{
    private PostgresTestDatabase? _database;
    private WebApplicationFactory<Program>? _factory;

    internal ShowcaseKit.MemoryLogs Logs { get; } = new();

    internal ShowcaseKit.Run FirstRun { get; private set; } = new(-1, string.Empty, string.Empty);

    internal ShowcaseKit.Run SecondRun { get; private set; } = new(-1, string.Empty, string.Empty);

    public string SnapshotAfterFirst { get; private set; } = string.Empty;

    public string SnapshotAfterSecond { get; private set; } = string.Empty;

    /// <summary>Vitrin müşterisinin parola özeti ve security stamp'i her koşudan sonra: seed her koşuda ikisini de yeniler (sertleştirme).</summary>
    public string CredentialAfterFirst { get; private set; } = string.Empty;

    public string CredentialAfterSecond { get; private set; } = string.Empty;

    public IReadOnlyList<string> ViolatedAfterFirst { get; private set; } = [];

    public IReadOnlyList<string> ViolatedAfterSecond { get; private set; } = [];

    /// <summary>İlk koşunun başlamadan önceki ve bittikten sonraki gerçek anı: tarih penceresi doğrulaması için.</summary>
    public DateTimeOffset FirstRunStartedAt { get; private set; }

    public DateTimeOffset FirstRunFinishedAt { get; private set; }

    public PostgresTestDatabase Database => _database ?? throw SkipException();

    internal WebApplicationFactory<Program> Factory => _factory ?? throw SkipException();

    public async ValueTask InitializeAsync()
    {
        _database = await PostgresTestDatabase.TryCreateAsync(migrate: true);
        if (_database is null)
        {
            return;
        }

        _factory = ShowcaseKit.Factory(_database, logs: Logs);

        FirstRunStartedAt = DateTimeOffset.UtcNow;
        FirstRun = await ShowcaseKit.SeedAsync(_factory);
        FirstRunFinishedAt = DateTimeOffset.UtcNow;
        SnapshotAfterFirst = await ShowcaseKit.SnapshotAsync(_database);
        ViolatedAfterFirst = await ShowcaseKit.ViolatedInvariantsAsync(_database);
        CredentialAfterFirst = await CredentialAsync(_database);

        SecondRun = await ShowcaseKit.SeedAsync(_factory);
        SnapshotAfterSecond = await ShowcaseKit.SnapshotAsync(_database);
        ViolatedAfterSecond = await ShowcaseKit.ViolatedInvariantsAsync(_database);
        CredentialAfterSecond = await CredentialAsync(_database);
    }

    private static async Task<string> CredentialAsync(PostgresTestDatabase database) =>
        await ShowcaseKit.Sql<string>(
            database,
            "select \"PasswordHash\" || '|' || \"SecurityStamp\" from \"AspNetUsers\" where \"Email\" = 'demo-musteri@sscommerce.example'") ?? string.Empty;

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private static Exception SkipException()
    {
        Assert.Skip($"{PostgresTestDatabase.EnvironmentVariable} tanımlı değil; gerçek PostgreSQL testi atlandı (geçti sayılmaz).");
        return new InvalidOperationException();
    }
}

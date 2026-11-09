using Npgsql;
using SSCommerce.Infrastructure.Persistence.Seed;

namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// Eski şema sürümünde (ör. AddCartAndAddresses) duran bir veritabanına demo kataloğu yerleştirir. <see cref="DemoCatalogSeeder"/> güncel EF
/// modelini kullanır; Faz 8'den beri model <c>version</c> sütunlarını da okuyup yazar ve eski şemada patlar. Bu yardımcı seed'i güncel şemalı
/// bir YARDIMCI veritabanında çalıştırır, satırları JSON olarak alır ve hedefe hedef tablonun KENDİ satır tipiyle (fazla anahtarlar yok sayılır)
/// yazar: böylece eski göçlerin "veri korunuyor mu" testleri gerçek katalog verisiyle çalışmaya devam eder.
/// Hedef zaten güncel şemadaysa doğrudan <see cref="DemoCatalogSeeder"/> kullanılır.
/// </summary>
internal static class LegacySchemaSeed
{
    // Dış anahtar sırası: categories -> products -> product_variants / product_images.
    private static readonly string[] Tables = ["categories", "products", "product_variants", "product_images"];

    public static async Task SeedDemoCatalogAsync(PostgresTestDatabase target, CancellationToken cancellationToken)
    {
        var hasVersion = await target.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'categories' AND column_name = 'version'", cancellationToken);
        if (hasVersion > 0)
        {
            await using var context = target.CreateDbContext();
            await new DemoCatalogSeeder(context).SeedAsync(cancellationToken);
            return;
        }

        await using var helper = await PostgresTestDatabase.CreateAsync(migrate: true);
        await using (var context = helper.CreateDbContext())
        {
            await new DemoCatalogSeeder(context).SeedAsync(cancellationToken);
        }

        foreach (var table in Tables)
        {
            var json = await helper.ScalarAsync<string>($"SELECT coalesce(json_agg(t), '[]'::json)::text FROM {table} t", cancellationToken);
            await using var connection = await target.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $"INSERT INTO {table} SELECT * FROM json_populate_recordset(null::{table}, @rows::json)", connection);
            command.Parameters.AddWithValue("rows", json!);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

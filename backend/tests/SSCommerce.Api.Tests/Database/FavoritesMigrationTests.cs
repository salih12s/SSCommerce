using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// MD-1 <c>AddCustomerFavorites</c> migration'ı. Yalnız EKLEYİCİDİR (yeni tablo): mevcut tabloların satırları değişmez, hiçbir şey düşmez.
/// Şema üzerinde oynadıkları için her test KENDİ izole veritabanını açar.
/// </summary>
public partial class FavoritesMigrationTests
{
    private const string PreviousMigration = "AddAdminOperations";
    private const string Table = "customer_favorites";

    [GeneratedRegex(@"\bDROP\b", RegexOptions.IgnoreCase)]
    private static partial Regex DropRegex();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task MigrateToAsync(PostgresTestDatabase database, string? targetMigration)
    {
        await using var context = database.CreateDbContext();
        await context.GetService<IMigrator>().MigrateAsync(targetMigration, Ct);
    }

    private static async Task<bool> TableExistsAsync(PostgresTestDatabase database) =>
        await database.ScalarAsync<long>($"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{Table}'", Ct) == 1;

    private static async Task<string> ColumnAsync(PostgresTestDatabase database, string column) =>
        (await database.ScalarAsync<string>(
            $"SELECT data_type || ':' || is_nullable FROM information_schema.columns WHERE table_name = '{Table}' AND column_name = '{column}'", Ct))!;

    private static async Task<Guid> InsertUserAsync(PostgresTestDatabase database) => await new OrderingTestData(database).InsertUserAsync();

    private static async Task<Guid> InsertProductAsync(PostgresTestDatabase database)
    {
        await LegacySchemaSeed.SeedDemoCatalogAsync(database, Ct);
        return await database.ScalarAsync<Guid>("SELECT id FROM products ORDER BY id LIMIT 1", Ct);
    }

    [Fact]
    public async Task Fresh_install_creates_the_table_with_the_documented_columns_keys_and_indexes()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);

        Assert.True(await TableExistsAsync(database));
        Assert.Equal("uuid:NO", await ColumnAsync(database, "user_id"));
        Assert.Equal("uuid:NO", await ColumnAsync(database, "product_id"));
        Assert.Equal("timestamp with time zone:NO", await ColumnAsync(database, "created_at"));
        Assert.Equal(
            "pk_customer_favorites:p",
            await database.ScalarAsync<string>($"SELECT conname || ':' || contype::text FROM pg_constraint WHERE conrelid = '{Table}'::regclass AND contype = 'p'", Ct));
        Assert.Equal(
            "PRIMARY KEY (user_id, product_id)",
            await database.ScalarAsync<string>($"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = '{Table}'::regclass AND contype = 'p'", Ct));
        // FK davranışları: kullanıcı silinince CASCADE (c), ürün RESTRICT (r).
        Assert.Equal(
            "fk_customer_favorites_product:r,fk_customer_favorites_user:c",
            await database.ScalarAsync<string>(
                $"SELECT string_agg(conname || ':' || confdeltype::text, ',' ORDER BY conname) FROM pg_constraint WHERE conrelid = '{Table}'::regclass AND contype = 'f'", Ct));
        Assert.Equal(
            "ix_customer_favorites_product_id,ix_customer_favorites_user_id_created_at,pk_customer_favorites",
            await database.ScalarAsync<string>($"SELECT string_agg(indexname, ',' ORDER BY indexname) FROM pg_indexes WHERE tablename = '{Table}'", Ct));
        Assert.Contains(
            "(user_id, created_at DESC)",
            await database.ScalarAsync<string>("SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_customer_favorites_user_id_created_at'", Ct));

        await using var context = database.CreateDbContext();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task The_primary_key_rejects_a_duplicate_pair_and_the_foreign_keys_reject_unknown_parents()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        var user = await InsertUserAsync(database);
        var product = await InsertProductAsync(database);
        var insert = $"INSERT INTO {Table} (user_id, product_id, created_at) VALUES ('{user}', '{product}', now())";
        await database.ExecuteAsync(insert, Ct);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(insert, Ct));
        var noUser = await Assert.ThrowsAsync<PostgresException>(
            () => database.ExecuteAsync($"INSERT INTO {Table} VALUES ('{Guid.NewGuid()}', '{product}', now())", Ct));
        var noProduct = await Assert.ThrowsAsync<PostgresException>(
            () => database.ExecuteAsync($"INSERT INTO {Table} VALUES ('{user}', '{Guid.NewGuid()}', now())", Ct));

        Assert.Equal("23505", duplicate.SqlState);
        Assert.Equal("pk_customer_favorites", duplicate.ConstraintName);
        Assert.Equal("fk_customer_favorites_user", noUser.ConstraintName);
        Assert.Equal("fk_customer_favorites_product", noProduct.ConstraintName);
        Assert.Equal(1, await database.ScalarAsync<long>($"SELECT count(*) FROM {Table}", Ct));
    }

    [Fact]
    public async Task Deleting_the_user_cascades_but_deleting_a_favorited_product_is_restricted()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        var user = await InsertUserAsync(database);
        var product = await InsertProductAsync(database);
        await database.ExecuteAsync($"INSERT INTO {Table} VALUES ('{user}', '{product}', now())", Ct);

        var restricted = await Assert.ThrowsAsync<PostgresException>(
            () => database.ExecuteAsync($"DELETE FROM product_variants WHERE product_id = '{product}'; DELETE FROM products WHERE id = '{product}'", Ct));
        Assert.Equal("23503", restricted.SqlState);
        Assert.Equal(1, await database.ScalarAsync<long>($"SELECT count(*) FROM {Table}", Ct));

        await database.ExecuteAsync($"DELETE FROM \"AspNetUsers\" WHERE \"Id\" = '{user}'", Ct);
        Assert.Equal(0, await database.ScalarAsync<long>($"SELECT count(*) FROM {Table}", Ct));
    }

    [Fact]
    public async Task Upgrading_a_populated_previous_database_keeps_every_existing_row_and_starts_with_an_empty_table()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await MigrateToAsync(database, PreviousMigration);
        await LegacySchemaSeed.SeedDemoCatalogAsync(database, Ct);
        var user = await InsertUserAsync(database);
        var before = await FingerprintAsync(database);
        Assert.False(await TableExistsAsync(database));
        await using (var context = database.CreateDbContext())
        {
            var only = Assert.Single(await context.Database.GetPendingMigrationsAsync(Ct));
            Assert.EndsWith("_AddCustomerFavorites", only);
        }

        await MigrateToAsync(database, targetMigration: null);

        Assert.True(await TableExistsAsync(database));
        Assert.Equal(0, await database.ScalarAsync<long>($"SELECT count(*) FROM {Table}", Ct));
        Assert.Equal(before, await FingerprintAsync(database)); // mevcut hiçbir tablonun tek satırı değişmedi
        Assert.Equal(1, await database.ScalarAsync<long>($"SELECT count(*) FROM \"AspNetUsers\" WHERE \"Id\" = '{user}'", Ct));
    }

    [Fact]
    public async Task A_second_migrate_is_a_noop_and_rolling_back_drops_only_the_new_table_and_reapplying_works()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        var tablesWithout = await database.ScalarAsync<long>(
            $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> '{Table}'", Ct);
        await using (var context = database.CreateDbContext())
        {
            await context.Database.MigrateAsync(Ct); // ikinci çağrı sessiz
        }

        await MigrateToAsync(database, PreviousMigration);

        Assert.False(await TableExistsAsync(database));
        Assert.Equal(tablesWithout, await database.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", Ct));

        await MigrateToAsync(database, targetMigration: null);
        Assert.True(await TableExistsAsync(database));
    }

    [Fact]
    public async Task Sql_audit_the_up_script_only_creates_the_table_and_its_indexes()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        string script;
        await using (var context = database.CreateDbContext())
        {
            var migrations = context.Database.GetMigrations().ToArray();
            var previous = migrations.Single(m => m.EndsWith("_" + PreviousMigration, StringComparison.Ordinal));
            var favorites = migrations.Single(m => m.EndsWith("_AddCustomerFavorites", StringComparison.Ordinal));
            script = context.GetService<IMigrator>().GenerateScript(previous, favorites);
        }

        var statements = script.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)).ToArray();

        Assert.DoesNotContain(statements, l => DropRegex().IsMatch(l));                                  // yıkıcı işlem yok
        Assert.DoesNotContain(statements, l => l.StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase)); // mevcut tablolara dokunulmaz
        Assert.DoesNotContain(
            statements,
            l => Regex.IsMatch(l, @"^(DELETE FROM|TRUNCATE|UPDATE\s+\S+\s+SET|INSERT INTO\s+(?!""__EFMigrationsHistory"")\S+)", RegexOptions.IgnoreCase));
        Assert.Single(statements, l => l.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, statements.Count(l => l.StartsWith("CREATE INDEX", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Mevcut tabloların (geçmiş ve yeni tablo hariç) içerik parmak izi.</summary>
    private static async Task<string> FingerprintAsync(PostgresTestDatabase database)
    {
        var tables = new List<string>();
        await using (var connection = await database.OpenConnectionAsync(Ct))
        await using (var command = new NpgsqlCommand(
            $"SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' AND table_name NOT IN ('__EFMigrationsHistory', '{Table}') ORDER BY table_name COLLATE \"C\"",
            connection))
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var parts = new List<string>();
        foreach (var table in tables)
        {
            parts.Add(table + "=" + await database.ScalarAsync<string>(
                $"SELECT md5(coalesce(string_agg(r, '|' ORDER BY r), '')) || ':' || count(*) FROM (SELECT ROW(t.*)::text AS r FROM \"{table}\" t) q", Ct));
        }

        return string.Join(";", parts);
    }
}

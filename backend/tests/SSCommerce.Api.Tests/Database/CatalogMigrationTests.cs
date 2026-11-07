using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// Migration'ın sıfırdan, tekrar ve geri alınarak uygulanabildiğini kanıtlar. Her test KENDİ izole veritabanını açar
/// (şema üzerinde oynadıkları için paylaşımlı veritabanı kullanmazlar).
/// </summary>
public class CatalogMigrationTests
{
    private static readonly string[] CatalogTables = ["categories", "product_images", "product_variants", "products"];
    private static readonly string[] IdentityTables =
        ["AspNetRoleClaims", "AspNetRoles", "AspNetUserClaims", "AspNetUserLogins", "AspNetUserRoles", "AspNetUsers", "AspNetUserTokens"];

    private static readonly string[] InventoryTables = ["inventory_balances", "stock_movements", "warehouses"];
    private static readonly string[] CartAddressTables = ["cart_items", "carts", "customer_addresses"];
    private static readonly string[] OrderTables = ["order_inventory_allocations", "order_items", "order_status_history", "orders"];
    private static readonly string[] AdminTables = ["admin_audit_log"];

    // Tüm migration'lar uygulanmış şema: geçmiş tablosu + Catalog (InitialCatalog) + Identity (AddIdentity) + Inventory (AddInventory) + Cart/Addresses (AddCartAndAddresses) + Ordering (AddOrdering) + Admin (AddAdminOperations). Sıralama Ordinal.
    private static readonly string[] AllTables =
        ["__EFMigrationsHistory", .. CatalogTables, .. IdentityTables, .. InventoryTables, .. CartAddressTables, .. OrderTables, .. AdminTables];

    private static async Task<string[]> PublicTablesAsync(PostgresTestDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name COLLATE \"C\"",
            connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var tables = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        return [.. tables.Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task Fresh_database_gets_history_catalog_and_identity_tables()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        Assert.Empty(await PublicTablesAsync(database));

        await using (var context = database.CreateDbContext())
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        var tables = await PublicTablesAsync(database);
        Assert.Equal(AllTables.Order(StringComparer.Ordinal), tables);

        var applied = await database.ScalarAsync<string>(
            "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" LIMIT 1",
            TestContext.Current.CancellationToken);
        Assert.EndsWith("_InitialCatalog", applied);
    }

    [Fact]
    public async Task Second_migrate_is_a_noop()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);

        await using var context = database.CreateDbContext();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));

        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var historyRows = await database.ScalarAsync<long>(
            "SELECT count(*) FROM \"__EFMigrationsHistory\"",
            TestContext.Current.CancellationToken);
        Assert.Equal(6, historyRows); // InitialCatalog + AddIdentity + AddInventory + AddCartAndAddresses + AddOrdering + AddAdminOperations
        Assert.Equal(AllTables.Order(StringComparer.Ordinal), await PublicTablesAsync(database));
    }

    [Fact]
    public async Task Model_and_latest_migration_snapshot_are_in_sync()
    {
        // "Modeli değiştirdim ama migration eklemeyi unuttum" hatasını CI'da yakalar.
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await using var context = database.CreateDbContext();

        Assert.False(context.Database.HasPendingModelChanges(), "Model değişti ama migration eklenmedi (dotnet ef migrations add).");
    }

    [Fact]
    public async Task Migration_can_be_rolled_back_to_empty_and_reapplied()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync("0", TestContext.Current.CancellationToken);
        Assert.DoesNotContain(await PublicTablesAsync(database), t => CatalogTables.Contains(t));

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(AllTables.Order(StringComparer.Ordinal), await PublicTablesAsync(database));
    }

    [Fact]
    public async Task Idempotent_script_can_run_twice_on_an_empty_database_and_on_a_migrated_one()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await using var context = database.CreateDbContext();
        var script = context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);

        await database.ExecuteAsync(script, TestContext.Current.CancellationToken);
        Assert.Equal(AllTables.Order(StringComparer.Ordinal), await PublicTablesAsync(database));

        // İkinci çalıştırma hata vermemeli ve satır/tablo çoğaltmamalı.
        await database.ExecuteAsync(script, TestContext.Current.CancellationToken);
        Assert.Equal(AllTables.Order(StringComparer.Ordinal), await PublicTablesAsync(database));
        Assert.Equal(6, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", TestContext.Current.CancellationToken));

        // EF ile migrate edilmiş veritabanında da script zararsızdır.
        await using var migrated = await PostgresTestDatabase.CreateAsync(migrate: true);
        await migrated.ExecuteAsync(script, TestContext.Current.CancellationToken);
        Assert.Equal(6, await migrated.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Column_types_follow_the_data_contract()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);

        // Para: numeric(18,2). Zaman: yalnızca timestamptz. ID: uuid. jsonb alanları.
        var price = await database.ScalarAsync<string>(
            "SELECT data_type || '(' || numeric_precision || ',' || numeric_scale || ')' FROM information_schema.columns " +
            "WHERE table_name = 'product_variants' AND column_name = 'price'",
            TestContext.Current.CancellationToken);
        Assert.Equal("numeric(18,2)", price);

        var currency = await database.ScalarAsync<string>(
            "SELECT udt_name || '(' || character_maximum_length || ')' FROM information_schema.columns " +
            "WHERE table_name = 'product_variants' AND column_name = 'currency'",
            TestContext.Current.CancellationToken);
        Assert.Equal("bpchar(3)", currency);

        var nonTimestamptz = await database.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' " +
            "AND column_name IN ('created_at', 'updated_at') AND data_type <> 'timestamp with time zone'",
            TestContext.Current.CancellationToken);
        Assert.Equal(0, nonTimestamptz);

        var timestampColumns = await database.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND column_name IN ('created_at', 'updated_at')",
            TestContext.Current.CancellationToken);
        Assert.Equal(20, timestampColumns); // 4 katalog + warehouses + inventory_balances + carts + cart_items + customer_addresses + orders (stock_movements.occurred_at sayılmaz; değişmez sipariş tablolarında created_at/updated_at yok)

        var nonUuidIds = await database.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name IN ('categories','products','product_variants','product_images') " +
            "AND column_name IN ('id','parent_id','category_id','product_id') AND data_type <> 'uuid'",
            TestContext.Current.CancellationToken);
        Assert.Equal(0, nonUuidIds);

        var jsonbColumns = await database.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND data_type = 'jsonb'",
            TestContext.Current.CancellationToken);
        Assert.Equal(3, jsonbColumns); // products.specifications, product_variants.options, admin_audit_log.changes
    }
}

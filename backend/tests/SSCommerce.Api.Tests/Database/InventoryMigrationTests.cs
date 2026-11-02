using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SSCommerce.Infrastructure.Persistence.Seed;

namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// Faz 5: <c>AddInventory</c> migration'ı (D-045..D-048). Yalnızca EKLEMELİDİR: mevcut Catalog/Identity tabloları ve verisi değişmez.
/// Şema üzerinde oynadıkları için her test KENDİ izole veritabanını açar.
/// </summary>
public class InventoryMigrationTests
{
    private const string IdentityMigration = "AddIdentity";
    private const string InventoryMigration = "AddInventory";
    private static readonly string[] InventoryTables = ["inventory_balances", "stock_movements", "warehouses"];

    private static async Task MigrateToAsync(PostgresTestDatabase database, string? targetMigration)
    {
        await using var context = database.CreateDbContext();
        await context.GetService<IMigrator>().MigrateAsync(targetMigration, TestContext.Current.CancellationToken);
    }

    private static async Task<long> TableCountAsync(PostgresTestDatabase database, params string[] tables)
    {
        var list = string.Join(", ", tables.Select(t => $"'{t}'"));
        return await database.ScalarAsync<long>(
            $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name IN ({list})",
            TestContext.Current.CancellationToken);
    }

    /// <summary>Eski tabloların içeriğinin parmak izi: katalog + kullanıcı + roller.</summary>
    private static async Task<string> ExistingDataChecksumAsync(PostgresTestDatabase database) =>
        (await database.ScalarAsync<string>(
            """
            SELECT md5(
                coalesce((SELECT string_agg(id::text || slug || name || is_demo::text, ',' ORDER BY id) FROM products), '') || '|' ||
                coalesce((SELECT string_agg(id::text || sku || price::text || is_active::text, ',' ORDER BY id) FROM product_variants), '') || '|' ||
                coalesce((SELECT string_agg(id::text || slug, ',' ORDER BY id) FROM categories), '') || '|' ||
                coalesce((SELECT string_agg("Id"::text || "Email" || "NormalizedEmail", ',' ORDER BY "Id") FROM "AspNetUsers"), '') || '|' ||
                coalesce((SELECT string_agg("Id"::text || "Name", ',' ORDER BY "Id") FROM "AspNetRoles"), ''))
            """,
            TestContext.Current.CancellationToken))!;

    private static async Task InsertUserAsync(PostgresTestDatabase database)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        await database.ExecuteAsync(
            $"""
            INSERT INTO "AspNetUsers" ("Id", "CreatedAt", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed",
                "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES ('{Guid.NewGuid()}', now(), 'u{suffix}', 'U{suffix}', 'u{suffix}@ornek.test', 'U{suffix}@ORNEK.TEST', false, false, false, true, 0)
            """,
            TestContext.Current.CancellationToken);
    }

    private static async Task<long> InventoryTriggerCountAsync(PostgresTestDatabase database) =>
        await database.ScalarAsync<long>(
            "SELECT count(*) FROM pg_trigger WHERE tgrelid = 'stock_movements'::regclass AND NOT tgisinternal",
            TestContext.Current.CancellationToken);

    private static async Task<long> FunctionCountAsync(PostgresTestDatabase database) =>
        await database.ScalarAsync<long>(
            "SELECT count(*) FROM pg_proc WHERE proname = 'stock_movements_reject_change'",
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Fresh_install_applies_all_migrations_and_creates_the_inventory_objects()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);

        await using (var context = database.CreateDbContext())
        {
            await context.Database.MigrateAsync(ct);
        }

        Assert.Equal(3, await TableCountAsync(database, InventoryTables));
        var history = (await database.ScalarAsync<string>(
            "SELECT string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\") FROM \"__EFMigrationsHistory\"", ct))!.Split(',');
        Assert.Equal(5, history.Length);
        Assert.EndsWith("_InitialCatalog", history[0]);
        Assert.EndsWith("_AddIdentity", history[1]);
        Assert.EndsWith("_AddInventory", history[2]);
        Assert.EndsWith("_AddCartAndAddresses", history[3]);
        Assert.EndsWith("_AddOrdering", history[4]);

        Assert.Equal(2, await InventoryTriggerCountAsync(database));
        Assert.Equal(1, await FunctionCountAsync(database));
        // Yeni tablolar boş: stok seed edilmez (D-047).
        foreach (var table in InventoryTables)
        {
            Assert.Equal(0, await database.ScalarAsync<long>($"SELECT count(*) FROM {table}", ct));
        }
    }

    [Fact]
    public async Task Upgrading_a_seeded_identity_database_keeps_existing_data_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await MigrateToAsync(database, IdentityMigration);
        Assert.Equal(0, await TableCountAsync(database, InventoryTables));

        await using (var context = database.CreateDbContext())
        {
            await new DemoCatalogSeeder(context).SeedAsync(ct);
        }

        await InsertUserAsync(database);
        var before = await ExistingDataChecksumAsync(database);
        var variantsBefore = await database.ScalarAsync<long>("SELECT count(*) FROM product_variants", ct);
        Assert.True(variantsBefore > 0);
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM \"AspNetUsers\"", ct));

        await using (var context = database.CreateDbContext())
        {
            var pending = (await context.Database.GetPendingMigrationsAsync(ct)).ToArray();
            Assert.Equal(3, pending.Length); // AddInventory, ardından Faz 6'nın AddCartAndAddresses'i ve Faz 7'nin AddOrdering'i
            Assert.EndsWith("_AddInventory", pending[0]);
        }

        await MigrateToAsync(database, InventoryMigration);

        Assert.Equal(before, await ExistingDataChecksumAsync(database));
        Assert.Equal(variantsBefore, await database.ScalarAsync<long>("SELECT count(*) FROM product_variants", ct));
        Assert.Equal(3, await TableCountAsync(database, InventoryTables));
        Assert.Equal(2, await InventoryTriggerCountAsync(database));
    }

    [Fact]
    public async Task A_second_migrate_is_a_noop()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        var checksum = await ExistingDataChecksumAsync(database);

        await using (var context = database.CreateDbContext())
        {
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(ct));
            await context.Database.MigrateAsync(ct);
        }

        Assert.Equal(5, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", ct));
        Assert.Equal(3, await TableCountAsync(database, InventoryTables));
        Assert.Equal(2, await InventoryTriggerCountAsync(database));
        Assert.Equal(checksum, await ExistingDataChecksumAsync(database));
    }

    [Fact]
    public async Task Rolling_back_to_identity_removes_inventory_objects_keeps_old_data_and_reapplying_works()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        await using (var context = database.CreateDbContext())
        {
            await new DemoCatalogSeeder(context).SeedAsync(ct);
        }

        await InsertUserAsync(database);
        var before = await ExistingDataChecksumAsync(database);

        await MigrateToAsync(database, IdentityMigration);

        Assert.Equal(0, await TableCountAsync(database, InventoryTables));
        Assert.Equal(0, await FunctionCountAsync(database)); // trigger fonksiyonu da kalkmış olmalı
        Assert.Equal(2, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", ct));
        Assert.Equal(before, await ExistingDataChecksumAsync(database));

        await MigrateToAsync(database, targetMigration: null);

        Assert.Equal(3, await TableCountAsync(database, InventoryTables));
        Assert.Equal(2, await InventoryTriggerCountAsync(database));
        Assert.Equal(1, await FunctionCountAsync(database));
        Assert.Equal(5, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", ct));
        Assert.Equal(before, await ExistingDataChecksumAsync(database));
    }

    [Fact]
    public async Task Idempotent_script_for_the_inventory_step_upgrades_an_identity_database_and_is_safe_to_rerun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await MigrateToAsync(database, IdentityMigration);
        await InsertUserAsync(database);
        var before = await ExistingDataChecksumAsync(database);

        string script;
        await using (var context = database.CreateDbContext())
        {
            script = context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        }

        await database.ExecuteAsync(script, ct);
        await database.ExecuteAsync(script, ct);

        Assert.Equal(3, await TableCountAsync(database, InventoryTables));
        Assert.Equal(2, await InventoryTriggerCountAsync(database));
        Assert.Equal(5, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", ct));
        Assert.Equal(before, await ExistingDataChecksumAsync(database));
    }

    [Fact]
    public async Task Inventory_columns_and_objects_follow_the_data_contract()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);

        async Task<string?> ColumnAsync(string table, string column) => await database.ScalarAsync<string>(
            $"SELECT data_type || coalesce('(' || character_maximum_length || ')', '') || ':' || is_nullable FROM information_schema.columns " +
            $"WHERE table_schema = 'public' AND table_name = '{table}' AND column_name = '{column}'",
            ct);

        Assert.Equal("uuid:NO", await ColumnAsync("inventory_balances", "id"));
        Assert.Equal("integer:NO", await ColumnAsync("inventory_balances", "on_hand_quantity"));
        Assert.Equal("bigint:NO", await ColumnAsync("inventory_balances", "version"));
        Assert.Equal("timestamp with time zone:NO", await ColumnAsync("inventory_balances", "created_at"));
        Assert.Equal("timestamp with time zone:NO", await ColumnAsync("warehouses", "updated_at"));
        Assert.Equal("character varying(32):NO", await ColumnAsync("warehouses", "code"));
        Assert.Equal("character varying(100):NO", await ColumnAsync("warehouses", "name"));
        Assert.Equal("integer:NO", await ColumnAsync("stock_movements", "quantity_delta"));
        Assert.Equal("character varying(32):NO", await ColumnAsync("stock_movements", "operation_type"));
        Assert.Equal("character(64):NO", await ColumnAsync("stock_movements", "request_fingerprint"));
        Assert.Equal("character varying(128):NO", await ColumnAsync("stock_movements", "operation_key"));
        Assert.Equal("character varying(128):YES", await ColumnAsync("stock_movements", "reference"));
        Assert.Equal("character varying(500):NO", await ColumnAsync("stock_movements", "reason"));
        Assert.Equal("timestamp with time zone:NO", await ColumnAsync("stock_movements", "occurred_at"));
        Assert.Equal("uuid:NO", await ColumnAsync("stock_movements", "actor_user_id"));
        // Hareket tablosunda updated_at yoktur (değişmez kayıt).
        Assert.Null(await ColumnAsync("stock_movements", "updated_at"));

        var indexes = await database.ScalarAsync<string>(
            """
            SELECT string_agg(indexname, ',' ORDER BY indexname) FROM pg_indexes
            WHERE schemaname = 'public' AND tablename IN ('warehouses', 'inventory_balances', 'stock_movements')
            """,
            ct);
        Assert.Equal(
            "ix_inventory_balances_product_variant_id,ix_stock_movements_actor_user_id,ix_stock_movements_product_variant_id_occurred_at," +
            "ix_stock_movements_warehouse_id_occurred_at,pk_inventory_balances,pk_stock_movements,pk_warehouses," +
            "ux_inventory_balances_warehouse_id_product_variant_id,ux_stock_movements_operation_key,ux_warehouses_code",
            indexes);

        var checks = await database.ScalarAsync<long>(
            "SELECT count(*) FROM pg_constraint WHERE contype = 'c' AND conrelid::regclass::text IN ('warehouses', 'inventory_balances', 'stock_movements')",
            ct);
        Assert.Equal(13, checks);

        var triggers = await database.ScalarAsync<string>(
            """
            SELECT string_agg(tgname || ':' || (tgtype & 1) || ':' || (tgtype & 32), ',' ORDER BY tgname) FROM pg_trigger
            WHERE tgrelid = 'stock_movements'::regclass AND NOT tgisinternal
            """,
            ct);
        // tgtype bit 1 = satır düzeyi, bit 32 = TRUNCATE. update/delete: satır (1), truncate değil; truncate: deyim (0), TRUNCATE (32).
        Assert.Equal("trg_stock_movements_no_truncate:0:32,trg_stock_movements_no_update_delete:1:0", triggers);
    }

    [Fact]
    public async Task Existing_tables_have_no_new_columns_or_constraints_after_the_inventory_migration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await MigrateToAsync(database, IdentityMigration);

        const string SchemaFingerprintSql =
            """
            SELECT md5(
                (SELECT string_agg(table_name || '.' || column_name || ':' || data_type || ':' || is_nullable, ',' ORDER BY table_name, column_name)
                 FROM information_schema.columns
                 WHERE table_schema = 'public' AND table_name NOT IN ('warehouses', 'inventory_balances', 'stock_movements')) || '|' ||
                (SELECT string_agg(conrelid::regclass::text || '.' || conname, ',' ORDER BY conrelid::regclass::text, conname)
                 FROM pg_constraint WHERE connamespace = 'public'::regnamespace
                   AND conrelid::regclass::text NOT IN ('warehouses', 'inventory_balances', 'stock_movements')) || '|' ||
                (SELECT string_agg(indexname, ',' ORDER BY indexname) FROM pg_indexes
                 WHERE schemaname = 'public' AND tablename NOT IN ('warehouses', 'inventory_balances', 'stock_movements')))
            """;

        var before = await database.ScalarAsync<string>(SchemaFingerprintSql, ct);
        await MigrateToAsync(database, InventoryMigration);
        Assert.Equal(before, await database.ScalarAsync<string>(SchemaFingerprintSql, ct));
    }
}

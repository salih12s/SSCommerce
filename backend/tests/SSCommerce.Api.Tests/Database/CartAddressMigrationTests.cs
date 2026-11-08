using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SSCommerce.Infrastructure.Persistence.Seed;

namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// Faz 6: <c>AddCartAndAddresses</c> migration'ı (D-049..D-052). Yalnızca EKLEMELİDİR: Catalog, Identity ve Inventory tabloları, verisi ve
/// stock_movements trigger'ları değişmez. Şema üzerinde oynadıkları için her test KENDİ izole veritabanını açar.
/// </summary>
public class CartAddressMigrationTests
{
    private const string InventoryMigration = "AddInventory";
    private static readonly string[] CartAddressTables = ["cart_items", "carts", "customer_addresses"];
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

    /// <summary>Eski tabloların içeriğinin parmak izi: katalog + kullanıcı + roller + stok verisi.</summary>
    private static async Task<string> ExistingDataChecksumAsync(PostgresTestDatabase database) =>
        (await database.ScalarAsync<string>(
            """
            SELECT md5(
                coalesce((SELECT string_agg(id::text || slug || name || is_demo::text, ',' ORDER BY id) FROM products), '') || '|' ||
                coalesce((SELECT string_agg(id::text || sku || price::text || is_active::text, ',' ORDER BY id) FROM product_variants), '') || '|' ||
                coalesce((SELECT string_agg("Id"::text || "Email" || "NormalizedEmail", ',' ORDER BY "Id") FROM "AspNetUsers"), '') || '|' ||
                coalesce((SELECT string_agg("Id"::text || "Name", ',' ORDER BY "Id") FROM "AspNetRoles"), '') || '|' ||
                coalesce((SELECT string_agg(id::text || code, ',' ORDER BY id) FROM warehouses), '') || '|' ||
                coalesce((SELECT string_agg(id::text || on_hand_quantity::text || version::text, ',' ORDER BY id) FROM inventory_balances), '') || '|' ||
                coalesce((SELECT string_agg(id::text || operation_key || on_hand_after::text, ',' ORDER BY id) FROM stock_movements), ''))
            """,
            TestContext.Current.CancellationToken))!;

    /// <summary>Katalog + kullanıcı + depo + bakiye + hareket verisini ham SQL ile kurar (Inventory sürümünde var olan tablolar).</summary>
    private static async Task SeedExistingDataAsync(PostgresTestDatabase database)
    {
        var ct = TestContext.Current.CancellationToken;
        await LegacySchemaSeed.SeedDemoCatalogAsync(database, ct);

        var userId = Guid.NewGuid();
        var warehouseId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        await database.ExecuteAsync(
            $"""
            INSERT INTO "AspNetUsers" ("Id", "CreatedAt", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed",
                "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES ('{userId}', now(), 'u{suffix}', 'U{suffix}', 'u{suffix}@ornek.test', 'U{suffix}@ORNEK.TEST', false, false, false, true, 0)
            """,
            ct);
        await database.ExecuteAsync(
            $"INSERT INTO warehouses (id, code, name, is_active, created_at, updated_at) VALUES ('{warehouseId}', 'DEPO-{suffix}', 'Test depo', true, now(), now())", ct);
        await database.ExecuteAsync(
            $"""
            INSERT INTO inventory_balances (id, warehouse_id, product_variant_id, on_hand_quantity, version, created_at, updated_at)
            SELECT '{Guid.NewGuid()}', '{warehouseId}', id, 7, 1, now(), now() FROM product_variants ORDER BY sku LIMIT 1
            """,
            ct);
        await database.ExecuteAsync(
            $"""
            INSERT INTO stock_movements (id, warehouse_id, product_variant_id, sku, quantity_delta, operation_type, actor_user_id, reason, reference,
                operation_key, request_fingerprint, on_hand_after, occurred_at)
            SELECT '{Guid.NewGuid()}', '{warehouseId}', id, sku, 7, 'receiving', '{userId}', 'Test girişi', NULL,
                'migration-key-{suffix}-0123456789', '{new string('b', 64)}', 7, now()
            FROM product_variants ORDER BY sku LIMIT 1
            """,
            ct);
    }

    private static async Task<long> StockTriggerCountAsync(PostgresTestDatabase database) =>
        await database.ScalarAsync<long>(
            "SELECT count(*) FROM pg_trigger WHERE tgrelid = 'stock_movements'::regclass AND NOT tgisinternal",
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Fresh_install_creates_the_cart_and_address_tables_last()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);

        await using (var context = database.CreateDbContext())
        {
            await context.Database.MigrateAsync(ct);
        }

        Assert.Equal(3, await TableCountAsync(database, CartAddressTables));
        var history = (await database.ScalarAsync<string>(
            "SELECT string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\") FROM \"__EFMigrationsHistory\"", ct))!.Split(',');
        Assert.Equal(7, history.Length);
        Assert.EndsWith("_AddInventory", history[2]);
        Assert.EndsWith("_AddCartAndAddresses", history[3]);
        Assert.EndsWith("_AddOrdering", history[4]);
        Assert.EndsWith("_AddAdminOperations", history[5]);
        Assert.EndsWith("_AddCustomerFavorites", history[6]);
        foreach (var table in CartAddressTables)
        {
            Assert.Equal(0, await database.ScalarAsync<long>($"SELECT count(*) FROM {table}", ct));
        }
    }

    [Fact]
    public async Task Upgrading_an_inventory_database_keeps_catalog_user_and_stock_data_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await MigrateToAsync(database, InventoryMigration);
        Assert.Equal(0, await TableCountAsync(database, CartAddressTables));
        await SeedExistingDataAsync(database);

        var before = await ExistingDataChecksumAsync(database);
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM stock_movements", ct));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM inventory_balances", ct));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM \"AspNetUsers\"", ct));

        await using (var context = database.CreateDbContext())
        {
            var pending = (await context.Database.GetPendingMigrationsAsync(ct)).ToArray();
            Assert.Equal(4, pending.Length); // AddCartAndAddresses, ardından Faz 7'nin AddOrdering'i, Faz 8'in AddAdminOperations'ı ve MD-1'in AddCustomerFavorites'ı
            Assert.EndsWith("_AddCartAndAddresses", pending[0]);
        }

        await MigrateToAsync(database, targetMigration: null);

        Assert.Equal(before, await ExistingDataChecksumAsync(database));
        Assert.Equal(3, await TableCountAsync(database, CartAddressTables));
        Assert.Equal(3, await TableCountAsync(database, InventoryTables));
        // Stok değişmezliği korunuyor.
        Assert.Equal(2, await StockTriggerCountAsync(database));
    }

    [Fact]
    public async Task A_second_migrate_is_a_noop()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);

        await using (var context = database.CreateDbContext())
        {
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(ct));
            await context.Database.MigrateAsync(ct);
        }

        Assert.Equal(7, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", ct));
        Assert.Equal(3, await TableCountAsync(database, CartAddressTables));
    }

    [Fact]
    public async Task Rolling_back_to_inventory_removes_only_cart_objects_keeps_data_and_reapplying_works()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        await SeedExistingDataAsync(database);
        var before = await ExistingDataChecksumAsync(database);

        await MigrateToAsync(database, InventoryMigration);

        Assert.Equal(0, await TableCountAsync(database, CartAddressTables));
        Assert.Equal(3, await TableCountAsync(database, InventoryTables));
        Assert.Equal(3, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", ct));
        Assert.Equal(2, await StockTriggerCountAsync(database));
        Assert.Equal(before, await ExistingDataChecksumAsync(database));

        await MigrateToAsync(database, targetMigration: null);

        Assert.Equal(3, await TableCountAsync(database, CartAddressTables));
        Assert.Equal(7, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", ct));
        Assert.Equal(before, await ExistingDataChecksumAsync(database));
    }

    [Fact]
    public async Task Idempotent_script_is_safe_to_rerun_on_an_inventory_database()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await MigrateToAsync(database, InventoryMigration);
        await SeedExistingDataAsync(database);
        var before = await ExistingDataChecksumAsync(database);

        string script;
        await using (var context = database.CreateDbContext())
        {
            script = context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        }

        await database.ExecuteAsync(script, ct);
        await database.ExecuteAsync(script, ct);

        Assert.Equal(3, await TableCountAsync(database, CartAddressTables));
        Assert.Equal(7, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", ct));
        Assert.Equal(before, await ExistingDataChecksumAsync(database));
    }

    [Fact]
    public async Task Existing_tables_get_no_new_columns_constraints_or_indexes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await MigrateToAsync(database, InventoryMigration);

        const string SchemaFingerprintSql =
            """
            SELECT md5(
                (SELECT string_agg(table_name || '.' || column_name || ':' || data_type || ':' || is_nullable, ',' ORDER BY table_name, column_name)
                 FROM information_schema.columns
                 WHERE table_schema = 'public' AND table_name NOT IN ('cart_items', 'carts', 'customer_addresses')) || '|' ||
                (SELECT string_agg(conrelid::regclass::text || '.' || conname, ',' ORDER BY conrelid::regclass::text, conname)
                 FROM pg_constraint WHERE connamespace = 'public'::regnamespace
                   AND conrelid::regclass::text NOT IN ('cart_items', 'carts', 'customer_addresses')) || '|' ||
                (SELECT string_agg(indexname, ',' ORDER BY indexname) FROM pg_indexes
                 WHERE schemaname = 'public' AND tablename NOT IN ('cart_items', 'carts', 'customer_addresses')))
            """;

        var before = await database.ScalarAsync<string>(SchemaFingerprintSql, ct);
        await MigrateToAsync(database, "AddCartAndAddresses"); // sonraki AddOrdering kendi testinde (OrderingMigrationTests) denetlenir
        Assert.Equal(before, await database.ScalarAsync<string>(SchemaFingerprintSql, ct));
    }

    [Fact]
    public async Task Cart_and_address_columns_and_objects_follow_the_data_contract()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);

        async Task<string?> ColumnAsync(string table, string column) => await database.ScalarAsync<string>(
            $"SELECT data_type || coalesce('(' || character_maximum_length || ')', '') || ':' || is_nullable FROM information_schema.columns " +
            $"WHERE table_schema = 'public' AND table_name = '{table}' AND column_name = '{column}'",
            ct);

        Assert.Equal("uuid:NO", await ColumnAsync("carts", "user_id"));
        Assert.Equal("integer:NO", await ColumnAsync("cart_items", "quantity"));
        Assert.Equal("uuid:NO", await ColumnAsync("cart_items", "product_variant_id"));
        Assert.Equal(
            "numeric(18,2)",
            await database.ScalarAsync<string>(
                "SELECT data_type || '(' || numeric_precision || ',' || numeric_scale || ')' FROM information_schema.columns " +
                "WHERE table_name = 'cart_items' AND column_name = 'unit_price_at_add'",
                ct));
        Assert.Equal("character(10):NO", await ColumnAsync("customer_addresses", "phone"));
        Assert.Equal("character(5):YES", await ColumnAsync("customer_addresses", "postal_code"));
        Assert.Equal("character varying(50):NO", await ColumnAsync("customer_addresses", "label"));
        Assert.Equal("character varying(100):NO", await ColumnAsync("customer_addresses", "recipient_name"));
        Assert.Equal("character varying(32):NO", await ColumnAsync("customer_addresses", "province"));
        Assert.Equal("character varying(64):NO", await ColumnAsync("customer_addresses", "district"));
        Assert.Equal("character varying(300):NO", await ColumnAsync("customer_addresses", "street_address"));
        Assert.Equal("boolean:NO", await ColumnAsync("customer_addresses", "is_default"));
        Assert.Equal("timestamp with time zone:NO", await ColumnAsync("customer_addresses", "updated_at"));
        // Sepet fiyat ya da stok taşımaz.
        Assert.Equal(0, await database.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'carts' AND column_name NOT IN ('id', 'user_id', 'created_at', 'updated_at')", ct));

        // FK silme davranışı: kullanıcı ve sepet CASCADE, varyant RESTRICT.
        Assert.Equal(
            "fk_cart_items_cart:c,fk_cart_items_product_variant:r,fk_carts_user:c,fk_customer_addresses_user:c",
            await database.ScalarAsync<string>(
                """
                SELECT string_agg(conname || ':' || confdeltype::text, ',' ORDER BY conname) FROM pg_constraint
                WHERE contype = 'f' AND conrelid::regclass::text IN ('carts', 'cart_items', 'customer_addresses')
                """,
                ct));

        Assert.Equal(
            "ix_cart_items_product_variant_id,ix_customer_addresses_user_id,pk_cart_items,pk_carts,pk_customer_addresses," +
            "ux_cart_items_cart_id_product_variant_id,ux_carts_user_id,ux_customer_addresses_user_id_default",
            await database.ScalarAsync<string>(
                "SELECT string_agg(indexname, ',' ORDER BY indexname) FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ('carts', 'cart_items', 'customer_addresses')",
                ct));
        Assert.Contains("WHERE is_default", (await database.ScalarAsync<string>(
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'ux_customer_addresses_user_id_default'", ct))!);
        Assert.Equal(
            9,
            await database.ScalarAsync<long>(
                "SELECT count(*) FROM pg_constraint WHERE contype = 'c' AND conrelid::regclass::text IN ('carts', 'cart_items', 'customer_addresses')", ct));
    }
}

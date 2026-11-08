using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SSCommerce.Infrastructure.Persistence.Seed;

namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// Faz 7: <c>AddOrdering</c> migration'ı (D-059..D-064). Tek ileri-yönlü EKLEMELİ migration'dır; mevcut tabloların verisi ve şeması değişmez,
/// tek istisna <c>ck_stock_movements_operation_type</c> CHECK'inin yeniden kurulmasıdır. Şema üzerinde oynadıkları için her test KENDİ izole
/// veritabanını açar.
/// </summary>
public partial class OrderingMigrationTests
{
    private const string PreviousMigration = "AddCartAndAddresses";
    private static readonly string[] OrderTables = ["order_inventory_allocations", "order_items", "order_status_history", "orders"];
    private static readonly string[] OrderFunctions = ["order_records_reject_change", "orders_restrict_change"];

    [GeneratedRegex(@"\bDROP\b", RegexOptions.IgnoreCase)]
    private static partial Regex DropRegex();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task MigrateToAsync(PostgresTestDatabase database, string? targetMigration)
    {
        await using var context = database.CreateDbContext();
        await context.GetService<IMigrator>().MigrateAsync(targetMigration, Ct);
    }

    private static async Task<long> TableCountAsync(PostgresTestDatabase database, params string[] tables)
    {
        var list = string.Join(", ", tables.Select(t => $"'{t}'"));
        return await database.ScalarAsync<long>(
            $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name IN ({list})", Ct);
    }

    private static async Task<long> OrderFunctionCountAsync(PostgresTestDatabase database) =>
        await database.ScalarAsync<long>(
            "SELECT count(*) FROM pg_proc WHERE proname IN ('order_records_reject_change', 'orders_restrict_change')", Ct);

    private static async Task<long> TriggerCountAsync(PostgresTestDatabase database, string table) =>
        await database.ScalarAsync<long>($"SELECT count(*) FROM pg_trigger WHERE tgrelid = '{table}'::regclass AND NOT tgisinternal", Ct);

    private static async Task<string> OperationTypeCheckAsync(PostgresTestDatabase database) =>
        (await database.ScalarAsync<string>(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_stock_movements_operation_type'", Ct))!;

    /// <summary>Eski tabloların içeriğinin parmak izi: katalog + kullanıcı + roller + stok + sepet + adres.</summary>
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
                coalesce((SELECT string_agg(id::text || operation_key || on_hand_after::text || operation_type, ',' ORDER BY id) FROM stock_movements), '') || '|' ||
                coalesce((SELECT string_agg(id::text || user_id::text || updated_at::text, ',' ORDER BY id) FROM carts), '') || '|' ||
                coalesce((SELECT string_agg(id::text || cart_id::text || product_variant_id::text || quantity::text || unit_price_at_add::text, ',' ORDER BY id) FROM cart_items), '') || '|' ||
                coalesce((SELECT string_agg(id::text || user_id::text || label || phone || street_address, ',' ORDER BY id) FROM customer_addresses), ''))
            """,
            Ct))!;

    /// <summary>Katalog, kullanıcı, depo, bakiye, hareket, sepet ve adres verisini (AddCartAndAddresses sürümünde var olan tablolar) kurar.</summary>
    private static async Task SeedExistingDataAsync(PostgresTestDatabase database)
    {
        await LegacySchemaSeed.SeedDemoCatalogAsync(database, Ct);

        var userId = Guid.NewGuid();
        var warehouseId = Guid.NewGuid();
        var cartId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        await database.ExecuteAsync(
            $"""
            INSERT INTO "AspNetUsers" ("Id", "CreatedAt", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed",
                "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES ('{userId}', now(), 'u{suffix}', 'U{suffix}', 'u{suffix}@ornek.test', 'U{suffix}@ORNEK.TEST', false, false, false, true, 0)
            """,
            Ct);
        await database.ExecuteAsync(
            $"INSERT INTO warehouses (id, code, name, is_active, created_at, updated_at) VALUES ('{warehouseId}', 'DEPO-{suffix}', 'Test depo', true, now(), now())", Ct);
        await database.ExecuteAsync(
            $"""
            INSERT INTO inventory_balances (id, warehouse_id, product_variant_id, on_hand_quantity, version, created_at, updated_at)
            SELECT '{Guid.NewGuid()}', '{warehouseId}', id, 7, 1, now(), now() FROM product_variants ORDER BY sku LIMIT 1
            """,
            Ct);
        await database.ExecuteAsync(
            $"""
            INSERT INTO stock_movements (id, warehouse_id, product_variant_id, sku, quantity_delta, operation_type, actor_user_id, reason, reference,
                operation_key, request_fingerprint, on_hand_after, occurred_at)
            SELECT '{Guid.NewGuid()}', '{warehouseId}', id, sku, 7, 'receiving', '{userId}', 'Test girişi', NULL,
                'migration-key-{suffix}-0123456789', '{new string('b', 64)}', 7, now()
            FROM product_variants ORDER BY sku LIMIT 1
            """,
            Ct);
        await database.ExecuteAsync($"INSERT INTO carts (id, user_id, created_at, updated_at) VALUES ('{cartId}', '{userId}', now(), now())", Ct);
        await database.ExecuteAsync(
            $"""
            INSERT INTO cart_items (id, cart_id, product_variant_id, quantity, unit_price_at_add, created_at, updated_at)
            SELECT '{Guid.NewGuid()}', '{cartId}', id, 2, price, now(), now() FROM product_variants ORDER BY sku LIMIT 1
            """,
            Ct);
        await database.ExecuteAsync(
            $"""
            INSERT INTO customer_addresses (id, user_id, label, recipient_name, phone, province, district, street_address, postal_code, is_default, created_at, updated_at)
            VALUES ('{Guid.NewGuid()}', '{userId}', 'Ev', 'Ali Veli', '5321234567', 'İstanbul', 'Kadıköy', 'Moda Cd. No 1', '34710', true, now(), now())
            """,
            Ct);
    }

    [Fact]
    public async Task Fresh_install_applies_all_migrations_and_creates_the_ordering_objects_last()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);

        await using (var context = database.CreateDbContext())
        {
            await context.Database.MigrateAsync(Ct);
        }

        Assert.Equal(4, await TableCountAsync(database, OrderTables));
        var history = (await database.ScalarAsync<string>(
            "SELECT string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\") FROM \"__EFMigrationsHistory\"", Ct))!.Split(',');
        Assert.Equal(7, history.Length);
        Assert.EndsWith("_AddCartAndAddresses", history[3]);
        Assert.EndsWith("_AddOrdering", history[4]);
        Assert.EndsWith("_AddAdminOperations", history[5]);
        Assert.EndsWith("_AddCustomerFavorites", history[6]);

        foreach (var table in OrderTables)
        {
            Assert.Equal(0, await database.ScalarAsync<long>($"SELECT count(*) FROM {table}", Ct));
        }

        Assert.Equal(5, await TriggerCountAsync(database, "orders")); // 3 Faz 7 + Faz 8: durum geçişi ve ilk durum
        Assert.Equal(2, await TriggerCountAsync(database, "order_items"));
        Assert.Equal(2, await TriggerCountAsync(database, "order_inventory_allocations"));
        Assert.Equal(2, await TriggerCountAsync(database, "order_status_history"));
        Assert.Equal(2, await TriggerCountAsync(database, "stock_movements")); // Faz 5 değişmezliği korunuyor
        Assert.Equal(2, await OrderFunctionCountAsync(database));
    }

    [Fact]
    public async Task The_first_order_number_on_a_fresh_database_is_100001()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        var data = new OrderingTestData(database);
        var userId = await data.InsertUserAsync();

        var first = await data.InsertOrderAsync(userId);

        Assert.Equal(100001L, await database.ScalarAsync<long>($"SELECT order_number FROM orders WHERE id = '{first}'", Ct));
    }

    [Fact]
    public async Task Upgrading_a_populated_cart_database_keeps_existing_data_unchanged_and_extends_the_movement_check()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await MigrateToAsync(database, PreviousMigration);
        Assert.Equal(0, await TableCountAsync(database, OrderTables));
        await SeedExistingDataAsync(database);

        var before = await ExistingDataChecksumAsync(database);
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM stock_movements", Ct));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM cart_items", Ct));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM customer_addresses", Ct));
        Assert.DoesNotContain("order_placement", await OperationTypeCheckAsync(database));

        await using (var context = database.CreateDbContext())
        {
            var pending = (await context.Database.GetPendingMigrationsAsync(Ct)).ToArray();
            Assert.Equal(3, pending.Length); // AddOrdering, ardından Faz 8'in AddAdminOperations'ı ve MD-1'in AddCustomerFavorites'ı
            Assert.EndsWith("_AddOrdering", pending[0]);
            Assert.EndsWith("_AddAdminOperations", pending[1]);
            Assert.EndsWith("_AddCustomerFavorites", pending[2]);
        }

        await MigrateToAsync(database, targetMigration: null);

        Assert.Equal(before, await ExistingDataChecksumAsync(database));
        Assert.Equal(4, await TableCountAsync(database, OrderTables));
        var check = await OperationTypeCheckAsync(database);
        Assert.Contains("order_placement", check);
        Assert.Contains("order_cancellation", check);
        Assert.Equal(2, await TriggerCountAsync(database, "stock_movements"));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM stock_movements", Ct)); // eski hareket silinmedi, değişmedi
    }

    [Fact]
    public async Task A_second_migrate_is_a_noop()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        await SeedExistingDataAsync(database);
        var checksum = await ExistingDataChecksumAsync(database);

        await using (var context = database.CreateDbContext())
        {
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
            await context.Database.MigrateAsync(Ct);
        }

        Assert.Equal(7, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", Ct));
        Assert.Equal(4, await TableCountAsync(database, OrderTables));
        Assert.Equal(2, await OrderFunctionCountAsync(database));
        Assert.Equal(checksum, await ExistingDataChecksumAsync(database));
    }

    [Fact]
    public async Task Rolling_back_removes_only_the_ordering_objects_keeps_data_and_reapplying_works()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        await SeedExistingDataAsync(database);
        var before = await ExistingDataChecksumAsync(database);

        await MigrateToAsync(database, PreviousMigration);

        Assert.Equal(0, await TableCountAsync(database, OrderTables));
        Assert.Equal(0, await OrderFunctionCountAsync(database)); // trigger işlevleri de kalkmış olmalı
        Assert.Equal(4, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", Ct));
        Assert.DoesNotContain("order_placement", await OperationTypeCheckAsync(database)); // eski dar CHECK geri geldi
        Assert.Equal(2, await TriggerCountAsync(database, "stock_movements"));
        Assert.Equal(before, await ExistingDataChecksumAsync(database));

        await MigrateToAsync(database, targetMigration: null);

        Assert.Equal(4, await TableCountAsync(database, OrderTables));
        Assert.Equal(2, await OrderFunctionCountAsync(database));
        Assert.Contains("order_cancellation", await OperationTypeCheckAsync(database));
        Assert.Equal(7, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", Ct));
        Assert.Equal(before, await ExistingDataChecksumAsync(database));
        // Yeniden uygulanan şema çalışıyor: ilk sipariş yine 100001'den başlar.
        var data = new OrderingTestData(database);
        var orderId = await data.InsertOrderAsync(await data.InsertUserAsync());
        Assert.Equal(100001L, await database.ScalarAsync<long>($"SELECT order_number FROM orders WHERE id = '{orderId}'", Ct));
    }

    [Fact]
    public async Task Rolling_back_while_order_stock_movements_exist_fails_atomically_and_loses_nothing()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        var data = new OrderingTestData(database);
        var placed = await data.InsertFullOrderAsync();

        // Her migration kendi transaction'ındadır: önce Faz 8 adımı geri alınır (başarılı), sınanan adım AddOrdering'in kendi Down'ıdır.
        await MigrateToAsync(database, "AddOrdering");
        Assert.Equal(5, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", Ct));

        // Sipariş hareketi (order_placement) eski dar CHECK ile uyumsuzdur: geri alma reddedilir ve tek transaction olarak geri sarılır.
        await Assert.ThrowsAnyAsync<Exception>(() => MigrateToAsync(database, PreviousMigration));

        Assert.Equal(4, await TableCountAsync(database, OrderTables));
        Assert.Equal(1, await database.ScalarAsync<long>($"SELECT count(*) FROM orders WHERE id = '{placed.OrderId}'", Ct));
        Assert.Equal(1, await database.ScalarAsync<long>($"SELECT count(*) FROM order_items WHERE id = '{placed.ItemId}'", Ct));
        Assert.Equal(1, await database.ScalarAsync<long>($"SELECT count(*) FROM stock_movements WHERE id = '{placed.MovementId}'", Ct));
        Assert.Equal(5, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", Ct));
        Assert.Equal(2, await OrderFunctionCountAsync(database));
        Assert.Equal(3, await TriggerCountAsync(database, "orders")); // trigger'lar da geri sarıldı (Faz 8 trigger'ları zaten kalkmıştı)
        Assert.Contains("order_placement", await OperationTypeCheckAsync(database));
    }

    [Fact]
    public async Task Idempotent_script_upgrades_a_populated_database_and_is_safe_to_rerun()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await MigrateToAsync(database, PreviousMigration);
        await SeedExistingDataAsync(database);
        var before = await ExistingDataChecksumAsync(database);

        string script;
        await using (var context = database.CreateDbContext())
        {
            script = context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        }

        await database.ExecuteAsync(script, Ct);
        await database.ExecuteAsync(script, Ct);

        Assert.Equal(4, await TableCountAsync(database, OrderTables));
        Assert.Equal(2, await OrderFunctionCountAsync(database));
        Assert.Equal(5, await TriggerCountAsync(database, "orders"));
        Assert.Equal(7, await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", Ct));
        Assert.Equal(before, await ExistingDataChecksumAsync(database));
    }

    [Fact]
    public async Task Sql_audit_the_up_script_contains_exactly_one_drop_and_touches_no_existing_table_but_the_movement_check()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        string script;
        await using (var context = database.CreateDbContext())
        {
            var migrations = context.Database.GetMigrations().ToArray();
            var previous = migrations.Single(m => m.EndsWith("_" + PreviousMigration, StringComparison.Ordinal));
            var ordering = migrations.Single(m => m.EndsWith("_AddOrdering", StringComparison.Ordinal));
            script = context.GetService<IMigrator>().GenerateScript(previous, ordering);
        }

        // Yorum satırları ayıklanır; tek DROP: stock_movements CHECK'i (yeniden kurulmak üzere).
        var statements = script.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var drops = statements.Where(l => DropRegex().IsMatch(l)).ToArray();
        Assert.Equal(new[] { "ALTER TABLE stock_movements DROP CONSTRAINT ck_stock_movements_operation_type;" }, drops);

        // Mevcut tablolara ALTER yalnız aynı CHECK için (DROP + ADD).
        var alters = statements.Where(l => l.StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(2, alters.Length);
        Assert.All(alters, l => Assert.StartsWith("ALTER TABLE stock_movements ", l, StringComparison.Ordinal));
        Assert.Contains(alters, l => l.Contains("ADD CONSTRAINT ck_stock_movements_operation_type", StringComparison.Ordinal));

        // Veri değiştiren deyim yok: DELETE/TRUNCATE/UPDATE/INSERT yalnız trigger tanımlarının içinde ("BEFORE UPDATE OR DELETE ..." ya da işlev gövdesi).
        Assert.DoesNotContain(statements, l => Regex.IsMatch(l, @"^(DELETE FROM|TRUNCATE|UPDATE\s+\S+\s+SET|INSERT INTO\s+(?!""__EFMigrationsHistory"")\S+)", RegexOptions.IgnoreCase));
    }

    [Fact]
    public async Task Existing_tables_get_no_new_columns_constraints_or_indexes()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await MigrateToAsync(database, PreviousMigration);

        const string SchemaFingerprintSql =
            """
            SELECT md5(
                (SELECT string_agg(table_name || '.' || column_name || ':' || data_type || ':' || is_nullable, ',' ORDER BY table_name, column_name)
                 FROM information_schema.columns
                 WHERE table_schema = 'public' AND table_name NOT IN ('orders', 'order_items', 'order_inventory_allocations', 'order_status_history')) || '|' ||
                (SELECT string_agg(conrelid::regclass::text || '.' || conname, ',' ORDER BY conrelid::regclass::text, conname)
                 FROM pg_constraint WHERE connamespace = 'public'::regnamespace
                   AND conrelid::regclass::text NOT IN ('orders', 'order_items', 'order_inventory_allocations', 'order_status_history')) || '|' ||
                (SELECT string_agg(indexname, ',' ORDER BY indexname) FROM pg_indexes
                 WHERE schemaname = 'public' AND tablename NOT IN ('orders', 'order_items', 'order_inventory_allocations', 'order_status_history')) || '|' ||
                (SELECT string_agg(tgrelid::regclass::text || '.' || tgname, ',' ORDER BY tgrelid::regclass::text, tgname) FROM pg_trigger
                 WHERE NOT tgisinternal AND tgrelid::regclass::text NOT IN ('orders', 'order_items', 'order_inventory_allocations', 'order_status_history')))
            """;

        var before = await database.ScalarAsync<string>(SchemaFingerprintSql, Ct);
        await MigrateToAsync(database, "AddOrdering"); // AddAdminOperations bilerek version sütunu ekler: AdminOperationsMigrationTests denetler
        Assert.Equal(before, await database.ScalarAsync<string>(SchemaFingerprintSql, Ct));
    }

    [Fact]
    public async Task Ordering_columns_and_objects_follow_the_data_contract()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);

        async Task<string?> ColumnAsync(string table, string column) => await database.ScalarAsync<string>(
            $"SELECT data_type || coalesce('(' || character_maximum_length || ')', '') || ':' || is_nullable FROM information_schema.columns " +
            $"WHERE table_schema = 'public' AND table_name = '{table}' AND column_name = '{column}'",
            Ct);

        async Task<string?> NumericAsync(string table, string column) => await database.ScalarAsync<string>(
            $"SELECT numeric_precision || ',' || numeric_scale || ':' || is_nullable FROM information_schema.columns WHERE table_name = '{table}' AND column_name = '{column}'", Ct);

        Assert.Equal("uuid:NO", await ColumnAsync("orders", "id"));
        Assert.Equal("bigint:NO", await ColumnAsync("orders", "order_number"));
        Assert.Equal("ALWAYS", await database.ScalarAsync<string>(
            "SELECT identity_generation FROM information_schema.columns WHERE table_name = 'orders' AND column_name = 'order_number'", Ct));
        Assert.Equal("uuid:NO", await ColumnAsync("orders", "user_id"));
        Assert.Equal("character varying(128):NO", await ColumnAsync("orders", "idempotency_key"));
        Assert.Equal("character(64):NO", await ColumnAsync("orders", "request_fingerprint"));
        Assert.Equal("character varying(32):NO", await ColumnAsync("orders", "status"));
        Assert.Equal("character varying(50):NO", await ColumnAsync("orders", "delivery_label"));
        Assert.Equal("character varying(100):NO", await ColumnAsync("orders", "delivery_recipient_name"));
        Assert.Equal("character(10):NO", await ColumnAsync("orders", "delivery_phone"));
        Assert.Equal("character varying(32):NO", await ColumnAsync("orders", "delivery_province"));
        Assert.Equal("character varying(64):NO", await ColumnAsync("orders", "delivery_district"));
        Assert.Equal("character varying(300):NO", await ColumnAsync("orders", "delivery_street_address"));
        Assert.Equal("character(5):YES", await ColumnAsync("orders", "delivery_postal_code"));
        Assert.Equal("character(3):NO", await ColumnAsync("orders", "currency"));
        Assert.Equal("18,2:NO", await NumericAsync("orders", "item_subtotal"));
        Assert.Equal("character varying(32):NO", await ColumnAsync("orders", "shipping_status"));
        Assert.Equal("18,2:YES", await NumericAsync("orders", "shipping_amount"));
        Assert.Equal("18,2:YES", await NumericAsync("orders", "grand_total"));
        Assert.Equal("timestamp with time zone:NO", await ColumnAsync("orders", "created_at"));
        Assert.Equal("timestamp with time zone:NO", await ColumnAsync("orders", "updated_at"));

        Assert.Equal("18,2:NO", await NumericAsync("order_items", "unit_price"));
        Assert.Equal("18,2:NO", await NumericAsync("order_items", "line_subtotal"));
        Assert.Equal("integer:NO", await ColumnAsync("order_items", "quantity"));
        Assert.Equal("character varying(300):YES", await ColumnAsync("order_items", "image_url"));
        Assert.Equal("character varying(200):YES", await ColumnAsync("order_items", "image_alt"));
        Assert.Equal("character(3):NO", await ColumnAsync("order_items", "currency"));
        // Değişmez kayıtlarda updated_at yoktur.
        Assert.Null(await ColumnAsync("order_items", "updated_at"));
        Assert.Null(await ColumnAsync("order_inventory_allocations", "updated_at"));
        Assert.Equal("uuid:NO", await ColumnAsync("order_inventory_allocations", "placement_movement_id"));
        Assert.Equal("character varying(32):YES", await ColumnAsync("order_status_history", "from_status"));
        Assert.Equal("character varying(32):NO", await ColumnAsync("order_status_history", "to_status"));
        Assert.Equal("character varying(16):NO", await ColumnAsync("order_status_history", "actor_kind"));
        Assert.Equal("character varying(200):YES", await ColumnAsync("order_status_history", "reason"));
        Assert.Equal("timestamp with time zone:NO", await ColumnAsync("order_status_history", "occurred_at"));

        Assert.Equal(
            "ix_order_inventory_allocations_product_variant_id,ix_order_inventory_allocations_warehouse_id,ix_order_items_product_variant_id," +
            "ix_order_status_history_actor_user_id,ix_order_status_history_occurred_at_id,ix_order_status_history_order_id_occurred_at_id," +
            "ix_orders_open_created_at,ix_orders_open_user,ix_orders_status_created_at_id," +
            "ix_orders_user_id_created_at_id,pk_order_inventory_allocations,pk_order_items,pk_order_status_history,pk_orders," +
            "ux_order_inventory_allocations_order_item_id_warehouse_id,ux_order_inventory_allocations_placement_movement_id," +
            "ux_order_items_order_id_product_variant_id,ux_orders_order_number,ux_orders_user_id_idempotency_key",
            await database.ScalarAsync<string>(
                "SELECT string_agg(indexname, ',' ORDER BY indexname) FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ('orders', 'order_items', 'order_inventory_allocations', 'order_status_history')",
                Ct));
        // Geçmiş sorgusu en yeni önce: son iki sütun azalan.
        Assert.Contains("(user_id, created_at DESC, id DESC)", (await database.ScalarAsync<string>("SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_orders_user_id_created_at_id'", Ct))!);
        Assert.Contains("(status, created_at DESC, id DESC)", (await database.ScalarAsync<string>("SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_orders_status_created_at_id'", Ct))!);

        // Bütün FK'ler RESTRICT.
        Assert.Equal(
            "r",
            await database.ScalarAsync<string>(
                "SELECT string_agg(DISTINCT confdeltype::text, ',') FROM pg_constraint WHERE contype = 'f' AND conrelid::regclass::text IN ('orders', 'order_items', 'order_inventory_allocations', 'order_status_history')", Ct));
        Assert.Equal(
            9,
            await database.ScalarAsync<long>(
                "SELECT count(*) FROM pg_constraint WHERE contype = 'f' AND conrelid::regclass::text IN ('orders', 'order_items', 'order_inventory_allocations', 'order_status_history')", Ct));

        var triggers = await database.ScalarAsync<string>(
            """
            SELECT string_agg(tgrelid::regclass::text || '.' || tgname || ':' || (tgtype & 1) || ':' || (tgtype & 32), ',' ORDER BY tgrelid::regclass::text, tgname) FROM pg_trigger
            WHERE NOT tgisinternal AND tgrelid::regclass::text IN ('orders', 'order_items', 'order_inventory_allocations', 'order_status_history')
            """,
            Ct);
        // tgtype bit 1 = satır düzeyi, bit 32 = TRUNCATE.
        Assert.Equal(
            "order_inventory_allocations.trg_order_inventory_allocations_no_truncate:0:32,order_inventory_allocations.trg_order_inventory_allocations_no_update_delete:1:0," +
            "order_items.trg_order_items_no_truncate:0:32,order_items.trg_order_items_no_update_delete:1:0," +
            "order_status_history.trg_order_status_history_no_truncate:0:32,order_status_history.trg_order_status_history_no_update_delete:1:0," +
            "orders.trg_orders_insert_placed:1:0,orders.trg_orders_no_delete:1:0,orders.trg_orders_no_truncate:0:32,orders.trg_orders_restrict_update:1:0," +
            "orders.trg_orders_status_transition:1:0",
            triggers);

        // Adrese FK yok, yalnız snapshot sütunları.
        Assert.Equal(0, await database.ScalarAsync<long>(
            "SELECT count(*) FROM pg_constraint WHERE contype = 'f' AND conrelid = 'orders'::regclass AND confrelid = 'customer_addresses'::regclass", Ct));
    }
}

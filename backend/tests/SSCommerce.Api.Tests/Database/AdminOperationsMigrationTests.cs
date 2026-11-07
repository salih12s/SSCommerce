using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// Faz 8 <c>AddAdminOperations</c> migration'ı (docs/CONTRACT_PHASE_8.md "Persistence"). Tek ileri-yönlü EKLEMELİ migration'dır: mevcut tabloların
/// satırları değişmez; tek DDL etkisi catalog tablolarına <c>version</c> sütunudur. Şema üzerinde oynadıkları için her test KENDİ izole veritabanını açar.
/// </summary>
public partial class AdminOperationsMigrationTests
{
    private const string PreviousMigration = "AddOrdering";
    private static readonly string[] ObjectFunctions =
        ["append_only_reject_change", "categories_guard_parent", "orders_guard_status_transition", "orders_guard_insert_status"];

    [GeneratedRegex(@"\bDROP\b", RegexOptions.IgnoreCase)]
    private static partial Regex DropRegex();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task MigrateToAsync(PostgresTestDatabase database, string? targetMigration)
    {
        await using var context = database.CreateDbContext();
        await context.GetService<IMigrator>().MigrateAsync(targetMigration, Ct);
    }

    private static async Task<long> FunctionCountAsync(PostgresTestDatabase database) =>
        await database.ScalarAsync<long>(
            $"SELECT count(*) FROM pg_proc WHERE proname IN ({string.Join(", ", ObjectFunctions.Select(f => $"'{f}'"))})", Ct);

    private static async Task<long> TriggerCountAsync(PostgresTestDatabase database, string table) =>
        await database.ScalarAsync<long>($"SELECT count(*) FROM pg_trigger WHERE tgrelid = '{table}'::regclass AND NOT tgisinternal", Ct);

    private static async Task<long> HistoryCountAsync(PostgresTestDatabase database) =>
        await database.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\"", Ct);

    private static async Task<bool> TableExistsAsync(PostgresTestDatabase database, string table) =>
        await database.ScalarAsync<long>($"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{table}'", Ct) == 1;

    private static async Task<bool> ColumnExistsAsync(PostgresTestDatabase database, string table, string column) =>
        await database.ScalarAsync<long>(
            $"SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' AND column_name = '{column}'", Ct) == 1;

    /// <summary>
    /// ÖNCEKİ şemadaki her tablonun (geçmiş tablosu hariç) sütun listesi ve satır md5'i. Sütun listesi göç ÖNCESİ alınır; göç sonrası aynı
    /// sütunlarla yeniden hesaplanır, böylece yeni <c>version</c> sütunu parmak izini bozmaz ama eski verinin tek bir baytı değişse yakalanır.
    /// </summary>
    private static async Task<Dictionary<string, (string Columns, string Md5, long Rows)>> SnapshotAsync(PostgresTestDatabase database)
    {
        var tables = new List<string>();
        await using (var connection = await database.OpenConnectionAsync(Ct))
        await using (var command = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' AND table_name <> '__EFMigrationsHistory' ORDER BY table_name COLLATE \"C\"",
            connection))
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                tables.Add(reader.GetString(0));
            }
        }

        var result = new Dictionary<string, (string, string, long)>();
        foreach (var table in tables)
        {
            var columns = (await database.ScalarAsync<string>(
                $"SELECT string_agg('\"' || column_name || '\"', ', ' ORDER BY ordinal_position) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}'", Ct))!;
            result[table] = await ChecksumAsync(database, table, columns);
        }

        return result;
    }

    private static async Task<(string Columns, string Md5, long Rows)> ChecksumAsync(PostgresTestDatabase database, string table, string columns)
    {
        var md5 = (await database.ScalarAsync<string>(
            $"SELECT md5(coalesce(string_agg(r, '|' ORDER BY r), '')) FROM (SELECT ROW({columns})::text AS r FROM \"{table}\") q", Ct))!;
        var rows = await database.ScalarAsync<long>($"SELECT count(*) FROM \"{table}\"", Ct);
        return (columns, md5, rows);
    }

    private static async Task AssertSnapshotUnchangedAsync(PostgresTestDatabase database, Dictionary<string, (string Columns, string Md5, long Rows)> before)
    {
        foreach (var (table, state) in before)
        {
            var after = await ChecksumAsync(database, table, state.Columns);
            Assert.True(state.Md5 == after.Md5 && state.Rows == after.Rows, $"Tablo değişti: {table}");
        }
    }

    /// <summary>AddOrdering sürümündeki veritabanını gerçekçi veriyle doldurur: katalog, kullanıcılar, tam bir sipariş zinciri, bir sepet ve adres.</summary>
    private static async Task<OrderingTestData> SeedAtPreviousSchemaAsync(PostgresTestDatabase database)
    {
        await LegacySchemaSeed.SeedDemoCatalogAsync(database, Ct);
        var data = new OrderingTestData(database);
        var placed = await data.InsertFullOrderAsync();
        var second = await data.InsertOrderAsync(placed.UserId);
        await database.ExecuteAsync($"UPDATE orders SET status = 'processing' WHERE id = '{second}'", Ct);
        var cartId = Guid.NewGuid();
        await database.ExecuteAsync($"INSERT INTO carts (id, user_id, created_at, updated_at) VALUES ('{cartId}', '{placed.UserId}', now(), now())", Ct);
        await database.ExecuteAsync(
            $"""
            INSERT INTO customer_addresses (id, user_id, label, recipient_name, phone, province, district, street_address, postal_code, is_default, created_at, updated_at)
            VALUES ('{Guid.NewGuid()}', '{placed.UserId}', 'Ev', 'Ali Veli', '5321234567', 'İstanbul', 'Kadıköy', 'Moda Cd. No 1', '34710', true, now(), now())
            """,
            Ct);
        return data;
    }

    private static async Task InsertAuditRowAsync(PostgresTestDatabase database, Guid actorId) =>
        await database.ExecuteAsync(
            $"""
            INSERT INTO admin_audit_log (id, actor_user_id, action, target_type, target_id, changes, occurred_at)
            VALUES ('{Guid.NewGuid()}', '{actorId}', 'category.create', 'category', gen_random_uuid(), jsonb_build_object('name', 'Yeni'), now())
            """,
            Ct);

    [Fact]
    public async Task Fresh_install_creates_the_admin_objects_last()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);

        await MigrateToAsync(database, targetMigration: null);

        Assert.Equal(6, await HistoryCountAsync(database));
        Assert.EndsWith("_AddAdminOperations", (await database.ScalarAsync<string>(
            "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1", Ct))!);
        Assert.True(await TableExistsAsync(database, "admin_audit_log"));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM admin_audit_log", Ct));
        Assert.Equal(4, await FunctionCountAsync(database));
        Assert.Equal(2, await TriggerCountAsync(database, "admin_audit_log"));
        Assert.Equal(1, await TriggerCountAsync(database, "categories"));
        foreach (var table in new[] { "categories", "products", "product_variants" })
        {
            Assert.True(await ColumnExistsAsync(database, table, "version"));
        }

        await using var context = database.CreateDbContext();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Upgrading_a_populated_ordering_database_keeps_every_existing_row_unchanged_and_versions_start_at_one()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await MigrateToAsync(database, PreviousMigration);
        await SeedAtPreviousSchemaAsync(database);
        var before = await SnapshotAsync(database);
        Assert.True(before["orders"].Rows >= 2);
        Assert.True(before["product_variants"].Rows > 0);
        Assert.False(await TableExistsAsync(database, "admin_audit_log"));
        Assert.False(await ColumnExistsAsync(database, "categories", "version"));

        await using (var context = database.CreateDbContext())
        {
            var only = Assert.Single(await context.Database.GetPendingMigrationsAsync(Ct));
            Assert.EndsWith("_AddAdminOperations", only);
        }

        await MigrateToAsync(database, targetMigration: null);

        await AssertSnapshotUnchangedAsync(database, before);
        foreach (var table in new[] { "categories", "products", "product_variants" })
        {
            Assert.Equal(0, await database.ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE version <> 1", Ct));
            Assert.Equal(before[table].Rows, await database.ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE version = 1", Ct));
        }

        Assert.True(await TableExistsAsync(database, "admin_audit_log"));
        Assert.Equal(0, await database.ScalarAsync<long>("SELECT count(*) FROM admin_audit_log", Ct));
        // Eski siparişler yeni trigger'lardan etkilenmez: yasal bir geçiş hâlâ çalışır, yasadışı olan reddedilir.
        var orderId = await database.ScalarAsync<Guid>("SELECT id FROM orders WHERE status = 'processing' LIMIT 1", Ct);
        await database.ExecuteAsync($"UPDATE orders SET status = 'shipped' WHERE id = '{orderId}'", Ct);
        await new OrderingTestData(database).AssertViolationAsync("23514", $"UPDATE orders SET status = 'placed' WHERE id = '{orderId}'", "ck_orders_status_transition");
    }

    [Fact]
    public async Task A_second_migrate_is_a_noop()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        var data = new OrderingTestData(database);
        await data.InsertFullOrderAsync();
        var before = await SnapshotAsync(database);

        await using (var context = database.CreateDbContext())
        {
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));
            await context.Database.MigrateAsync(Ct);
        }

        Assert.Equal(6, await HistoryCountAsync(database));
        Assert.Equal(4, await FunctionCountAsync(database));
        await AssertSnapshotUnchangedAsync(database, before);
    }

    [Fact]
    public async Task Rolling_back_on_an_empty_audit_table_removes_only_the_admin_objects_and_reapplying_works()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await MigrateToAsync(database, PreviousMigration);
        await SeedAtPreviousSchemaAsync(database);
        await MigrateToAsync(database, targetMigration: null);
        var before = await SnapshotAsync(database);
        var withoutVersion = await SnapshotWithoutVersionAsync(database);

        await MigrateToAsync(database, PreviousMigration);

        Assert.Equal(5, await HistoryCountAsync(database));
        Assert.False(await TableExistsAsync(database, "admin_audit_log"));
        Assert.False(await ColumnExistsAsync(database, "categories", "version"));
        Assert.False(await ColumnExistsAsync(database, "products", "version"));
        Assert.False(await ColumnExistsAsync(database, "product_variants", "version"));
        Assert.Equal(0, await FunctionCountAsync(database)); // işlevler de kalkmış olmalı
        Assert.Equal(0, await TriggerCountAsync(database, "categories"));
        Assert.Equal(3, await TriggerCountAsync(database, "orders")); // Faz 7 trigger'ları kalır
        Assert.Equal(0, await database.ScalarAsync<long>(
            "SELECT count(*) FROM pg_indexes WHERE indexname IN ('ix_orders_open_created_at', 'ix_orders_open_user', 'ix_stock_movements_occurred_at_id', 'ix_order_status_history_occurred_at_id')", Ct));
        await AssertSnapshotUnchangedAsync(database, withoutVersion);

        await MigrateToAsync(database, targetMigration: null);

        Assert.Equal(6, await HistoryCountAsync(database));
        Assert.Equal(4, await FunctionCountAsync(database));
        Assert.Equal(5, await TriggerCountAsync(database, "orders"));
        await AssertSnapshotUnchangedAsync(database, before);
    }

    /// <summary>Tam şemada, <c>version</c> hariç sütunlarla parmak izi (geri alma sonrası eski şemayla kıyaslanabilsin diye).</summary>
    private static async Task<Dictionary<string, (string Columns, string Md5, long Rows)>> SnapshotWithoutVersionAsync(PostgresTestDatabase database)
    {
        var full = await SnapshotAsync(database);
        var result = new Dictionary<string, (string, string, long)>();
        foreach (var (table, state) in full.Where(t => t.Key != "admin_audit_log")) // eski şemada bu tablo yok
        {
            var columns = string.Join(", ", state.Columns.Split(", ").Where(c => c != "\"version\"" || table is "inventory_balances"));
            result[table] = await ChecksumAsync(database, table, columns);
            Assert.Equal(state.Rows, result[table].Item3);
        }

        return result;
    }

    [Fact]
    public async Task Rolling_back_is_refused_while_audit_rows_exist_and_nothing_is_lost()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        var data = new OrderingTestData(database);
        var actor = await data.InsertUserAsync();
        await InsertAuditRowAsync(database, actor);
        var before = await SnapshotAsync(database);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => MigrateToAsync(database, PreviousMigration));

        Assert.Contains("admin_audit_log has rows", (exception.InnerException ?? exception).Message, StringComparison.Ordinal);
        Assert.Equal(6, await HistoryCountAsync(database));
        Assert.Equal(1, await database.ScalarAsync<long>("SELECT count(*) FROM admin_audit_log", Ct));
        Assert.Equal(4, await FunctionCountAsync(database)); // hiçbir nesne düşmedi (tek transaction geri sarıldı)
        Assert.Equal(5, await TriggerCountAsync(database, "orders"));
        Assert.Equal(1, await TriggerCountAsync(database, "categories"));
        Assert.True(await ColumnExistsAsync(database, "categories", "version"));
        await AssertSnapshotUnchangedAsync(database, before);
    }

    [Fact]
    public async Task The_idempotent_script_upgrades_a_populated_database_and_is_safe_to_apply_twice()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        await MigrateToAsync(database, PreviousMigration);
        await SeedAtPreviousSchemaAsync(database);
        var before = await SnapshotAsync(database);

        string script;
        await using (var context = database.CreateDbContext())
        {
            script = context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        }

        await database.ExecuteAsync(script, Ct);
        await database.ExecuteAsync(script, Ct);

        Assert.Equal(6, await HistoryCountAsync(database));
        Assert.True(await TableExistsAsync(database, "admin_audit_log"));
        Assert.Equal(4, await FunctionCountAsync(database));
        Assert.Equal(5, await TriggerCountAsync(database, "orders"));
        Assert.Equal(1, await TriggerCountAsync(database, "categories"));
        await AssertSnapshotUnchangedAsync(database, before);
    }

    [Fact]
    public async Task Sql_audit_the_up_script_has_no_drop_no_data_change_and_only_version_columns_on_existing_tables()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        string script;
        await using (var context = database.CreateDbContext())
        {
            var migrations = context.Database.GetMigrations().ToArray();
            var previous = migrations.Single(m => m.EndsWith("_" + PreviousMigration, StringComparison.Ordinal));
            var admin = migrations.Single(m => m.EndsWith("_AddAdminOperations", StringComparison.Ordinal));
            script = context.GetService<IMigrator>().GenerateScript(previous, admin);
        }

        var statements = script.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)).ToArray();

        Assert.DoesNotContain(statements, l => DropRegex().IsMatch(l)); // hiçbir mevcut nesne düşürülmez
        // Veri değiştiren deyim yok (geçmiş tablosuna kayıt hariç).
        Assert.DoesNotContain(
            statements,
            l => Regex.IsMatch(l, @"^(DELETE FROM|TRUNCATE|UPDATE\s+\S+\s+SET|INSERT INTO\s+(?!""__EFMigrationsHistory"")\S+)", RegexOptions.IgnoreCase));

        // Mevcut tablolara ALTER: yalnız version sütunu ve onun CHECK'i.
        var alters = statements.Where(l => l.StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(6, alters.Length);
        Assert.Equal(3, alters.Count(l => Regex.IsMatch(l, @"^ALTER TABLE (categories|products|product_variants) ADD version integer NOT NULL DEFAULT 1;$")));
        Assert.Equal(3, alters.Count(l => Regex.IsMatch(l, @"^ALTER TABLE (categories|products|product_variants) ADD CONSTRAINT ck_\w+_version_positive CHECK \(version >= 1\);$")));
    }

    [Fact]
    public async Task The_down_script_removes_what_the_up_script_added_and_refuses_with_audit_rows_in_sql()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: false);
        string script;
        await using (var context = database.CreateDbContext())
        {
            var migrations = context.Database.GetMigrations().ToArray();
            var admin = migrations.Single(m => m.EndsWith("_AddAdminOperations", StringComparison.Ordinal));
            var previous = migrations.Single(m => m.EndsWith("_" + PreviousMigration, StringComparison.Ordinal));
            script = context.GetService<IMigrator>().GenerateScript(admin, previous);
        }

        Assert.Contains("admin_audit_log has rows", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("admin_audit_log has rows", StringComparison.Ordinal) < script.IndexOf("DROP TABLE admin_audit_log", StringComparison.Ordinal),
            "Satır denetimi tablo düşmeden ÖNCE gelmeli.");
    }

    [Fact]
    public async Task Partial_indexes_are_usable_for_literal_status_lists()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(migrate: true);
        var data = new OrderingTestData(database);
        var heavy = await data.InsertUserAsync();
        // ~3% açık, tek kullanıcıda çok sipariş: planlayıcı kısmi indeksleri seçebilsin.
        await database.ExecuteAsync(
            $"""
            INSERT INTO orders (id, user_id, idempotency_key, request_fingerprint, status, delivery_label, delivery_recipient_name, delivery_phone,
                delivery_province, delivery_district, delivery_street_address, delivery_postal_code, currency, item_subtotal, shipping_status,
                shipping_amount, grand_total, created_at, updated_at)
            SELECT md5('o' || i)::uuid, '{heavy}', md5('k' || i) || md5('j' || i), md5('f' || i) || md5('g' || i), 'placed',
                'Ev', 'Ali Veli', '5321234567', 'İstanbul', 'Kadıköy', 'Moda Cd. No 1', '34710', 'TRY', 10.00, 'shippingNotCalculated', NULL, NULL,
                now() - (i * interval '1 minute'), now()
            FROM generate_series(1, 3000) i
            """,
            Ct);
        // Çoğunu tamamlanmışa çek (yasal yol): açık küme küçülür.
        await database.ExecuteAsync("UPDATE orders SET status = 'cancelled' WHERE id IN (SELECT id FROM orders ORDER BY created_at DESC LIMIT 2900)", Ct);
        await database.ExecuteAsync("ANALYZE orders", Ct);

        await using var connection = await database.OpenConnectionAsync(Ct);
        async Task<string> PlanAsync(string sql)
        {
            await using var command = new NpgsqlCommand("EXPLAIN " + sql, connection);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var lines = new List<string>();
            while (await reader.ReadAsync(Ct))
            {
                lines.Add(reader.GetString(0));
            }

            return string.Join('\n', lines);
        }

        var openByUser = await PlanAsync($"SELECT count(*) FROM orders WHERE user_id = '{heavy}' AND status IN ('placed', 'processing')");
        var oldestOpen = await PlanAsync("SELECT id FROM orders WHERE status IN ('placed', 'processing') ORDER BY created_at, id LIMIT 10");

        Assert.Contains("ix_orders_open_user", openByUser, StringComparison.Ordinal);
        Assert.Contains("ix_orders_open_created_at", oldestOpen, StringComparison.Ordinal);
    }
}

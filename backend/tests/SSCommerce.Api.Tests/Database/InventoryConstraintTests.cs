using Microsoft.EntityFrameworkCore;
using Npgsql;
using SSCommerce.Domain.Inventory;

namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// Faz 5 stok şemasının (D-045..D-048) kısıtlarının kirli veriyi GERÇEKTEN reddettiğini kanıtlar. Ham SQL kullanılır: domain sınıfları
/// zaten yanlış girdiyi önce reddeder; soru "uygulama hatası ya da elle SQL kirli veri üretebilir mi?" sorusudur (savunma derinliği).
/// SqlState: 23505 unique, 23514 check, 23503 foreign key, 23001 stock_movements trigger'ı (restrict_violation).
/// Her test kendi benzersiz verisini kurar; sıra bağımsızdır.
/// </summary>
public class InventoryConstraintTests(MigratedDatabaseFixture fixture) : IClassFixture<MigratedDatabaseFixture>
{
    private static readonly string Fingerprint = new('a', 64);
    private const string EmptyJson = "{}";

    private PostgresTestDatabase Db => fixture.Database;

    // ---- Küçük veri kurucular ----

    private static string Unique() => Guid.NewGuid().ToString("N")[..12];

    private async Task<Guid> InsertWarehouseAsync(string? code = null)
    {
        var id = Guid.NewGuid();
        code ??= $"W-{Unique().ToUpperInvariant()}";
        await Db.ExecuteAsync(
            $"INSERT INTO warehouses (id, code, name, is_active, created_at, updated_at) VALUES ('{id}', '{code}', 'Test depo', true, now(), now())",
            TestContext.Current.CancellationToken);
        return id;
    }

    private async Task<(Guid VariantId, string Sku)> InsertVariantAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var categoryId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var sku = $"INV-{Unique().ToUpperInvariant()}";
        await Db.ExecuteAsync(
            $"INSERT INTO categories (id, name, slug, parent_id, sort_order, is_active, created_at, updated_at) VALUES ('{categoryId}', 'K', 'kat-{Unique()}', NULL, 0, true, now(), now())", ct);
        await Db.ExecuteAsync(
            $"INSERT INTO products (id, category_id, name, slug, is_active, created_at, updated_at) VALUES ('{productId}', '{categoryId}', 'U', 'urun-{Unique()}', true, now(), now())", ct);
        await Db.ExecuteAsync(
            $"""
            INSERT INTO product_variants (id, product_id, sku, display_name, options, option_signature, price, currency, is_active, created_at, updated_at)
            VALUES ('{variantId}', '{productId}', '{sku}', 'V', '{EmptyJson}'::jsonb, 'default', 10.00, 'TRY', true, now(), now())
            """,
            ct);
        return (variantId, sku);
    }

    private async Task<Guid> InsertUserAsync()
    {
        var id = Guid.NewGuid();
        var suffix = Unique().ToUpperInvariant();
        await Db.ExecuteAsync(
            $"""
            INSERT INTO "AspNetUsers" ("Id", "CreatedAt", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed",
                "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES ('{id}', now(), 'u{suffix}', 'U{suffix}', 'u{suffix}@ornek.test', 'U{suffix}@ORNEK.TEST', false, false, false, true, 0)
            """,
            TestContext.Current.CancellationToken);
        return id;
    }

    private static string BalanceSql(Guid warehouseId, Guid variantId, string onHand = "5", string version = "1") =>
        $"""
        INSERT INTO inventory_balances (id, warehouse_id, product_variant_id, on_hand_quantity, version, created_at, updated_at)
        VALUES ('{Guid.NewGuid()}', '{warehouseId}', '{variantId}', {onHand}, {version}, now(), now())
        """;

    private static string MovementSql(
        Guid warehouseId,
        Guid variantId,
        string sku,
        Guid actorId,
        string delta = "5",
        string type = "receiving",
        string? operationKey = null,
        string onHandAfter = "5",
        string reason = "Test girişi",
        string fingerprint = "") =>
        $"""
        INSERT INTO stock_movements (id, warehouse_id, product_variant_id, sku, quantity_delta, operation_type, actor_user_id, reason, reference,
            operation_key, request_fingerprint, on_hand_after, occurred_at)
        VALUES ('{Guid.NewGuid()}', '{warehouseId}', '{variantId}', '{sku}', {delta}, '{type}', '{actorId}', '{reason}', NULL,
            '{operationKey ?? $"key-{Unique()}-{Unique()}"}', '{(fingerprint.Length == 0 ? Fingerprint : fingerprint)}', {onHandAfter}, now())
        """;

    private async Task AssertViolationAsync(string expectedSqlState, string sql, string? expectedConstraint = null)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => Db.ExecuteAsync(sql, TestContext.Current.CancellationToken));
        Assert.Equal(expectedSqlState, exception.SqlState);
        if (expectedConstraint is not null)
        {
            // Hata gerçekten beklenen kısıttan mı geldi? (Başka bir nedenle patlayıp testin yanlışlıkla geçmesini önler.)
            Assert.Equal(expectedConstraint, exception.ConstraintName);
        }
    }

    private async Task<(Guid WarehouseId, Guid VariantId, string Sku, Guid ActorId)> NewContextAsync()
    {
        var warehouseId = await InsertWarehouseAsync();
        var (variantId, sku) = await InsertVariantAsync();
        var actorId = await InsertUserAsync();
        return (warehouseId, variantId, sku, actorId);
    }

    // ---- warehouses ----

    [Theory]
    [InlineData("a")]            // tek karakter
    [InlineData("w-1")]          // küçük harf
    [InlineData("-DEPO")]        // tireyle başlar
    [InlineData("DEPO_1")]       // alt çizgi
    [InlineData("DEPO 1")]       // boşluk
    public async Task Warehouse_code_with_a_bad_format_is_rejected(string code)
    {
        await AssertViolationAsync(
            "23514",
            $"INSERT INTO warehouses (id, code, name, is_active, created_at, updated_at) VALUES ('{Guid.NewGuid()}', '{code}', 'Depo', true, now(), now())",
            "ck_warehouses_code_format");
    }

    [Fact]
    public async Task Warehouse_code_with_a_trailing_newline_is_rejected()
    {
        // PostgreSQL "~" "$" ile gerçek sonu ister: "ABC" + LF kabul edilmez.
        await AssertViolationAsync(
            "23514",
            $"INSERT INTO warehouses (id, code, name, is_active, created_at, updated_at) VALUES ('{Guid.NewGuid()}', E'DEPO-9\\n', 'Depo', true, now(), now())",
            "ck_warehouses_code_format");
    }

    [Fact]
    public async Task Duplicate_warehouse_code_is_rejected()
    {
        var code = $"DUP-{Unique().ToUpperInvariant()}";
        await InsertWarehouseAsync(code);

        await AssertViolationAsync(
            "23505",
            $"INSERT INTO warehouses (id, code, name, is_active, created_at, updated_at) VALUES ('{Guid.NewGuid()}', '{code}', 'Depo', true, now(), now())",
            "ux_warehouses_code");
    }

    [Fact]
    public async Task Blank_warehouse_name_is_rejected()
    {
        await AssertViolationAsync(
            "23514",
            $"INSERT INTO warehouses (id, code, name, is_active, created_at, updated_at) VALUES ('{Guid.NewGuid()}', 'BOS-{Unique().ToUpperInvariant()}', '   ', true, now(), now())",
            "ck_warehouses_name_not_blank");
    }

    // ---- inventory_balances ----

    [Fact]
    public async Task Zero_stock_is_a_valid_balance_and_the_maximum_is_accepted()
    {
        var (warehouseId, variantId, _, _) = await NewContextAsync();
        await Db.ExecuteAsync(BalanceSql(warehouseId, variantId, onHand: "0"), TestContext.Current.CancellationToken);

        var (otherWarehouse, otherVariant, _, _) = await NewContextAsync();
        await Db.ExecuteAsync(
            BalanceSql(otherWarehouse, otherVariant, onHand: InventoryRules.MaxOnHandQuantity.ToString()), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Negative_on_hand_is_rejected()
    {
        var (warehouseId, variantId, _, _) = await NewContextAsync();

        await AssertViolationAsync("23514", BalanceSql(warehouseId, variantId, onHand: "-1"), "ck_inventory_balances_on_hand_range");
    }

    [Fact]
    public async Task On_hand_above_the_maximum_is_rejected()
    {
        var (warehouseId, variantId, _, _) = await NewContextAsync();

        await AssertViolationAsync(
            "23514",
            BalanceSql(warehouseId, variantId, onHand: (InventoryRules.MaxOnHandQuantity + 1).ToString()),
            "ck_inventory_balances_on_hand_range");
    }

    [Fact]
    public async Task A_conditional_update_that_would_go_negative_is_stopped_by_the_check_as_the_last_line_of_defense()
    {
        var (warehouseId, variantId, _, _) = await NewContextAsync();
        await Db.ExecuteAsync(BalanceSql(warehouseId, variantId, onHand: "3"), TestContext.Current.CancellationToken);

        await AssertViolationAsync(
            "23514",
            $"UPDATE inventory_balances SET on_hand_quantity = on_hand_quantity - 4 WHERE warehouse_id = '{warehouseId}' AND product_variant_id = '{variantId}'",
            "ck_inventory_balances_on_hand_range");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task Version_below_one_is_rejected(string version)
    {
        var (warehouseId, variantId, _, _) = await NewContextAsync();

        await AssertViolationAsync("23514", BalanceSql(warehouseId, variantId, version: version), "ck_inventory_balances_version_positive");
    }

    [Fact]
    public async Task Duplicate_warehouse_and_variant_pair_is_rejected_but_other_pairs_are_allowed()
    {
        var (warehouseId, variantId, _, _) = await NewContextAsync();
        await Db.ExecuteAsync(BalanceSql(warehouseId, variantId), TestContext.Current.CancellationToken);

        await AssertViolationAsync("23505", BalanceSql(warehouseId, variantId), "ux_inventory_balances_warehouse_id_product_variant_id");

        // Aynı varyant başka depoda, aynı depoda başka varyant serbest.
        var otherWarehouse = await InsertWarehouseAsync();
        var (otherVariant, _) = await InsertVariantAsync();
        await Db.ExecuteAsync(BalanceSql(otherWarehouse, variantId), TestContext.Current.CancellationToken);
        await Db.ExecuteAsync(BalanceSql(warehouseId, otherVariant), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Upsert_on_conflict_targets_the_unique_pair_and_enforces_the_maximum_through_its_where_clause()
    {
        // Sözleşmedeki atomik akışın (D-046) SQL şekli: ON CONFLICT hedefi (warehouse_id, product_variant_id) indeksi olmalı.
        var (warehouseId, variantId, _, _) = await NewContextAsync();
        var max = InventoryRules.MaxOnHandQuantity;
        string Upsert(int delta) =>
            $"""
            INSERT INTO inventory_balances (id, warehouse_id, product_variant_id, on_hand_quantity, version, created_at, updated_at)
            VALUES ('{Guid.NewGuid()}', '{warehouseId}', '{variantId}', {delta}, 1, now(), now())
            ON CONFLICT (warehouse_id, product_variant_id) DO UPDATE
                SET on_hand_quantity = inventory_balances.on_hand_quantity + {delta}, version = inventory_balances.version + 1, updated_at = now()
                WHERE inventory_balances.on_hand_quantity + {delta} <= {max}
            RETURNING on_hand_quantity
            """;

        Assert.Equal(10L, await Db.ScalarAsync<long>(Upsert(10), TestContext.Current.CancellationToken));
        Assert.Equal(15L, await Db.ScalarAsync<long>(Upsert(5), TestContext.Current.CancellationToken));

        // Üst sınırı aşan artış: WHERE yanlış → 0 satır (RETURNING boş), bakiye değişmez.
        Assert.Equal(0L, await Db.ScalarAsync<long>(Upsert(max), TestContext.Current.CancellationToken));
        Assert.Equal(15L, await Db.ScalarAsync<long>(
            $"SELECT on_hand_quantity FROM inventory_balances WHERE warehouse_id = '{warehouseId}' AND product_variant_id = '{variantId}'",
            TestContext.Current.CancellationToken));
        Assert.Equal(2L, await Db.ScalarAsync<long>(
            $"SELECT version FROM inventory_balances WHERE warehouse_id = '{warehouseId}' AND product_variant_id = '{variantId}'",
            TestContext.Current.CancellationToken));
    }

    // ---- stock_movements ----

    [Fact]
    public async Task A_valid_movement_is_accepted_for_every_operation_type_with_either_sign()
    {
        var (warehouseId, variantId, sku, actorId) = await NewContextAsync();
        var ct = TestContext.Current.CancellationToken;

        await Db.ExecuteAsync(MovementSql(warehouseId, variantId, sku, actorId, delta: "5", type: "receiving"), ct);
        await Db.ExecuteAsync(MovementSql(warehouseId, variantId, sku, actorId, delta: "-2", type: "adjustment", onHandAfter: "3"), ct);
        await Db.ExecuteAsync(MovementSql(warehouseId, variantId, sku, actorId, delta: "-3", type: "correction", onHandAfter: "0"), ct);
        await Db.ExecuteAsync(
            MovementSql(warehouseId, variantId, sku, actorId, delta: InventoryRules.MaxAbsoluteDelta.ToString(), onHandAfter: InventoryRules.MaxOnHandQuantity.ToString()),
            ct);
    }

    [Fact]
    public async Task Zero_delta_is_rejected()
    {
        var (warehouseId, variantId, sku, actorId) = await NewContextAsync();

        await AssertViolationAsync(
            "23514", MovementSql(warehouseId, variantId, sku, actorId, delta: "0", type: "adjustment"), "ck_stock_movements_quantity_delta_nonzero");
    }

    [Theory]
    [InlineData(1_000_001)]
    [InlineData(-1_000_001)]
    public async Task Delta_beyond_the_absolute_maximum_is_rejected(int delta)
    {
        var (warehouseId, variantId, sku, actorId) = await NewContextAsync();

        await AssertViolationAsync(
            "23514",
            MovementSql(warehouseId, variantId, sku, actorId, delta: delta.ToString(), type: "adjustment"),
            "ck_stock_movements_quantity_delta_range");
    }

    [Theory]
    [InlineData("issue")]
    [InlineData("RECEIVING")]
    [InlineData("")]
    public async Task Unknown_operation_type_is_rejected(string type)
    {
        var (warehouseId, variantId, sku, actorId) = await NewContextAsync();

        await AssertViolationAsync(
            "23514", MovementSql(warehouseId, variantId, sku, actorId, type: type), "ck_stock_movements_operation_type");
    }

    [Fact]
    public async Task Negative_on_hand_after_is_rejected()
    {
        var (warehouseId, variantId, sku, actorId) = await NewContextAsync();

        await AssertViolationAsync(
            "23514", MovementSql(warehouseId, variantId, sku, actorId, onHandAfter: "-1"), "ck_stock_movements_on_hand_after_range");
    }

    [Fact]
    public async Task Blank_reason_is_rejected()
    {
        var (warehouseId, variantId, sku, actorId) = await NewContextAsync();

        await AssertViolationAsync(
            "23514", MovementSql(warehouseId, variantId, sku, actorId, reason: "   "), "ck_stock_movements_reason_not_blank");
    }

    [Fact]
    public async Task Duplicate_operation_key_is_rejected_even_for_a_different_warehouse()
    {
        var (warehouseId, variantId, sku, actorId) = await NewContextAsync();
        var otherWarehouse = await InsertWarehouseAsync();
        var key = $"dup-key-{Unique()}-{Unique()}";
        await Db.ExecuteAsync(MovementSql(warehouseId, variantId, sku, actorId, operationKey: key), TestContext.Current.CancellationToken);

        await AssertViolationAsync(
            "23505", MovementSql(otherWarehouse, variantId, sku, actorId, operationKey: key), "ux_stock_movements_operation_key");
    }

    [Theory]
    [InlineData("kisa")]                       // 16 karakterden kısa
    [InlineData("anahtar bosluklu 123456")]   // boşluk
    [InlineData("anahtar/slash/12345678")]    // izinli olmayan karakter
    public async Task Malformed_operation_key_is_rejected(string key)
    {
        var (warehouseId, variantId, sku, actorId) = await NewContextAsync();

        await AssertViolationAsync(
            "23514", MovementSql(warehouseId, variantId, sku, actorId, operationKey: key), "ck_stock_movements_operation_key_format");
    }

    [Theory]
    [InlineData("abc")]                                                                 // char(64) boşlukla doldurur; format CHECK'i gerekli
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]    // büyük harf hex
    public async Task Malformed_request_fingerprint_is_rejected(string fingerprint)
    {
        var (warehouseId, variantId, sku, actorId) = await NewContextAsync();

        await AssertViolationAsync(
            "23514",
            MovementSql(warehouseId, variantId, sku, actorId, fingerprint: fingerprint),
            "ck_stock_movements_request_fingerprint_format");
    }

    // ---- Yabancı anahtarlar: hepsi RESTRICT ----

    [Fact]
    public async Task Balance_and_movement_must_reference_existing_warehouse_variant_and_user()
    {
        var (warehouseId, variantId, sku, actorId) = await NewContextAsync();
        var ghost = Guid.NewGuid();

        await AssertViolationAsync("23503", BalanceSql(ghost, variantId), "fk_inventory_balances_warehouse");
        await AssertViolationAsync("23503", BalanceSql(warehouseId, ghost), "fk_inventory_balances_product_variant");
        await AssertViolationAsync("23503", MovementSql(ghost, variantId, sku, actorId), "fk_stock_movements_warehouse");
        await AssertViolationAsync("23503", MovementSql(warehouseId, ghost, sku, actorId), "fk_stock_movements_product_variant");
        await AssertViolationAsync("23503", MovementSql(warehouseId, variantId, sku, ghost), "fk_stock_movements_actor_user");
    }

    [Fact]
    public async Task Deleting_a_referenced_warehouse_variant_or_user_is_restricted()
    {
        var (warehouseId, variantId, sku, actorId) = await NewContextAsync();
        var ct = TestContext.Current.CancellationToken;
        await Db.ExecuteAsync(BalanceSql(warehouseId, variantId), ct);
        await Db.ExecuteAsync(MovementSql(warehouseId, variantId, sku, actorId), ct);

        await AssertViolationAsync("23503", $"DELETE FROM warehouses WHERE id = '{warehouseId}'");
        await AssertViolationAsync("23503", $"DELETE FROM product_variants WHERE id = '{variantId}'");
        await AssertViolationAsync("23503", $"DELETE FROM \"AspNetUsers\" WHERE \"Id\" = '{actorId}'");

        // Hiçbiri silinmedi.
        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM warehouses WHERE id = '{warehouseId}'", ct));
        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM product_variants WHERE id = '{variantId}'", ct));
        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM \"AspNetUsers\" WHERE \"Id\" = '{actorId}'", ct));
    }

    [Fact]
    public async Task All_inventory_foreign_keys_are_on_delete_restrict()
    {
        var rows = await Db.ScalarAsync<string>(
            """
            SELECT string_agg(conname || ':' || confdeltype::text, ',' ORDER BY conname)
            FROM pg_constraint
            WHERE contype = 'f' AND conrelid::regclass::text IN ('inventory_balances', 'stock_movements')
            """,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "fk_inventory_balances_product_variant:r,fk_inventory_balances_warehouse:r," +
            "fk_stock_movements_actor_user:r,fk_stock_movements_product_variant:r,fk_stock_movements_warehouse:r",
            rows);
    }

    // ---- Değişmezlik: trigger ----

    [Fact]
    public async Task Stock_movements_reject_update_delete_and_truncate_and_stay_intact()
    {
        var (warehouseId, variantId, sku, actorId) = await NewContextAsync();
        var ct = TestContext.Current.CancellationToken;
        var key = $"immutable-{Unique()}-{Unique()}";
        await Db.ExecuteAsync(MovementSql(warehouseId, variantId, sku, actorId, operationKey: key, reason: "Orijinal"), ct);

        await AssertViolationAsync("23001", $"UPDATE stock_movements SET reason = 'Değişti' WHERE operation_key = '{key}'");
        await AssertViolationAsync("23001", $"UPDATE stock_movements SET quantity_delta = 99 WHERE operation_key = '{key}'");
        await AssertViolationAsync("23001", $"DELETE FROM stock_movements WHERE operation_key = '{key}'");
        // Faz 7'den beri order_inventory_allocations FK ile stock_movements'a başvurur: düz TRUNCATE trigger'dan önce PostgreSQL'in kendi
        // kuralına (0A000) takılır. Her iki durumda da boşaltılamaz; CASCADE referans kontrolünü aşar ve DOĞRUDAN trigger'ı (23001) sınar.
        var plainTruncate = await Assert.ThrowsAsync<PostgresException>(() => Db.ExecuteAsync("TRUNCATE TABLE stock_movements", ct));
        Assert.Contains(plainTruncate.SqlState, new[] { "23001", "0A000" });
        await AssertViolationAsync("23001", "TRUNCATE TABLE stock_movements CASCADE");

        // Satır olduğu gibi duruyor.
        Assert.Equal("Orijinal", await Db.ScalarAsync<string>($"SELECT reason FROM stock_movements WHERE operation_key = '{key}'", ct));
        Assert.Equal(5L, await Db.ScalarAsync<long>($"SELECT quantity_delta FROM stock_movements WHERE operation_key = '{key}'", ct));
    }

    [Fact]
    public async Task A_cascading_truncate_started_from_a_parent_table_is_also_blocked_by_the_movement_trigger()
    {
        var (warehouseId, variantId, sku, actorId) = await NewContextAsync();
        var ct = TestContext.Current.CancellationToken;
        await Db.ExecuteAsync(MovementSql(warehouseId, variantId, sku, actorId), ct);

        // TRUNCATE ... CASCADE ana tablodan geçerek stock_movements'a ulaşmaya çalışsa da trigger devreye girer.
        await AssertViolationAsync("23001", "TRUNCATE TABLE warehouses CASCADE");
        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM warehouses WHERE id = '{warehouseId}'", ct));
    }

    // ---- EF eşlemesi ----

    [Fact]
    public async Task Entities_round_trip_through_ef_with_utc_timestamps_enum_text_and_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero);
        var (variantId, sku) = await InsertVariantAsync();
        var actorId = await InsertUserAsync();
        var warehouse = new Warehouse($"EF-{Unique().ToUpperInvariant()}", "  EF deposu  ");
        var balance = new InventoryBalance(warehouse.Id, variantId, 7);
        var movement = new StockMovement(
            warehouse.Id, variantId, sku, 7, StockOperationType.Receiving, actorId, " İlk giriş ", " PO-1 ",
            $"ef-key-{Unique()}-{Unique()}", Fingerprint, 7, now.ToOffset(TimeSpan.FromHours(3)));

        await using (var context = Db.CreateDbContext(new FixedTimeProvider(now)))
        {
            context.AddRange(warehouse, balance, movement);
            await context.SaveChangesAsync(ct);
        }

        await using var read = Db.CreateDbContext();
        var loadedWarehouse = await read.Warehouses.SingleAsync(w => w.Id == warehouse.Id, ct);
        Assert.Equal("EF deposu", loadedWarehouse.Name);
        Assert.True(loadedWarehouse.IsActive);
        Assert.Equal(now, loadedWarehouse.CreatedAt);

        var loadedBalance = await read.InventoryBalances.SingleAsync(b => b.Id == balance.Id, ct);
        Assert.Equal((7, 1L), (loadedBalance.OnHandQuantity, loadedBalance.Version));
        Assert.Equal(now, loadedBalance.UpdatedAt);

        var loadedMovement = await read.StockMovements.SingleAsync(m => m.Id == movement.Id, ct);
        Assert.Equal(StockOperationType.Receiving, loadedMovement.OperationType);
        Assert.Equal("İlk giriş", loadedMovement.Reason);
        Assert.Equal("PO-1", loadedMovement.Reference);
        Assert.Equal(now, loadedMovement.OccurredAt);
        Assert.Equal(TimeSpan.Zero, loadedMovement.OccurredAt.Offset);
        Assert.Equal("receiving", await Db.ScalarAsync<string>($"SELECT operation_type FROM stock_movements WHERE id = '{movement.Id}'", ct));
    }

    [Fact]
    public async Task Changing_a_tracked_movement_through_ef_is_rejected_by_the_trigger()
    {
        // Uygulama kodu hatayla bir hareketi SaveChanges ile değiştirmeye/silmeye çalışsa bile veritabanı reddeder.
        var ct = TestContext.Current.CancellationToken;
        var (warehouseId, variantId, sku, actorId) = await NewContextAsync();
        var movement = new StockMovement(
            warehouseId, variantId, sku, 4, StockOperationType.Receiving, actorId, "Giriş", null,
            $"ef-del-{Unique()}-{Unique()}", Fingerprint, 4, DateTimeOffset.UtcNow);

        await using (var context = Db.CreateDbContext())
        {
            context.StockMovements.Add(movement);
            await context.SaveChangesAsync(ct);
        }

        await using (var context = Db.CreateDbContext())
        {
            var tracked = await context.StockMovements.SingleAsync(m => m.Id == movement.Id, ct);
            context.StockMovements.Remove(tracked);
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(ct));
            var inner = Assert.IsType<PostgresException>(exception.InnerException);
            Assert.Equal("23001", inner.SqlState);
        }

        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM stock_movements WHERE id = '{movement.Id}'", ct));
    }
}

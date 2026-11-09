using Microsoft.EntityFrameworkCore;
using Npgsql;
using SSCommerce.Domain.Customers;
using SSCommerce.Domain.Inventory;
using SSCommerce.Domain.Ordering;

namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// Faz 7 sipariş şemasının (D-059..D-064) kısıtlarının, FK'lerin ve değişmezlik trigger'larının kirli veriyi GERÇEKTEN reddettiğini
/// kanıtlar. Ham SQL kullanılır (savunma derinliği). Her test kendi benzersiz verisini kurar; sıra bağımsızdır.
/// </summary>
public class OrderingConstraintTests(MigratedDatabaseFixture fixture) : IClassFixture<MigratedDatabaseFixture>
{
    private PostgresTestDatabase Db => fixture.Database;

    private OrderingTestData Data => new(Db);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyDictionary<string, string> With(string column, string value) =>
        new Dictionary<string, string> { [column] = value };

    // ---- orders: CHECK ----

    [Fact]
    public async Task A_valid_order_row_is_accepted_and_gets_an_identity_order_number()
    {
        var userId = await Data.InsertUserAsync();

        var first = await Data.InsertOrderAsync(userId);
        var second = await Data.InsertOrderAsync(userId);

        var firstNumber = await Db.ScalarAsync<long>($"SELECT order_number FROM orders WHERE id = '{first}'", Ct);
        var secondNumber = await Db.ScalarAsync<long>($"SELECT order_number FROM orders WHERE id = '{second}'", Ct);
        Assert.True(firstNumber >= OrderRules.InitialOrderNumber);
        Assert.True(secondNumber > firstNumber);
    }

    [Theory]
    [InlineData("idempotency_key", "'kisa-anahtar'", "ck_orders_idempotency_key_format")] // 16 karakterden kısa
    [InlineData("idempotency_key", "'gecersiz anahtar bosluk'", "ck_orders_idempotency_key_format")]
    [InlineData("idempotency_key", "'gecerli-anahtar-0123' || chr(10)", "ck_orders_idempotency_key_format")] // sondaki satır sonu
    [InlineData("request_fingerprint", "'ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789'", "ck_orders_request_fingerprint_format")] // büyük harf
    [InlineData("request_fingerprint", "repeat('a', 63)", "ck_orders_request_fingerprint_format")]
    [InlineData("status", "'paid'", "ck_orders_status")]
    [InlineData("status", "'Placed'", "ck_orders_status")]
    [InlineData("delivery_label", "'   '", "ck_orders_delivery_label_not_blank")]
    [InlineData("delivery_recipient_name", "''", "ck_orders_delivery_recipient_name_not_blank")]
    [InlineData("delivery_phone", "'4321234567'", "ck_orders_delivery_phone_format")] // 5 ile başlamıyor
    [InlineData("delivery_phone", "'532123456 '", "ck_orders_delivery_phone_format")]
    [InlineData("delivery_province", "' '", "ck_orders_delivery_province_not_blank")]
    [InlineData("delivery_district", "' '", "ck_orders_delivery_district_not_blank")]
    [InlineData("delivery_street_address", "' '", "ck_orders_delivery_street_address_not_blank")]
    [InlineData("delivery_postal_code", "'99999'", "ck_orders_delivery_postal_code_format")] // il kodu 82+
    [InlineData("currency", "'USD'", "ck_orders_currency")]
    [InlineData("item_subtotal", "-0.01", "ck_orders_item_subtotal_non_negative")]
    [InlineData("shipping_status", "'free'", "ck_orders_shipping_status")]
    [InlineData("shipping_amount", "0.00", "ck_orders_shipping_amount_null")] // sıfır bile olsa uydurulmuş kargo tutarı
    [InlineData("shipping_amount", "29.90", "ck_orders_shipping_amount_null")]
    [InlineData("grand_total", "30.00", "ck_orders_grand_total_null")]
    public async Task Order_check_constraints_reject_bad_values(string column, string value, string constraint)
    {
        var userId = await Data.InsertUserAsync();

        await Data.AssertViolationAsync("23514", OrderingTestData.OrderSql(Guid.NewGuid(), userId, With(column, value)), constraint);
    }

    [Fact]
    public async Task Nullable_snapshot_postal_code_accepts_null()
    {
        var userId = await Data.InsertUserAsync();

        var id = await Data.InsertOrderAsync(userId, With("delivery_postal_code", "NULL"));

        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM orders WHERE id = '{id}' AND delivery_postal_code IS NULL", Ct));
    }

    [Theory]
    [InlineData("user_id")]
    [InlineData("idempotency_key")]
    [InlineData("request_fingerprint")]
    [InlineData("status")]
    [InlineData("delivery_label")]
    [InlineData("delivery_recipient_name")]
    [InlineData("delivery_phone")]
    [InlineData("delivery_province")]
    [InlineData("delivery_district")]
    [InlineData("delivery_street_address")]
    [InlineData("currency")]
    [InlineData("item_subtotal")]
    [InlineData("shipping_status")]
    [InlineData("created_at")]
    [InlineData("updated_at")]
    public async Task Required_order_columns_reject_null(string column)
    {
        var userId = await Data.InsertUserAsync();

        await Data.AssertViolationAsync("23502", OrderingTestData.OrderSql(Guid.NewGuid(), userId, With(column, "NULL")));
    }

    [Fact]
    public async Task The_idempotency_key_is_unique_per_user_not_global()
    {
        var userId = await Data.InsertUserAsync();
        var otherUserId = await Data.InsertUserAsync();
        var key = OrderingTestData.NewKey();
        await Data.InsertOrderAsync(userId, With("idempotency_key", $"'{key}'"));

        await Data.AssertViolationAsync(
            "23505",
            OrderingTestData.OrderSql(Guid.NewGuid(), userId, With("idempotency_key", $"'{key}'")),
            "ux_orders_user_id_idempotency_key");

        // Başka kullanıcı aynı anahtarı kullanabilir (anahtar kullanıcıya özeldir).
        await Data.InsertOrderAsync(otherUserId, With("idempotency_key", $"'{key}'"));
    }

    [Fact]
    public async Task The_order_number_is_generated_always_and_cannot_be_supplied()
    {
        var userId = await Data.InsertUserAsync();
        var sql = OrderingTestData.OrderSql(Guid.NewGuid(), userId).Replace("INSERT INTO orders (", "INSERT INTO orders (order_number, ", StringComparison.Ordinal)
            .Replace("VALUES (", "VALUES (1, ", StringComparison.Ordinal);

        await Data.AssertViolationAsync("428C9", sql);
    }

    [Fact]
    public async Task The_order_number_is_unique_and_backed_by_a_unique_index()
    {
        var userId = await Data.InsertUserAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await Data.InsertOrderAsync(userId));
        }

        var list = string.Join(", ", ids.Select(i => $"'{i}'"));
        Assert.Equal(3L, await Db.ScalarAsync<long>($"SELECT count(DISTINCT order_number) FROM orders WHERE id IN ({list})", Ct));
        Assert.True(await Db.ScalarAsync<bool>(
            "SELECT indisunique FROM pg_index WHERE indexrelid = 'ux_orders_order_number'::regclass", Ct));
    }

    [Fact]
    public async Task Orders_reference_users_with_restrict_and_there_is_no_address_foreign_key()
    {
        var userId = await Data.InsertUserAsync();
        await Data.InsertOrderAsync(userId);

        await Data.AssertViolationAsync("23503", $"DELETE FROM \"AspNetUsers\" WHERE \"Id\" = '{userId}'", "fk_orders_user");
        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM \"AspNetUsers\" WHERE \"Id\" = '{userId}'", Ct));

        // Bilinmeyen kullanıcı da reddedilir.
        await Data.AssertViolationAsync("23503", OrderingTestData.OrderSql(Guid.NewGuid(), Guid.NewGuid()), "fk_orders_user");

        // Adrese FK yok: snapshot adres düzenlense ya da silinse de sipariş kalır.
        Assert.Equal(
            0L,
            await Db.ScalarAsync<long>(
                "SELECT count(*) FROM pg_constraint WHERE contype = 'f' AND conrelid = 'orders'::regclass AND confrelid <> '\"AspNetUsers\"'::regclass", Ct));
    }

    // ---- order_items ----

    [Fact]
    public async Task A_valid_order_item_is_accepted_and_exact_decimal_line_subtotals_pass()
    {
        var userId = await Data.InsertUserAsync();
        var orderId = await Data.InsertOrderAsync(userId);
        var (variantId, sku) = await Data.InsertVariantAsync();

        // 0.10 × 3 = 0.30: ikili kayan nokta olsaydı eşitlik tutmazdı; numeric tutar.
        await Db.ExecuteAsync(OrderingTestData.ItemSql(Guid.NewGuid(), orderId, variantId, sku, unitPrice: "0.10", quantity: "3", lineSubtotal: "0.30"), Ct);
    }

    [Theory]
    [InlineData("unitPrice", "-1.00", "ck_order_items_unit_price_non_negative")]
    [InlineData("quantity", "0", "ck_order_items_quantity_range")]
    [InlineData("quantity", "21", "ck_order_items_quantity_range")]
    [InlineData("lineSubtotal", "29.99", "ck_order_items_line_subtotal")] // 10.00 × 3 ≠ 29.99
    [InlineData("lineSubtotal", "30.01", "ck_order_items_line_subtotal")]
    [InlineData("currency", "USD", "ck_order_items_currency")]
    [InlineData("sku", "kucuk-harf", "ck_order_items_sku_format")]
    [InlineData("slug", "Buyuk_Harf", "ck_order_items_product_slug_format")]
    [InlineData("image", "'/etc/passwd'", "ck_order_items_image_url_format")]
    [InlineData("productName", "  ", "ck_order_items_product_name_not_blank")]
    [InlineData("variantName", "  ", "ck_order_items_variant_name_not_blank")]
    public async Task Order_item_check_constraints_reject_bad_values(string field, string value, string constraint)
    {
        var userId = await Data.InsertUserAsync();
        var orderId = await Data.InsertOrderAsync(userId);
        var (variantId, sku) = await Data.InsertVariantAsync();

        var sql = field switch
        {
            // Tek ihlal: satır toplamı eşitliği korunur ki hata yalnız denenen kısıttan gelsin.
            "unitPrice" => OrderingTestData.ItemSql(Guid.NewGuid(), orderId, variantId, sku, unitPrice: value, lineSubtotal: "-3.00"),
            "quantity" => OrderingTestData.ItemSql(Guid.NewGuid(), orderId, variantId, sku, quantity: value, lineSubtotal: value == "0" ? "0.00" : "210.00"),
            "lineSubtotal" => OrderingTestData.ItemSql(Guid.NewGuid(), orderId, variantId, sku, lineSubtotal: value),
            "currency" => OrderingTestData.ItemSql(Guid.NewGuid(), orderId, variantId, sku, currency: value),
            "sku" => OrderingTestData.ItemSql(Guid.NewGuid(), orderId, variantId, value),
            "slug" => OrderingTestData.ItemSql(Guid.NewGuid(), orderId, variantId, sku, productSlug: value),
            "image" => OrderingTestData.ItemSql(Guid.NewGuid(), orderId, variantId, sku, imageUrl: value),
            "productName" => OrderingTestData.ItemSql(Guid.NewGuid(), orderId, variantId, sku, productName: value),
            "variantName" => OrderingTestData.ItemSql(Guid.NewGuid(), orderId, variantId, sku, variantName: value),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        await Data.AssertViolationAsync("23514", sql, constraint);
    }

    [Fact]
    public async Task An_order_cannot_hold_the_same_variant_twice_and_item_foreign_keys_restrict()
    {
        var userId = await Data.InsertUserAsync();
        var orderId = await Data.InsertOrderAsync(userId);
        var (variantId, sku) = await Data.InsertVariantAsync();
        await Data.InsertItemAsync(orderId, variantId, sku);

        await Data.AssertViolationAsync(
            "23505",
            OrderingTestData.ItemSql(Guid.NewGuid(), orderId, variantId, sku),
            "ux_order_items_order_id_product_variant_id");
        await Data.AssertViolationAsync(
            "23503",
            OrderingTestData.ItemSql(Guid.NewGuid(), Guid.NewGuid(), variantId, sku),
            "fk_order_items_order");
        await Data.AssertViolationAsync(
            "23503",
            OrderingTestData.ItemSql(Guid.NewGuid(), orderId, Guid.NewGuid(), sku),
            "fk_order_items_product_variant");

        // Varyant, sipariş kalemi tarafından kullanılıyorsa silinemez (RESTRICT).
        await Data.AssertViolationAsync("23503", $"DELETE FROM product_variants WHERE id = '{variantId}'", "fk_order_items_product_variant");
    }

    // ---- order_inventory_allocations ----

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task Allocation_quantity_must_be_positive(string quantity)
    {
        var userId = await Data.InsertUserAsync();
        var warehouseId = await Data.InsertWarehouseAsync();
        var (variantId, sku) = await Data.InsertVariantAsync();
        var orderId = await Data.InsertOrderAsync(userId);
        var itemId = await Data.InsertItemAsync(orderId, variantId, sku);
        var movementId = await Data.InsertMovementAsync(warehouseId, variantId, sku, userId);

        await Data.AssertViolationAsync(
            "23514",
            OrderingTestData.AllocationSql(Guid.NewGuid(), itemId, warehouseId, variantId, movementId, quantity),
            "ck_order_inventory_allocations_quantity_positive");
    }

    [Fact]
    public async Task Allocation_uniques_and_foreign_keys_hold()
    {
        var placed = await Data.InsertFullOrderAsync();
        var secondMovement = await Data.InsertMovementAsync(placed.WarehouseId, placed.VariantId, placed.Sku, placed.UserId);

        // Aynı kalem aynı depoya ikinci kez dağıtılamaz.
        await Data.AssertViolationAsync(
            "23505",
            OrderingTestData.AllocationSql(Guid.NewGuid(), placed.ItemId, placed.WarehouseId, placed.VariantId, secondMovement),
            "ux_order_inventory_allocations_order_item_id_warehouse_id");

        // Aynı kalem başka depoya bölünebilir (D-062), ama bir stok hareketi iki parçayı kanıtlayamaz.
        var otherWarehouse = await Data.InsertWarehouseAsync();
        var otherMovement = await Data.InsertMovementAsync(otherWarehouse, placed.VariantId, placed.Sku, placed.UserId);
        await Db.ExecuteAsync(OrderingTestData.AllocationSql(Guid.NewGuid(), placed.ItemId, otherWarehouse, placed.VariantId, otherMovement), Ct);
        await Data.AssertViolationAsync(
            "23505",
            OrderingTestData.AllocationSql(Guid.NewGuid(), placed.ItemId, await Data.InsertWarehouseAsync(), placed.VariantId, placed.MovementId),
            "ux_order_inventory_allocations_placement_movement_id");

        await Data.AssertViolationAsync(
            "23503",
            OrderingTestData.AllocationSql(Guid.NewGuid(), Guid.NewGuid(), placed.WarehouseId, placed.VariantId, secondMovement),
            "fk_order_inventory_allocations_order_item");
        await Data.AssertViolationAsync(
            "23503",
            OrderingTestData.AllocationSql(Guid.NewGuid(), placed.ItemId, Guid.NewGuid(), placed.VariantId, secondMovement),
            "fk_order_inventory_allocations_warehouse");
        await Data.AssertViolationAsync(
            "23503",
            OrderingTestData.AllocationSql(Guid.NewGuid(), placed.ItemId, await Data.InsertWarehouseAsync(), Guid.NewGuid(), secondMovement),
            "fk_order_inventory_allocations_product_variant");
        await Data.AssertViolationAsync(
            "23503",
            OrderingTestData.AllocationSql(Guid.NewGuid(), placed.ItemId, await Data.InsertWarehouseAsync(), placed.VariantId, Guid.NewGuid()),
            "fk_order_inventory_allocations_placement_movement");
    }

    [Fact]
    public async Task Referenced_warehouses_variants_and_movements_cannot_be_deleted()
    {
        var placed = await Data.InsertFullOrderAsync();

        // Depo hem hareketten hem dağıtımdan başvurulur: hangisi önce yakalarsa yakalasın RESTRICT'tir.
        var warehouseDelete = await Assert.ThrowsAsync<PostgresException>(
            () => Db.ExecuteAsync($"DELETE FROM warehouses WHERE id = '{placed.WarehouseId}'", Ct));
        Assert.Equal("23503", warehouseDelete.SqlState);
        Assert.Contains(
            warehouseDelete.ConstraintName,
            new[] { "fk_order_inventory_allocations_warehouse", "fk_stock_movements_warehouse" });

        // Hareket: stock_movements trigger'ı zaten reddeder (23001); dağıtım FK'si ikinci savunmadır.
        await Data.AssertViolationAsync("23001", $"DELETE FROM stock_movements WHERE id = '{placed.MovementId}'");
        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM order_inventory_allocations WHERE id = '{placed.AllocationId}'", Ct));
    }

    // ---- order_status_history ----

    [Theory]
    [InlineData("processing", "placed", "staff", "ck_order_status_history_first_row")] // 'placed'e yalnız ilk satır (from NULL) gider
    [InlineData("processing", "processing", "staff", "ck_order_status_history_status_changed")]
    [InlineData("processing", "teslim", "staff", "ck_order_status_history_to_status")]
    [InlineData("hazir", "shipped", "staff", "ck_order_status_history_from_status")]
    [InlineData("placed", "processing", "admin", "ck_order_status_history_actor_kind")]
    public async Task History_check_constraints_reject_bad_rows(string from, string to, string actorKind, string constraint)
    {
        var userId = await Data.InsertUserAsync();
        var orderId = await Data.InsertOrderAsync(userId);

        await Data.AssertViolationAsync("23514", OrderingTestData.HistorySql(orderId, userId, from, to, actorKind), constraint);
    }

    [Fact]
    public async Task History_first_row_must_go_to_placed_and_reason_must_not_be_blank_or_too_long()
    {
        var userId = await Data.InsertUserAsync();
        var orderId = await Data.InsertOrderAsync(userId);

        await Data.AssertViolationAsync("23514", OrderingTestData.HistorySql(orderId, userId, null, "processing"), "ck_order_status_history_first_row");
        await Data.AssertViolationAsync("23514", OrderingTestData.HistorySql(orderId, userId, null, "placed", reason: "'   '"), "ck_order_status_history_reason_not_blank");
        await Data.AssertViolationAsync("22001", OrderingTestData.HistorySql(orderId, userId, null, "placed", reason: "repeat('x', 201)"));

        // Geçerli zincir: null → placed, placed → processing (personel), processing → cancelled (personel, nedenli).
        await Db.ExecuteAsync(OrderingTestData.HistorySql(orderId, userId, null, "placed"), Ct);
        await Db.ExecuteAsync(OrderingTestData.HistorySql(orderId, userId, "placed", "processing", "staff"), Ct);
        await Db.ExecuteAsync(OrderingTestData.HistorySql(orderId, userId, "processing", "cancelled", "staff", "'Müşteri istedi'"), Ct);
        Assert.Equal(3L, await Db.ScalarAsync<long>($"SELECT count(*) FROM order_status_history WHERE order_id = '{orderId}'", Ct));
    }

    [Fact]
    public async Task History_foreign_keys_restrict_orders_and_actors()
    {
        var userId = await Data.InsertUserAsync();
        var orderId = await Data.InsertOrderAsync(userId);

        await Data.AssertViolationAsync("23503", OrderingTestData.HistorySql(Guid.NewGuid(), userId, null, "placed"), "fk_order_status_history_order");
        await Data.AssertViolationAsync("23503", OrderingTestData.HistorySql(orderId, Guid.NewGuid(), null, "placed"), "fk_order_status_history_actor_user");
    }

    // ---- stock_movements: yeni işlem türleri ----

    [Theory]
    [InlineData("order_placement", -3)]
    [InlineData("order_cancellation", 3)]
    [InlineData("receiving", 3)]
    public async Task Stock_movements_accept_the_order_operation_types(string type, int delta)
    {
        var userId = await Data.InsertUserAsync();
        var warehouseId = await Data.InsertWarehouseAsync();
        var (variantId, sku) = await Data.InsertVariantAsync();

        await Data.InsertMovementAsync(warehouseId, variantId, sku, userId, delta, type);
    }

    [Theory]
    [InlineData("order_refund")]
    [InlineData("Order_Placement")]
    [InlineData("order_placement ")]
    [InlineData("order-placement")]
    [InlineData("")]
    public async Task Stock_movements_reject_unknown_operation_types(string type)
    {
        var userId = await Data.InsertUserAsync();
        var warehouseId = await Data.InsertWarehouseAsync();
        var (variantId, sku) = await Data.InsertVariantAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => Data.InsertMovementAsync(warehouseId, variantId, sku, userId, -3, type));
        Assert.Equal("23514", exception.SqlState);
        Assert.Equal("ck_stock_movements_operation_type", exception.ConstraintName);
    }

    // ---- Değişmezlik: trigger ----

    [Theory]
    [InlineData("order_items", "UPDATE order_items SET product_name = 'Değişti' WHERE order_id = '{0}'")]
    [InlineData("order_items", "UPDATE order_items SET quantity = 1, line_subtotal = unit_price WHERE order_id = '{0}'")]
    [InlineData("order_items", "DELETE FROM order_items WHERE order_id = '{0}'")]
    [InlineData("order_inventory_allocations", "UPDATE order_inventory_allocations SET quantity = 1 WHERE id = '{1}'")]
    [InlineData("order_inventory_allocations", "DELETE FROM order_inventory_allocations WHERE id = '{1}'")]
    [InlineData("order_status_history", "UPDATE order_status_history SET reason = 'Değişti' WHERE order_id = '{0}'")]
    [InlineData("order_status_history", "DELETE FROM order_status_history WHERE order_id = '{0}'")]
    public async Task Append_only_order_tables_reject_update_and_delete_and_stay_intact(string table, string template)
    {
        var placed = await Data.InsertFullOrderAsync();
        var before = await TableFingerprintAsync(table, placed);

        await Data.AssertViolationAsync("23001", string.Format(System.Globalization.CultureInfo.InvariantCulture, template, placed.OrderId, placed.AllocationId));

        Assert.Equal(before, await TableFingerprintAsync(table, placed));
    }

    [Theory]
    [InlineData("order_items")]
    [InlineData("order_inventory_allocations")]
    [InlineData("order_status_history")]
    [InlineData("orders")]
    public async Task Truncate_of_order_tables_is_rejected(string table)
    {
        var placed = await Data.InsertFullOrderAsync();

        // Başka tablodan FK ile başvurulan bir tabloda düz TRUNCATE, trigger'dan önce PostgreSQL'in kendi kuralına (0A000) takılabilir.
        // Her iki durumda da boşaltılamaz; CASCADE ise referans kontrolünü aşar ve DOĞRUDAN trigger'ı (23001) sınar.
        var plain = await Assert.ThrowsAsync<PostgresException>(() => Db.ExecuteAsync($"TRUNCATE TABLE {table}", Ct));
        Assert.Contains(plain.SqlState, new[] { "23001", "0A000" });
        await Data.AssertViolationAsync("23001", $"TRUNCATE TABLE {table} CASCADE");

        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM orders WHERE id = '{placed.OrderId}'", Ct));
        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM order_items WHERE order_id = '{placed.OrderId}'", Ct));
        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM order_inventory_allocations WHERE id = '{placed.AllocationId}'", Ct));
        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM order_status_history WHERE order_id = '{placed.OrderId}'", Ct));
    }

    [Fact]
    public async Task Orders_cannot_be_deleted()
    {
        var placed = await Data.InsertFullOrderAsync();
        var userId = await Data.InsertUserAsync();
        var lonelyOrder = await Data.InsertOrderAsync(userId); // hiçbir şeyin başvurmadığı sipariş: FK değil trigger reddetmeli

        await Data.AssertViolationAsync("23001", $"DELETE FROM orders WHERE id = '{lonelyOrder}'");
        await Data.AssertViolationAsync("23001", $"DELETE FROM orders WHERE id = '{placed.OrderId}'");
        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM orders WHERE id = '{lonelyOrder}'", Ct));
    }

    [Fact]
    public async Task Orders_allow_status_and_updated_at_updates_only()
    {
        var userId = await Data.InsertUserAsync();
        var orderId = await Data.InsertOrderAsync(userId);

        // İzinli: yalnız status, yalnız updated_at, ikisi birlikte, değişiklik içermeyen yeniden atama.
        await Db.ExecuteAsync($"UPDATE orders SET status = 'processing' WHERE id = '{orderId}'", Ct);
        await Db.ExecuteAsync($"UPDATE orders SET updated_at = now() + interval '1 second' WHERE id = '{orderId}'", Ct);
        await Db.ExecuteAsync($"UPDATE orders SET status = 'shipped', updated_at = now() + interval '2 seconds' WHERE id = '{orderId}'", Ct);
        await Db.ExecuteAsync($"UPDATE orders SET delivery_label = delivery_label, item_subtotal = item_subtotal WHERE id = '{orderId}'", Ct);
        Assert.Equal("shipped", await Db.ScalarAsync<string>($"SELECT status FROM orders WHERE id = '{orderId}'", Ct));

        // CHECK trigger'dan bağımsız çalışmaya devam eder.
        await Data.AssertViolationAsync("23514", $"UPDATE orders SET status = 'paid' WHERE id = '{orderId}'", "ck_orders_status");
    }

    [Theory]
    [InlineData("item_subtotal = item_subtotal + 1")]
    [InlineData("delivery_label = 'Başka'")]
    [InlineData("delivery_recipient_name = 'Başka Kişi'")]
    [InlineData("delivery_phone = '5551112233'")]
    [InlineData("delivery_province = 'Ankara'")]
    [InlineData("delivery_district = 'Çankaya'")]
    [InlineData("delivery_street_address = 'Başka adres'")]
    [InlineData("delivery_postal_code = NULL")]
    [InlineData("idempotency_key = 'degistirilmis-anahtar-123456'")]
    [InlineData("request_fingerprint = repeat('d', 64)")]
    [InlineData("created_at = created_at + interval '1 day'")]
    [InlineData("order_number = DEFAULT")]
    [InlineData("id = gen_random_uuid()")]
    [InlineData("user_id = (SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"Id\" <> orders.user_id LIMIT 1)")]
    [InlineData("status = 'processing', item_subtotal = item_subtotal + 1")] // izinli bir sütunla birlikte olsa da reddedilir
    public async Task Orders_reject_updates_to_snapshot_and_identity_columns(string assignment)
    {
        var userId = await Data.InsertUserAsync();
        await Data.InsertUserAsync(); // user_id değişiklik testi için başka kullanıcı
        var orderId = await Data.InsertOrderAsync(userId);
        var before = await OrderRowAsync(orderId);

        await Data.AssertViolationAsync("23001", $"UPDATE orders SET {assignment} WHERE id = '{orderId}'");

        Assert.Equal(before, await OrderRowAsync(orderId));
    }

    private async Task<string> OrderRowAsync(Guid orderId) =>
        (await Db.ScalarAsync<string>($"SELECT o::text FROM orders o WHERE o.id = '{orderId}'", Ct))!;

    private async Task<string> TableFingerprintAsync(string table, PlacedOrder placed) => table switch
    {
        "order_items" => (await Db.ScalarAsync<string>($"SELECT string_agg(i::text, ',') FROM order_items i WHERE i.order_id = '{placed.OrderId}'", Ct))!,
        "order_inventory_allocations" => (await Db.ScalarAsync<string>($"SELECT string_agg(a::text, ',') FROM order_inventory_allocations a WHERE a.id = '{placed.AllocationId}'", Ct))!,
        _ => (await Db.ScalarAsync<string>($"SELECT string_agg(h::text, ',') FROM order_status_history h WHERE h.order_id = '{placed.OrderId}'", Ct))!,
    };

    // ---- EF eşlemesi ----

    [Fact]
    public async Task Order_graph_round_trips_through_ef_and_the_identity_is_read_back()
    {
        var now = new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero);
        var userId = await Data.InsertUserAsync();
        var warehouseId = await Data.InsertWarehouseAsync();
        var (variantId, sku) = await Data.InsertVariantAsync();
        var address = new CustomerAddress(userId, "Ev", "Ali Veli", "0532 123 45 67", "İstanbul", "Kadıköy", "Moda Cd. No 1", "34710");
        var key = OrderingTestData.NewKey();
        var order = new Order(userId, key, OrderingTestData.Fingerprint, address, 30.00m);
        var item = new OrderItem(
            order.Id, variantId, sku, "Kablosuz Kulaklık", "Siyah", "kablosuz-kulaklik", "/images/products/kulaklik.webp", "Kulaklık", 10.00m, 3);
        var movement = new StockMovement(
            warehouseId, variantId, sku, -3, StockOperationType.OrderPlacement, userId, "Sipariş", "SS-100001",
            $"order-place-{OrderingTestData.Unique()}{OrderingTestData.Unique()}", OrderingTestData.Fingerprint, 2, now.ToOffset(TimeSpan.FromHours(3)));
        var allocation = new OrderInventoryAllocation(item.Id, warehouseId, variantId, 3, movement.Id);
        var history = order.CreateInitialHistory(now);

        Assert.Equal(0, order.OrderNumber); // ekleme öncesi: veritabanı atayacak

        await using (var context = Db.CreateDbContext(new FixedTimeProvider(now)))
        {
            // Hareket önce: dağıtım ona FK ile bağlı. EF bağımlılık sırasını kendisi çözer, yine de açıkça sıraladık.
            context.StockMovements.Add(movement);
            context.Orders.Add(order);
            context.OrderItems.Add(item);
            context.OrderInventoryAllocations.Add(allocation);
            context.OrderStatusHistory.Add(history);
            await context.SaveChangesAsync(Ct);
        }

        // EF, identity değerini ekleme sonrası aynı nesneye geri okudu.
        Assert.True(order.OrderNumber >= OrderRules.InitialOrderNumber);
        Assert.Equal($"SS-{order.OrderNumber}", order.DisplayNumber);

        await using var read = Db.CreateDbContext();
        var loaded = await read.Orders.SingleAsync(o => o.Id == order.Id, Ct);
        Assert.Equal(order.OrderNumber, loaded.OrderNumber);
        Assert.Equal(OrderStatus.Placed, loaded.Status);
        Assert.Equal("5321234567", loaded.DeliveryPhone);
        Assert.Equal("34710", loaded.DeliveryPostalCode);
        Assert.Equal(30.00m, loaded.ItemSubtotal);
        Assert.Null(loaded.ShippingAmount);
        Assert.Null(loaded.GrandTotal);
        Assert.Equal("shippingNotCalculated", loaded.ShippingStatus);
        Assert.Equal("TRY", loaded.Currency);
        Assert.Equal(now, loaded.CreatedAt);
        Assert.Equal(now, loaded.UpdatedAt);
        Assert.Equal(TimeSpan.Zero, loaded.CreatedAt.Offset);
        Assert.Equal("placed", await Db.ScalarAsync<string>($"SELECT status FROM orders WHERE id = '{order.Id}'", Ct));

        var loadedItem = await read.OrderItems.SingleAsync(i => i.Id == item.Id, Ct);
        Assert.Equal((30.00m, 10.00m, 3), (loadedItem.LineSubtotal, loadedItem.UnitPrice, loadedItem.Quantity));
        Assert.Equal("/images/products/kulaklik.webp", loadedItem.ImageUrl);

        var loadedAllocation = await read.OrderInventoryAllocations.SingleAsync(a => a.Id == allocation.Id, Ct);
        Assert.Equal(movement.Id, loadedAllocation.PlacementMovementId);

        var loadedMovement = await read.StockMovements.SingleAsync(m => m.Id == movement.Id, Ct);
        Assert.Equal(StockOperationType.OrderPlacement, loadedMovement.OperationType);
        Assert.Equal("order_placement", await Db.ScalarAsync<string>($"SELECT operation_type FROM stock_movements WHERE id = '{movement.Id}'", Ct));

        var loadedHistory = await read.OrderStatusHistory.SingleAsync(h => h.Id == history.Id, Ct);
        Assert.Null(loadedHistory.FromStatus);
        Assert.Equal(OrderStatus.Placed, loadedHistory.ToStatus);
        Assert.Equal(OrderActorKind.Customer, loadedHistory.ActorKind);
        Assert.Null(await Db.ScalarAsync<string>($"SELECT from_status FROM order_status_history WHERE id = '{history.Id}'", Ct));
        Assert.Equal("customer", await Db.ScalarAsync<string>($"SELECT actor_kind FROM order_status_history WHERE id = '{history.Id}'", Ct));
    }

    [Fact]
    public async Task A_status_change_saved_through_ef_passes_the_trigger_and_stamps_updated_at()
    {
        var created = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        var later = created.AddHours(2);
        var userId = await Data.InsertUserAsync();
        var staffId = await Data.InsertUserAsync();
        var address = new CustomerAddress(userId, "Ev", "Ali Veli", "5321234567", "Ankara", "Çankaya", "Atatürk Blv. 5", null);
        var order = new Order(userId, OrderingTestData.NewKey(), OrderingTestData.Fingerprint, address, 30.00m);

        await using (var context = Db.CreateDbContext(new FixedTimeProvider(created)))
        {
            context.Orders.Add(order);
            context.OrderStatusHistory.Add(order.CreateInitialHistory(created));
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = Db.CreateDbContext(new FixedTimeProvider(later)))
        {
            var tracked = await context.Orders.SingleAsync(o => o.Id == order.Id, Ct);
            var entry = tracked.ChangeStatus(OrderStatus.Processing, OrderActor.Warehouse, staffId, null, later);
            context.OrderStatusHistory.Add(entry);
            await context.SaveChangesAsync(Ct); // UPDATE yalnız status + updated_at yazar: trigger izin verir
        }

        await using var read = Db.CreateDbContext();
        var loaded = await read.Orders.SingleAsync(o => o.Id == order.Id, Ct);
        Assert.Equal(OrderStatus.Processing, loaded.Status);
        Assert.Equal(created, loaded.CreatedAt);
        Assert.Equal(later, loaded.UpdatedAt);
        Assert.Null(loaded.DeliveryPostalCode);

        var rows = await read.OrderStatusHistory.Where(h => h.OrderId == order.Id).OrderBy(h => h.OccurredAt).ToListAsync(Ct);
        Assert.Equal(2, rows.Count);
        Assert.Equal(OrderStatus.Placed, rows[1].FromStatus);
        Assert.Equal(OrderStatus.Processing, rows[1].ToStatus);
        Assert.Equal(OrderActorKind.Staff, rows[1].ActorKind);
    }

    [Fact]
    public async Task Removing_a_tracked_order_item_through_ef_is_rejected_by_the_trigger()
    {
        var placed = await Data.InsertFullOrderAsync();

        await using var context = Db.CreateDbContext();
        var tracked = await context.OrderItems.SingleAsync(i => i.Id == placed.ItemId, Ct);
        context.OrderItems.Remove(tracked);
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
        var inner = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal("23001", inner.SqlState);
        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM order_items WHERE id = '{placed.ItemId}'", Ct));
    }

    [Fact]
    public async Task Two_concurrent_inserts_of_the_same_user_and_key_leave_exactly_one_order()
    {
        // Gerçek yarış: son savunma hattı ux_orders_user_id_idempotency_key'dir (servis ayrıca advisory lock alır).
        var userId = await Data.InsertUserAsync();
        var key = OrderingTestData.NewKey();
        var sql1 = OrderingTestData.OrderSql(Guid.NewGuid(), userId, With("idempotency_key", $"'{key}'"));
        var sql2 = OrderingTestData.OrderSql(Guid.NewGuid(), userId, With("idempotency_key", $"'{key}'"));

        var results = await Task.WhenAll(TryAsync(sql1), TryAsync(sql2));

        Assert.Equal(1, results.Count(r => r is null));
        Assert.Equal("23505", Assert.Single(results, r => r is not null));
        Assert.Equal(1L, await Db.ScalarAsync<long>($"SELECT count(*) FROM orders WHERE user_id = '{userId}'", Ct));
    }

    private async Task<string?> TryAsync(string sql)
    {
        try
        {
            await Db.ExecuteAsync(sql, Ct);
            return null;
        }
        catch (PostgresException exception)
        {
            return exception.SqlState;
        }
    }
}

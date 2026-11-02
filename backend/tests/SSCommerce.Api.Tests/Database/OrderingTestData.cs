using Npgsql;

namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// Faz 7 sipariş testleri için HAM SQL veri kurucuları. Domain sınıfları yanlış girdiyi zaten reddeder; buradaki soru "elle SQL ya da uygulama
/// hatası kirli veri üretebilir mi?" sorusudur (savunma derinliği). Her çağrı kendi benzersiz verisini kurar (isolation).
/// SqlState: 23505 unique, 23514 check, 23503 foreign key, 23001 değişmezlik trigger'ı (restrict_violation), 428C9 GENERATED ALWAYS identity.
/// </summary>
internal sealed class OrderingTestData(PostgresTestDatabase db)
{
    public static readonly string Fingerprint = new('c', 64);

    private const string EmptyJson = "{}";

    public PostgresTestDatabase Db { get; } = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string Unique() => Guid.NewGuid().ToString("N")[..12];

    public static string NewKey() => $"order-key-{Unique()}-{Unique()}";

    public async Task<Guid> InsertUserAsync()
    {
        var id = Guid.NewGuid();
        var suffix = Unique().ToUpperInvariant();
        await Db.ExecuteAsync(
            $"""
            INSERT INTO "AspNetUsers" ("Id", "CreatedAt", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "EmailConfirmed",
                "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES ('{id}', now(), 'u{suffix}', 'U{suffix}', 'u{suffix}@ornek.test', 'U{suffix}@ORNEK.TEST', false, false, false, true, 0)
            """,
            Ct);
        return id;
    }

    public async Task<Guid> InsertWarehouseAsync()
    {
        var id = Guid.NewGuid();
        await Db.ExecuteAsync(
            $"INSERT INTO warehouses (id, code, name, is_active, created_at, updated_at) VALUES ('{id}', 'W-{Unique().ToUpperInvariant()}', 'Test depo', true, now(), now())",
            Ct);
        return id;
    }

    public async Task<(Guid VariantId, string Sku)> InsertVariantAsync()
    {
        var categoryId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var sku = $"ORD-{Unique().ToUpperInvariant()}";
        await Db.ExecuteAsync(
            $"INSERT INTO categories (id, name, slug, parent_id, sort_order, is_active, created_at, updated_at) VALUES ('{categoryId}', 'K', 'kat-{Unique()}', NULL, 0, true, now(), now())", Ct);
        await Db.ExecuteAsync(
            $"INSERT INTO products (id, category_id, name, slug, is_active, created_at, updated_at) VALUES ('{productId}', '{categoryId}', 'U', 'urun-{Unique()}', true, now(), now())", Ct);
        await Db.ExecuteAsync(
            $"""
            INSERT INTO product_variants (id, product_id, sku, display_name, options, option_signature, price, currency, is_active, created_at, updated_at)
            VALUES ('{variantId}', '{productId}', '{sku}', 'V', '{EmptyJson}'::jsonb, 'default', 10.00, 'TRY', true, now(), now())
            """,
            Ct);
        return (variantId, sku);
    }

    /// <summary>Geçerli bir sipariş satırı ekler. <paramref name="overrides"/> sütun adı → SQL değeri (tırnaklı metin dahil) ile varsayılanı ezer.</summary>
    public async Task<Guid> InsertOrderAsync(Guid userId, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var id = Guid.NewGuid();
        await Db.ExecuteAsync(OrderSql(id, userId, overrides), Ct);
        return id;
    }

    public static string OrderSql(Guid id, Guid userId, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("id", $"'{id}'"),
            new("user_id", $"'{userId}'"),
            new("idempotency_key", $"'{NewKey()}'"),
            new("request_fingerprint", $"'{Fingerprint}'"),
            new("status", "'placed'"),
            new("delivery_label", "'Ev'"),
            new("delivery_recipient_name", "'Ali Veli'"),
            new("delivery_phone", "'5321234567'"),
            new("delivery_province", "'İstanbul'"),
            new("delivery_district", "'Kadıköy'"),
            new("delivery_street_address", "'Moda Cd. No 1'"),
            new("delivery_postal_code", "'34710'"),
            new("currency", "'TRY'"),
            new("item_subtotal", "30.00"),
            new("shipping_status", "'shippingNotCalculated'"),
            new("shipping_amount", "NULL"),
            new("grand_total", "NULL"),
            new("created_at", "now()"),
            new("updated_at", "now()"),
        };

        if (overrides is not null)
        {
            foreach (var (column, value) in overrides)
            {
                var index = values.FindIndex(v => v.Key == column);
                if (index < 0)
                {
                    throw new ArgumentException($"Bilinmeyen sütun: {column}", nameof(overrides));
                }

                values[index] = new(column, value);
            }
        }

        return $"INSERT INTO orders ({string.Join(", ", values.Select(v => v.Key))}) VALUES ({string.Join(", ", values.Select(v => v.Value))})";
    }

    public static string ItemSql(
        Guid id,
        Guid orderId,
        Guid variantId,
        string sku,
        string unitPrice = "10.00",
        string quantity = "3",
        string lineSubtotal = "30.00",
        string currency = "TRY",
        string productSlug = "kablosuz-kulaklik",
        string imageUrl = "'/images/products/kulaklik.webp'",
        string productName = "Kablosuz Kulaklık",
        string variantName = "Siyah") =>
        $"""
        INSERT INTO order_items (id, order_id, product_variant_id, sku, product_name, variant_name, product_slug, image_url, image_alt,
            unit_price, quantity, line_subtotal, currency)
        VALUES ('{id}', '{orderId}', '{variantId}', '{sku}', '{productName}', '{variantName}', '{productSlug}', {imageUrl}, 'Kulaklık',
            {unitPrice}, {quantity}, {lineSubtotal}, '{currency}')
        """;

    public async Task<Guid> InsertItemAsync(Guid orderId, Guid variantId, string sku)
    {
        var id = Guid.NewGuid();
        await Db.ExecuteAsync(ItemSql(id, orderId, variantId, sku), Ct);
        return id;
    }

    public async Task<Guid> InsertMovementAsync(Guid warehouseId, Guid variantId, string sku, Guid actorId, int delta = -3, string type = "order_placement")
    {
        var id = Guid.NewGuid();
        await Db.ExecuteAsync(
            $"""
            INSERT INTO stock_movements (id, warehouse_id, product_variant_id, sku, quantity_delta, operation_type, actor_user_id, reason, reference,
                operation_key, request_fingerprint, on_hand_after, occurred_at)
            VALUES ('{id}', '{warehouseId}', '{variantId}', '{sku}', {delta}, '{type}', '{actorId}', 'Sipariş', 'SS-100001',
                'order-place-{Unique()}{Unique()}', '{Fingerprint}', 5, now())
            """,
            Ct);
        return id;
    }

    public static string AllocationSql(Guid id, Guid itemId, Guid warehouseId, Guid variantId, Guid movementId, string quantity = "3") =>
        $"""
        INSERT INTO order_inventory_allocations (id, order_item_id, warehouse_id, product_variant_id, quantity, placement_movement_id)
        VALUES ('{id}', '{itemId}', '{warehouseId}', '{variantId}', {quantity}, '{movementId}')
        """;

    public static string HistorySql(Guid orderId, Guid actorId, string? from, string to, string actorKind = "customer", string reason = "NULL") =>
        $"""
        INSERT INTO order_status_history (id, order_id, from_status, to_status, actor_user_id, actor_kind, reason, occurred_at)
        VALUES ('{Guid.NewGuid()}', '{orderId}', {(from is null ? "NULL" : $"'{from}'")}, '{to}', '{actorId}', '{actorKind}', {reason}, now())
        """;

    /// <summary>Bir siparişin tam iskeleti: kullanıcı, depo, varyant, sipariş, kalem, düşüm hareketi, dağıtım, ilk geçmiş satırı.</summary>
    public async Task<PlacedOrder> InsertFullOrderAsync()
    {
        var userId = await InsertUserAsync();
        var warehouseId = await InsertWarehouseAsync();
        var (variantId, sku) = await InsertVariantAsync();
        var orderId = await InsertOrderAsync(userId);
        var itemId = await InsertItemAsync(orderId, variantId, sku);
        var movementId = await InsertMovementAsync(warehouseId, variantId, sku, userId);
        var allocationId = Guid.NewGuid();
        await Db.ExecuteAsync(AllocationSql(allocationId, itemId, warehouseId, variantId, movementId), Ct);
        await Db.ExecuteAsync(HistorySql(orderId, userId, null, "placed"), Ct);
        return new PlacedOrder(userId, warehouseId, variantId, sku, orderId, itemId, movementId, allocationId);
    }

    public async Task AssertViolationAsync(string expectedSqlState, string sql, string? expectedConstraint = null)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => Db.ExecuteAsync(sql, Ct));
        Assert.Equal(expectedSqlState, exception.SqlState);
        if (expectedConstraint is not null)
        {
            // Hata gerçekten beklenen kısıttan mı geldi? (Başka bir nedenle patlayıp testin yanlışlıkla geçmesini önler.)
            Assert.Equal(expectedConstraint, exception.ConstraintName);
        }
    }
}

internal sealed record PlacedOrder(
    Guid UserId,
    Guid WarehouseId,
    Guid VariantId,
    string Sku,
    Guid OrderId,
    Guid ItemId,
    Guid MovementId,
    Guid AllocationId);

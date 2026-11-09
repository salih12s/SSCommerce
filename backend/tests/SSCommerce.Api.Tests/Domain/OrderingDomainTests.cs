using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Customers;
using SSCommerce.Domain.Inventory;
using SSCommerce.Domain.Ordering;

namespace SSCommerce.Api.Tests.Domain;

/// <summary>Saf unit testler (Faz 7 sipariş domain'i): veritabanı ve ağ yok.</summary>
public class OrderingDomainTests
{
    private static readonly string Fingerprint = new('a', 64);
    private const string Key = "order-key-0123456789";
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid StaffId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 30, 0, TimeSpan.Zero);

    private static CustomerAddress Address(Guid? userId = null) =>
        new(userId ?? UserId, "Ev", "Ali Veli", "0532 123 45 67", "İstanbul", "Kadıköy", "Moda Cd. No 1", "34710");

    private static Order NewOrder(decimal subtotal = 30.00m) => new(UserId, Key, Fingerprint, Address(), subtotal);

    private static OrderItem NewItem(
        Guid? orderId = null,
        string sku = "SSC-3001-STD",
        string? imageUrl = "/images/products/kulaklik.webp",
        string? imageAlt = "Kulaklık",
        decimal unitPrice = 10.00m,
        int quantity = 3) =>
        new(orderId ?? Guid.NewGuid(), Guid.NewGuid(), sku, "Kablosuz Kulaklık", "Siyah", "kablosuz-kulaklik", imageUrl, imageAlt, unitPrice, quantity);

    // ---- Sabitler (sözleşme) ----

    [Fact]
    public void Rule_constants_match_the_contract()
    {
        Assert.Equal(100001L, OrderRules.InitialOrderNumber);
        Assert.Equal(20, OrderRules.MaxLineQuantity);
        Assert.Equal(200, OrderRules.ReasonMaxLength);
        Assert.Equal("TRY", OrderRules.Currency);
        Assert.Equal("shippingNotCalculated", OrderRules.ShippingNotCalculated);
        Assert.Equal("^[A-Za-z0-9_-]{16,128}$", OrderRules.IdempotencyKeyPattern);
        Assert.Equal("^[0-9a-f]{64}$", OrderRules.RequestFingerprintPattern);
    }

    [Fact]
    public void Order_numbers_are_formatted_for_display()
    {
        Assert.Equal("SS-100001", OrderRules.FormatOrderNumber(100001));
        Assert.Equal("SS-1234567", OrderRules.FormatOrderNumber(1234567));
        Assert.Throws<OrderRuleException>(() => OrderRules.FormatOrderNumber(0)); // henüz atanmamış numara gösterilmez
        Assert.Throws<OrderRuleException>(() => OrderRules.FormatOrderNumber(100000));
    }

    // ---- Wire eşlemeleri ----

    [Fact]
    public void Status_and_actor_wire_values_round_trip_and_cover_every_enum_value()
    {
        Assert.Equal(new[] { "placed", "processing", "shipped", "completed", "cancelled" }, OrderStatuses.All);
        Assert.Equal(Enum.GetValues<OrderStatus>().Length, OrderStatuses.All.Count);
        foreach (var status in Enum.GetValues<OrderStatus>())
        {
            Assert.Equal(status, OrderStatuses.FromWire(OrderStatuses.ToWire(status)));
        }

        Assert.Equal(new[] { "customer", "staff" }, OrderActorKinds.All);
        foreach (var kind in Enum.GetValues<OrderActorKind>())
        {
            Assert.Equal(kind, OrderActorKinds.FromWire(OrderActorKinds.ToWire(kind)));
        }

        Assert.False(OrderStatuses.TryFromWire("Placed", out _)); // büyük/küçük harfe duyarlı
        Assert.False(OrderStatuses.TryFromWire("paid", out _));
        Assert.False(OrderStatuses.TryFromWire(null, out _));
        Assert.Throws<OrderRuleException>(() => OrderStatuses.FromWire("paid"));
        Assert.Throws<OrderRuleException>(() => OrderStatuses.ToWire((OrderStatus)0));
        Assert.Throws<OrderRuleException>(() => OrderActorKinds.FromWire("admin"));
    }

    // ---- İdempotency anahtarı / parmak izi ----

    [Theory]
    [InlineData("0123456789abcdef")]
    [InlineData("A_b-C_d-E_f-G_h-I")]
    public void Valid_idempotency_keys_are_accepted(string key) => Assert.Equal(key, OrderRules.RequireIdempotencyKey(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short-key")]                       // 16 karakterden kısa
    [InlineData("0123456789abcde ")]                // boşluk
    [InlineData("0123456789abcdef\n")]              // sondaki satır sonu (\z)
    [InlineData("0123456789abcdé!")]                // ASCII dışı
    public void Bad_idempotency_keys_are_rejected(string? key) => Assert.Throws<OrderRuleException>(() => OrderRules.RequireIdempotencyKey(key));

    [Fact]
    public void The_idempotency_key_length_bounds_are_enforced()
    {
        Assert.Equal(128, OrderRules.RequireIdempotencyKey(new string('k', 128)).Length);
        Assert.Throws<OrderRuleException>(() => OrderRules.RequireIdempotencyKey(new string('k', 129)));
        Assert.Throws<OrderRuleException>(() => OrderRules.RequireIdempotencyKey(new string('k', 15)));
    }

    [Theory]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")] // büyük harf
    [InlineData("abc")]
    public void Bad_fingerprints_are_rejected(string fingerprint) =>
        Assert.Throws<OrderRuleException>(() => OrderRules.RequireRequestFingerprint(fingerprint));

    // ---- Order ----

    [Fact]
    public void A_new_order_starts_placed_with_no_shipping_or_grand_total()
    {
        var order = NewOrder();

        Assert.Equal(OrderStatus.Placed, order.Status);
        Assert.Equal(UserId, order.UserId);
        Assert.Equal(Key, order.IdempotencyKey);
        Assert.Equal(Fingerprint, order.RequestFingerprint);
        Assert.Equal("TRY", order.Currency);
        Assert.Equal(30.00m, order.ItemSubtotal);
        Assert.Equal("shippingNotCalculated", order.ShippingStatus);
        Assert.Null(order.ShippingAmount);
        Assert.Null(order.GrandTotal);
        Assert.Equal(0, order.OrderNumber); // veritabanı atar
        Assert.Equal(7, order.Id.Version);  // UUIDv7
        Assert.True(order.CanCustomerCancel);
    }

    [Fact]
    public void The_delivery_snapshot_is_copied_and_normalized_from_the_address()
    {
        var order = NewOrder();

        Assert.Equal("Ev", order.DeliveryLabel);
        Assert.Equal("Ali Veli", order.DeliveryRecipientName);
        Assert.Equal("5321234567", order.DeliveryPhone);
        Assert.Equal("İstanbul", order.DeliveryProvince);
        Assert.Equal("Kadıköy", order.DeliveryDistrict);
        Assert.Equal("Moda Cd. No 1", order.DeliveryStreetAddress);
        Assert.Equal("34710", order.DeliveryPostalCode);
    }

    [Fact]
    public void An_address_edited_after_ordering_does_not_change_the_snapshot()
    {
        var address = Address();
        var order = new Order(UserId, Key, Fingerprint, address, 30.00m);

        address.Update("İş", "Başka Kişi", "5551112233", "Ankara", "Çankaya", "Başka cadde", null);

        Assert.Equal("Ev", order.DeliveryLabel);
        Assert.Equal("5321234567", order.DeliveryPhone);
        Assert.Equal("İstanbul", order.DeliveryProvince);
    }

    [Fact]
    public void An_address_of_another_user_is_rejected()
    {
        Assert.Throws<OrderRuleException>(() => new Order(UserId, Key, Fingerprint, Address(Guid.NewGuid()), 30.00m));
        Assert.Throws<OrderRuleException>(() => new Order(UserId, Key, Fingerprint, null!, 30.00m));
    }

    [Fact]
    public void An_optional_postal_code_may_be_missing()
    {
        var order = new Order(UserId, Key, Fingerprint, "Ev", "Ali Veli", "5321234567", "Ankara", "Çankaya", "Atatürk Blv. 5", "  ", 5.00m);

        Assert.Null(order.DeliveryPostalCode);
    }

    [Theory]
    [InlineData("label", "")]
    [InlineData("recipient", "   ")]
    [InlineData("phone", "4321234567")]
    [InlineData("phone", "123")]
    [InlineData("province", "Atlantis")]
    [InlineData("district", "")]
    [InlineData("street", "")]
    [InlineData("postal", "99999")]
    [InlineData("street", "kontrol	karakteri")]
    public void Invalid_delivery_snapshots_are_rejected(string field, string value)
    {
        string Pick(string name, string fallback) => name == field ? value : fallback;

        Assert.Throws<OrderRuleException>(() => new Order(
            UserId,
            Key,
            Fingerprint,
            Pick("label", "Ev"),
            Pick("recipient", "Ali Veli"),
            Pick("phone", "5321234567"),
            Pick("province", "İstanbul"),
            Pick("district", "Kadıköy"),
            Pick("street", "Moda Cd. No 1"),
            Pick("postal", "34710"),
            30.00m));
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("10.005")]                // kuruştan küçük kesir
    [InlineData("90071992547409.92")]     // Money.MaxAmount üstü
    public void Invalid_item_subtotals_are_rejected(string subtotal) =>
        Assert.Throws<OrderRuleException>(() => NewOrder(decimal.Parse(subtotal, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Boundary_item_subtotals_are_accepted()
    {
        Assert.Equal(0m, NewOrder(0m).ItemSubtotal);
        Assert.Equal(Money.MaxAmount, NewOrder(Money.MaxAmount).ItemSubtotal);
    }

    [Fact]
    public void Constructor_rejects_empty_user_bad_key_and_bad_fingerprint()
    {
        Assert.Throws<OrderRuleException>(() => new Order(Guid.Empty, Key, Fingerprint, "Ev", "Ali Veli", "5321234567", "Ankara", "Çankaya", "Atatürk Blv. 5", null, 1m));
        Assert.Throws<OrderRuleException>(() => new Order(UserId, "kisa", Fingerprint, Address(), 1m));
        Assert.Throws<OrderRuleException>(() => new Order(UserId, Key, "xyz", Address(), 1m));
    }

    // ---- Order.ChangeStatus ----

    [Fact]
    public void ChangeStatus_applies_an_allowed_transition_and_returns_the_history_row()
    {
        var order = NewOrder();

        var entry = order.ChangeStatus(OrderStatus.Processing, OrderActor.Warehouse, StaffId, "  Hazırlanıyor ", Now);

        Assert.Equal(OrderStatus.Processing, order.Status);
        Assert.Equal(order.Id, entry.OrderId);
        Assert.Equal(OrderStatus.Placed, entry.FromStatus);
        Assert.Equal(OrderStatus.Processing, entry.ToStatus);
        Assert.Equal(StaffId, entry.ActorUserId);
        Assert.Equal(OrderActorKind.Staff, entry.ActorKind);
        Assert.Equal("Hazırlanıyor", entry.Reason);
        Assert.Equal(Now, entry.OccurredAt);
        Assert.False(order.CanCustomerCancel);
    }

    [Fact]
    public void A_customer_cancels_their_own_placed_order_as_customer_kind()
    {
        var order = NewOrder();

        var entry = order.ChangeStatus(OrderStatus.Cancelled, OrderActor.Customer, UserId, null, Now);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(OrderActorKind.Customer, entry.ActorKind);
        Assert.Null(entry.Reason);
    }

    [Fact]
    public void ChangeStatus_walks_the_full_forward_path()
    {
        var order = NewOrder();

        order.ChangeStatus(OrderStatus.Processing, OrderActor.Warehouse, StaffId, null, Now);
        order.ChangeStatus(OrderStatus.Shipped, OrderActor.Warehouse, StaffId, null, Now);
        order.ChangeStatus(OrderStatus.Completed, OrderActor.Admin, StaffId, null, Now);

        Assert.Equal(OrderStatus.Completed, order.Status);
        Assert.True(OrderStateMachine.IsTerminal(order.Status));
    }

    [Theory]
    [InlineData(OrderStatus.Placed, OrderActor.Admin)]        // aynı durum
    [InlineData(OrderStatus.Cancelled, OrderActor.Warehouse)] // yetkisiz rol
    [InlineData(OrderStatus.Shipped, OrderActor.Admin)]       // tabloda kenar yok
    [InlineData(OrderStatus.Processing, OrderActor.Customer)] // müşteri iptal dışına geçemez
    public void ChangeStatus_rejects_everything_but_allowed_and_leaves_the_status_unchanged(OrderStatus target, OrderActor actor)
    {
        var order = NewOrder();

        Assert.Throws<OrderRuleException>(() => order.ChangeStatus(target, actor, StaffId, null, Now));

        Assert.Equal(OrderStatus.Placed, order.Status);
    }

    [Fact]
    public void A_failing_history_row_leaves_the_status_unchanged()
    {
        var order = NewOrder();

        Assert.Throws<OrderRuleException>(() => order.ChangeStatus(OrderStatus.Processing, OrderActor.Admin, Guid.Empty, null, Now));
        Assert.Throws<OrderRuleException>(() => order.ChangeStatus(OrderStatus.Processing, OrderActor.Admin, StaffId, new string('x', 201), Now));

        Assert.Equal(OrderStatus.Placed, order.Status);
    }

    [Fact]
    public void The_initial_history_row_goes_from_null_to_placed_by_the_customer()
    {
        var order = NewOrder();

        var entry = order.CreateInitialHistory(Now);

        Assert.Null(entry.FromStatus);
        Assert.Equal(OrderStatus.Placed, entry.ToStatus);
        Assert.Equal(UserId, entry.ActorUserId);
        Assert.Equal(OrderActorKind.Customer, entry.ActorKind);
        Assert.Equal(order.Id, entry.OrderId);
    }

    // ---- OrderItem ----

    [Fact]
    public void An_order_item_snapshots_the_catalog_and_computes_the_line_subtotal()
    {
        var item = NewItem(unitPrice: 0.10m, quantity: 3);

        Assert.Equal(0.30m, item.LineSubtotal);
        Assert.Equal("TRY", item.Currency);
        Assert.Equal(7, item.Id.Version);
        Assert.Equal("SSC-3001-STD", item.Sku);
        Assert.Equal("/images/products/kulaklik.webp", item.ImageUrl);
    }

    [Fact]
    public void An_item_without_an_image_is_valid_but_alt_text_needs_an_image()
    {
        var item = NewItem(imageUrl: null, imageAlt: null);

        Assert.Null(item.ImageUrl);
        Assert.Null(item.ImageAlt);
        Assert.Throws<OrderRuleException>(() => NewItem(imageUrl: null, imageAlt: "Alt metin"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public void Item_quantity_must_be_between_1_and_20(int quantity) => Assert.Throws<OrderRuleException>(() => NewItem(quantity: quantity));

    [Fact]
    public void Item_quantity_bounds_are_accepted()
    {
        Assert.Equal(1, NewItem(quantity: 1).Quantity);
        Assert.Equal(20, NewItem(quantity: 20).Quantity);
    }

    [Fact]
    public void Item_prices_and_line_totals_are_validated()
    {
        Assert.Throws<OrderRuleException>(() => NewItem(unitPrice: -0.01m));
        Assert.Throws<OrderRuleException>(() => NewItem(unitPrice: 1.005m));
        Assert.Throws<OrderRuleException>(() => NewItem(unitPrice: Money.MaxAmount, quantity: 20)); // satır toplamı üst sınırı aşar
        Assert.Equal(0m, NewItem(unitPrice: 0m).LineSubtotal);
    }

    [Theory]
    [InlineData("kucuk-harf")]
    [InlineData("")]
    public void Item_sku_must_follow_the_catalog_rule(string sku) => Assert.Throws<OrderRuleException>(() => NewItem(sku: sku));

    [Fact]
    public void Item_rejects_empty_ids_and_unsafe_image_paths()
    {
        Assert.Throws<OrderRuleException>(() => NewItem(orderId: Guid.Empty));
        Assert.Throws<OrderRuleException>(() => new OrderItem(
            Guid.NewGuid(), Guid.Empty, "SSC-1", "Ad", "Varyant", "slug", null, null, 1m, 1));
        Assert.Throws<OrderRuleException>(() => NewItem(imageUrl: "https://baska.example/x.png"));
        Assert.Throws<OrderRuleException>(() => NewItem(imageUrl: "/images/../etc/x.png"));
    }

    [Fact]
    public void Line_subtotals_sum_to_the_order_subtotal_with_limits()
    {
        Assert.Equal(60.30m, OrderRules.SumLineSubtotals([30.00m, 30.00m, 0.30m]));
        Assert.Throws<OrderRuleException>(() => OrderRules.SumLineSubtotals([]));
        Assert.Throws<OrderRuleException>(() => OrderRules.SumLineSubtotals([Money.MaxAmount, 0.01m]));
        Assert.Throws<OrderRuleException>(() => OrderRules.SumLineSubtotals([-1m]));
    }

    // ---- OrderInventoryAllocation ----

    [Fact]
    public void An_allocation_links_item_warehouse_variant_and_movement()
    {
        var itemId = Guid.NewGuid();
        var warehouseId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var movementId = Guid.NewGuid();

        var allocation = new OrderInventoryAllocation(itemId, warehouseId, variantId, 3, movementId);

        Assert.Equal((itemId, warehouseId, variantId, 3, movementId), (allocation.OrderItemId, allocation.WarehouseId, allocation.ProductVariantId, allocation.Quantity, allocation.PlacementMovementId));
        Assert.Equal(7, allocation.Id.Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public void Allocation_quantity_must_be_between_1_and_20(int quantity) =>
        Assert.Throws<OrderRuleException>(() => new OrderInventoryAllocation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), quantity, Guid.NewGuid()));

    [Fact]
    public void Allocation_rejects_empty_ids()
    {
        Assert.Throws<OrderRuleException>(() => new OrderInventoryAllocation(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid()));
        Assert.Throws<OrderRuleException>(() => new OrderInventoryAllocation(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), 1, Guid.NewGuid()));
        Assert.Throws<OrderRuleException>(() => new OrderInventoryAllocation(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, 1, Guid.NewGuid()));
        Assert.Throws<OrderRuleException>(() => new OrderInventoryAllocation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.Empty));
    }

    // ---- OrderStatusHistoryEntry ----

    [Fact]
    public void A_first_history_row_must_be_null_to_placed()
    {
        var orderId = Guid.NewGuid();

        var first = new OrderStatusHistoryEntry(orderId, null, OrderStatus.Placed, UserId, OrderActorKind.Customer, null, Now);
        Assert.Null(first.FromStatus);

        Assert.Throws<OrderRuleException>(() => new OrderStatusHistoryEntry(orderId, null, OrderStatus.Processing, UserId, OrderActorKind.Customer, null, Now));
        Assert.Throws<OrderRuleException>(() => new OrderStatusHistoryEntry(orderId, OrderStatus.Placed, OrderStatus.Placed, UserId, OrderActorKind.Customer, null, Now));
    }

    [Fact]
    public void A_history_row_must_follow_a_known_transition()
    {
        var orderId = Guid.NewGuid();

        _ = new OrderStatusHistoryEntry(orderId, OrderStatus.Processing, OrderStatus.Cancelled, StaffId, OrderActorKind.Staff, "Stok hatası", Now);
        Assert.Throws<OrderRuleException>(() => new OrderStatusHistoryEntry(orderId, OrderStatus.Shipped, OrderStatus.Cancelled, StaffId, OrderActorKind.Staff, null, Now));
        Assert.Throws<OrderRuleException>(() => new OrderStatusHistoryEntry(orderId, OrderStatus.Completed, OrderStatus.Placed, StaffId, OrderActorKind.Staff, null, Now));
    }

    [Fact]
    public void History_reason_is_trimmed_optional_and_bounded_and_time_is_utc()
    {
        var orderId = Guid.NewGuid();

        var blank = new OrderStatusHistoryEntry(orderId, null, OrderStatus.Placed, UserId, OrderActorKind.Customer, "   ", Now);
        Assert.Null(blank.Reason);
        var max = new OrderStatusHistoryEntry(orderId, null, OrderStatus.Placed, UserId, OrderActorKind.Customer, new string('x', 200), Now);
        Assert.Equal(200, max.Reason!.Length);
        Assert.Throws<OrderRuleException>(() => new OrderStatusHistoryEntry(orderId, null, OrderStatus.Placed, UserId, OrderActorKind.Customer, new string('x', 201), Now));

        var local = new OrderStatusHistoryEntry(orderId, null, OrderStatus.Placed, UserId, OrderActorKind.Customer, null, Now.ToOffset(TimeSpan.FromHours(3)));
        Assert.Equal(TimeSpan.Zero, local.OccurredAt.Offset);
        Assert.Equal(Now, local.OccurredAt);
    }

    [Fact]
    public void History_rejects_empty_ids_and_undefined_enums()
    {
        Assert.Throws<OrderRuleException>(() => new OrderStatusHistoryEntry(Guid.Empty, null, OrderStatus.Placed, UserId, OrderActorKind.Customer, null, Now));
        Assert.Throws<OrderRuleException>(() => new OrderStatusHistoryEntry(Guid.NewGuid(), null, OrderStatus.Placed, Guid.Empty, OrderActorKind.Customer, null, Now));
        Assert.Throws<OrderRuleException>(() => new OrderStatusHistoryEntry(Guid.NewGuid(), null, OrderStatus.Placed, UserId, (OrderActorKind)0, null, Now));
        Assert.Throws<OrderRuleException>(() => new OrderStatusHistoryEntry(Guid.NewGuid(), null, (OrderStatus)0, UserId, OrderActorKind.Customer, null, Now));
    }

    // ---- StockMovement: yeni işlem türleri ----

    private static StockMovement Movement(int delta, StockOperationType type) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "SSC-3001-STD", delta, type, UserId, "Sipariş", "SS-100001", "order-place-0123456789ab", Fingerprint, 5, Now);

    [Fact]
    public void Order_stock_operation_types_have_stable_wire_values_and_are_in_the_check_list()
    {
        Assert.Equal("order_placement", StockOperationTypes.ToWire(StockOperationType.OrderPlacement));
        Assert.Equal("order_cancellation", StockOperationTypes.ToWire(StockOperationType.OrderCancellation));
        Assert.Equal(StockOperationType.OrderPlacement, StockOperationTypes.FromWire("order_placement"));
        Assert.Equal(StockOperationType.OrderCancellation, StockOperationTypes.FromWire("order_cancellation"));
        Assert.Contains("order_placement", StockOperationTypes.All);
        Assert.Contains("order_cancellation", StockOperationTypes.All);
        Assert.Equal(Enum.GetValues<StockOperationType>().Length, StockOperationTypes.All.Count);
        Assert.False(StockOperationTypes.TryFromWire("Order_Placement", out _));
        Assert.False(StockOperationTypes.TryFromWire("order_refund", out _));
    }

    [Fact]
    public void An_order_placement_movement_must_be_negative()
    {
        Assert.Equal(-3, Movement(-3, StockOperationType.OrderPlacement).QuantityDelta);
        Assert.Throws<InventoryRuleException>(() => Movement(3, StockOperationType.OrderPlacement));
        Assert.Throws<InventoryRuleException>(() => Movement(0, StockOperationType.OrderPlacement));
    }

    [Fact]
    public void An_order_cancellation_movement_must_be_positive()
    {
        Assert.Equal(3, Movement(3, StockOperationType.OrderCancellation).QuantityDelta);
        Assert.Throws<InventoryRuleException>(() => Movement(-3, StockOperationType.OrderCancellation));
        Assert.Throws<InventoryRuleException>(() => Movement(0, StockOperationType.OrderCancellation));
    }

    [Fact]
    public void Existing_movement_types_keep_their_sign_rules()
    {
        Assert.Equal(5, Movement(5, StockOperationType.Receiving).QuantityDelta);
        Assert.Throws<InventoryRuleException>(() => Movement(-5, StockOperationType.Receiving));
        Assert.Equal(-5, Movement(-5, StockOperationType.Adjustment).QuantityDelta);
        Assert.Equal(5, Movement(5, StockOperationType.Correction).QuantityDelta);
        Assert.Equal(-5, Movement(-5, StockOperationType.Correction).QuantityDelta);
    }
}

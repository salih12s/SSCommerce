using SSCommerce.Domain.Inventory;

namespace SSCommerce.Api.Tests.Domain;

/// <summary>Saf unit testler (Faz 5 stok domain'i): veritabanı ve ağ yok.</summary>
public class InventoryDomainTests
{
    private static readonly string Fingerprint = new('f', 64);
    private static readonly Guid Id1 = Guid.NewGuid();
    private static readonly Guid Id2 = Guid.NewGuid();
    private static readonly Guid Id3 = Guid.NewGuid();

    private static StockMovement Movement(
        int delta = 5,
        StockOperationType type = StockOperationType.Receiving,
        string sku = "SSC-3001-STD",
        string reason = "Sayım",
        string? reference = null,
        string key = "idem-key-0123456789",
        string? fingerprint = null,
        int onHandAfter = 5) =>
        new(Id1, Id2, sku, delta, type, Id3, reason, reference, key, fingerprint ?? Fingerprint, onHandAfter, DateTimeOffset.UtcNow);

    // ---- Sabitler (sözleşme) ----

    [Fact]
    public void Rule_constants_match_the_contract()
    {
        Assert.Equal(1_000_000, InventoryRules.MaxOnHandQuantity);
        Assert.Equal(1_000_000, InventoryRules.MaxAbsoluteDelta);
        Assert.Equal(20, InventoryRules.PublicAvailabilityCap);
    }

    // ---- Warehouse ----

    [Theory]
    [InlineData("AN")]
    [InlineData("ANKARA-01")]
    [InlineData("1A")]
    [InlineData("A-")]
    public void Warehouse_accepts_valid_codes_and_starts_active(string code)
    {
        var warehouse = new Warehouse(code, "  Merkez depo  ");

        Assert.Equal(code, warehouse.Code);
        Assert.Equal("Merkez depo", warehouse.Name);
        Assert.True(warehouse.IsActive);
        Assert.NotEqual(Guid.Empty, warehouse.Id);
        Assert.Equal(7, warehouse.Id.Version); // UUIDv7
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A")]                                       // 1 karakter
    [InlineData("-AB")]                                     // tireyle başlar
    [InlineData("ab")]                                      // küçük harf
    [InlineData("A_B")]
    [InlineData("A B")]
    [InlineData("AB\n")]                                    // sonda satır sonu: "$" ile geçerdi, "\z" ile geçmez
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]       // 33 karakter
    public void Warehouse_rejects_invalid_codes(string? code)
    {
        Assert.Throws<InventoryRuleException>(() => new Warehouse(code!, "Depo"));
    }

    [Fact]
    public void Warehouse_accepts_a_code_of_exactly_32_characters()
    {
        var code = new string('A', 32);

        Assert.Equal(code, new Warehouse(code, "Depo").Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Warehouse_rejects_blank_names(string? name)
    {
        Assert.Throws<InventoryRuleException>(() => new Warehouse("DEPO", name!));
    }

    [Fact]
    public void Warehouse_rejects_names_longer_than_100_and_rename_applies_the_same_rule()
    {
        Assert.Throws<InventoryRuleException>(() => new Warehouse("DEPO", new string('a', 101)));
        var warehouse = new Warehouse("DEPO", new string('a', 100));

        warehouse.Rename(" Yeni ad ");
        Assert.Equal("Yeni ad", warehouse.Name);
        Assert.Throws<InventoryRuleException>(() => warehouse.Rename(" "));
        Assert.Equal("Yeni ad", warehouse.Name);

        warehouse.SetActive(false);
        Assert.False(warehouse.IsActive);
    }

    // ---- InventoryBalance ----

    [Fact]
    public void Balance_starts_at_version_one()
    {
        var balance = new InventoryBalance(Id1, Id2, 0);

        Assert.Equal((0, 1L), (balance.OnHandQuantity, balance.Version));
        Assert.Equal(Id1, balance.WarehouseId);
        Assert.Equal(Id2, balance.ProductVariantId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void Balance_rejects_quantities_outside_the_range(int onHand)
    {
        Assert.Throws<InventoryRuleException>(() => new InventoryBalance(Id1, Id2, onHand));
    }

    [Fact]
    public void Balance_accepts_zero_and_the_maximum_and_requires_ids()
    {
        Assert.Equal(1_000_000, new InventoryBalance(Id1, Id2, InventoryRules.MaxOnHandQuantity).OnHandQuantity);
        Assert.Throws<InventoryRuleException>(() => new InventoryBalance(Guid.Empty, Id2, 1));
        Assert.Throws<InventoryRuleException>(() => new InventoryBalance(Id1, Guid.Empty, 1));
    }

    // ---- StockMovement ----

    [Fact]
    public void Movement_normalizes_text_and_converts_time_to_utc()
    {
        var local = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(3));
        var movement = new StockMovement(
            Id1, Id2, "SSC-3001-STD", 5, StockOperationType.Receiving, Id3, "  Mal kabul  ", " PO-7 ", "idem-key-0123456789", Fingerprint, 5, local);

        Assert.Equal("Mal kabul", movement.Reason);
        Assert.Equal("PO-7", movement.Reference);
        Assert.Equal(TimeSpan.Zero, movement.OccurredAt.Offset);
        Assert.Equal(local, movement.OccurredAt);
        Assert.Equal(7, movement.Id.Version);
    }

    [Fact]
    public void Movement_treats_a_blank_reference_as_null()
    {
        Assert.Null(Movement(reference: "   ").Reference);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_001)]
    [InlineData(-1_000_001)]
    public void Movement_rejects_zero_and_oversized_deltas(int delta)
    {
        Assert.Throws<InventoryRuleException>(() => Movement(delta: delta, type: StockOperationType.Adjustment));
    }

    [Fact]
    public void Movement_accepts_the_largest_deltas_in_both_directions_for_adjustments()
    {
        Assert.Equal(1_000_000, Movement(delta: 1_000_000, type: StockOperationType.Adjustment, onHandAfter: 1_000_000).QuantityDelta);
        Assert.Equal(-1_000_000, Movement(delta: -1_000_000, type: StockOperationType.Correction, onHandAfter: 0).QuantityDelta);
    }

    [Fact]
    public void Receiving_must_be_positive_but_adjustment_and_correction_may_be_negative()
    {
        Assert.Throws<InventoryRuleException>(() => Movement(delta: -1, type: StockOperationType.Receiving));
        Assert.Equal(-1, Movement(delta: -1, type: StockOperationType.Adjustment, onHandAfter: 0).QuantityDelta);
        Assert.Equal(-1, Movement(delta: -1, type: StockOperationType.Correction, onHandAfter: 0).QuantityDelta);
    }

    [Fact]
    public void Movement_rejects_an_undefined_operation_type()
    {
        Assert.Throws<InventoryRuleException>(() => Movement(type: (StockOperationType)99));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Movement_rejects_a_blank_reason(string reason)
    {
        Assert.Throws<InventoryRuleException>(() => Movement(reason: reason));
    }

    [Fact]
    public void Movement_enforces_reason_and_reference_length_limits()
    {
        Assert.Equal(500, Movement(reason: new string('r', 500)).Reason.Length);
        Assert.Throws<InventoryRuleException>(() => Movement(reason: new string('r', 501)));
        Assert.Equal(128, Movement(reference: new string('x', 128)).Reference!.Length);
        Assert.Throws<InventoryRuleException>(() => Movement(reference: new string('x', 129)));
    }

    [Theory]
    [InlineData("kisa-anahtar")]                // 12 karakter
    [InlineData("bosluk iceren anahtar 123")]
    [InlineData("slash/iceren/anahtar123")]
    [InlineData("idem-key-0123456789\n")]       // sonda satır sonu
    public void Movement_rejects_malformed_operation_keys(string key)
    {
        Assert.Throws<InventoryRuleException>(() => Movement(key: key));
    }

    [Fact]
    public void Movement_accepts_operation_keys_of_16_to_128_characters()
    {
        Assert.Equal(16, Movement(key: new string('k', 16)).OperationKey.Length);
        Assert.Equal(128, Movement(key: new string('k', 128)).OperationKey.Length);
        Assert.Throws<InventoryRuleException>(() => Movement(key: new string('k', 129)));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")]
    public void Movement_rejects_fingerprints_that_are_not_lowercase_sha256_hex(string fingerprint)
    {
        Assert.Throws<InventoryRuleException>(() => Movement(fingerprint: fingerprint));
    }

    [Theory]
    [InlineData("ssc-1")]    // küçük harf
    [InlineData("SC")]       // çok kısa
    [InlineData("")]
    public void Movement_rejects_invalid_skus(string sku)
    {
        Assert.Throws<InventoryRuleException>(() => Movement(sku: sku));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void Movement_rejects_on_hand_after_outside_the_range(int onHandAfter)
    {
        Assert.Throws<InventoryRuleException>(() => Movement(onHandAfter: onHandAfter));
    }

    [Fact]
    public void Movement_requires_non_empty_ids()
    {
        Assert.Throws<InventoryRuleException>(() => new StockMovement(
            Guid.Empty, Id2, "SSC-1", 1, StockOperationType.Receiving, Id3, "x", null, "idem-key-0123456789", Fingerprint, 1, DateTimeOffset.UtcNow));
        Assert.Throws<InventoryRuleException>(() => new StockMovement(
            Id1, Guid.Empty, "SSC-1", 1, StockOperationType.Receiving, Id3, "x", null, "idem-key-0123456789", Fingerprint, 1, DateTimeOffset.UtcNow));
        Assert.Throws<InventoryRuleException>(() => new StockMovement(
            Id1, Id2, "SSC-1", 1, StockOperationType.Receiving, Guid.Empty, "x", null, "idem-key-0123456789", Fingerprint, 1, DateTimeOffset.UtcNow));
    }

    // ---- Operation type metni ----

    [Theory]
    [InlineData(StockOperationType.Receiving, "receiving")]
    [InlineData(StockOperationType.Adjustment, "adjustment")]
    [InlineData(StockOperationType.Correction, "correction")]
    public void Operation_type_round_trips_through_its_database_text(StockOperationType type, string wire)
    {
        Assert.Equal(wire, StockOperationTypes.ToWire(type));
        Assert.Equal(type, StockOperationTypes.FromWire(wire));
        Assert.True(StockOperationTypes.TryFromWire(wire, out var parsed));
        Assert.Equal(type, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Receiving")]
    [InlineData("issue")]
    public void Unknown_operation_type_text_is_not_parsed(string? wire)
    {
        Assert.False(StockOperationTypes.TryFromWire(wire, out _));
        Assert.Throws<InventoryRuleException>(() => StockOperationTypes.FromWire(wire!));
    }

    [Fact]
    public void Operation_type_list_matches_the_enum()
    {
        Assert.Equal(Enum.GetValues<StockOperationType>().Length, StockOperationTypes.All.Count);
    }
}

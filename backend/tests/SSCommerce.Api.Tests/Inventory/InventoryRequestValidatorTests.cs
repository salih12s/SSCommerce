using SSCommerce.Application.Inventory;
using SSCommerce.Domain.Inventory;

namespace SSCommerce.Api.Tests.Inventory;

/// <summary>
/// SAF (unit) testler: veritabanı yok, HTTP yok, girdi -> çıktı. Doğrulayıcı ve parmak izi burada hızlıca ve tüm sınırlarıyla sınanır;
/// aynı kuralların HTTP/DB yolu <see cref="StockApiTests"/> ve <see cref="StockConcurrencyTests"/>'te ayrıca kanıtlanır.
/// </summary>
public class InventoryRequestValidatorTests
{
    private static readonly Guid Warehouse = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Variant = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Actor = new("33333333-3333-3333-3333-333333333333");
    private const string Key = "anahtar-0123456789";

    private static ReceiveStockRequest Receive(long? quantity = 5, string? reason = "Mal kabul", string? reference = null) =>
        new(Warehouse, Variant, quantity, reason, reference);

    private static AdjustStockRequest Adjust(long? delta = -2, string? type = "adjustment", string? reason = "Sayim") =>
        new(Warehouse, Variant, delta, type, reason, null);

    // ---------- receive ----------

    [Fact]
    public void A_valid_receive_becomes_a_positive_receiving_command_with_trimmed_text()
    {
        var result = InventoryRequestValidator.ValidateReceive(Receive(reason: "  Mal kabul  ", reference: "  PO-1 "), Key, Actor);

        Assert.True(result.IsValid);
        var command = result.Value!;
        Assert.Equal(5, command.Delta);
        Assert.Equal(StockOperationType.Receiving, command.Type);
        Assert.Equal("Mal kabul", command.Reason);
        Assert.Equal("PO-1", command.Reference);
        Assert.Equal(Actor, command.ActorUserId);
        Assert.Equal(Key, command.OperationKey);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Receive_quantity_outside_1_to_1_000_000_is_rejected(long quantity)
    {
        var result = InventoryRequestValidator.ValidateReceive(Receive(quantity), Key, Actor);

        Assert.False(result.IsValid);
        Assert.Contains("quantity", result.Errors.Keys);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1_000_000)]
    public void Receive_quantity_at_the_edges_is_accepted(long quantity)
    {
        Assert.True(InventoryRequestValidator.ValidateReceive(Receive(quantity), Key, Actor).IsValid);
    }

    [Fact]
    public void All_errors_are_reported_together_not_just_the_first()
    {
        var result = InventoryRequestValidator.ValidateReceive(new ReceiveStockRequest(null, Guid.Empty, 0, "  ", new string('r', 129)), "kisa", Actor);

        Assert.False(result.IsValid);
        Assert.Equal(
            ["Idempotency-Key", "quantity", "reason", "reference", "variantId", "warehouseId"],
            result.Errors.Keys.Order().ToArray());
    }

    [Fact]
    public void An_over_long_reason_or_reference_is_rejected_at_501_and_129_but_not_at_500_and_128()
    {
        Assert.True(InventoryRequestValidator.ValidateReceive(Receive(reason: new string('r', 500), reference: new string('f', 128)), Key, Actor).IsValid);
        Assert.False(InventoryRequestValidator.ValidateReceive(Receive(reason: new string('r', 501)), Key, Actor).IsValid);
        Assert.False(InventoryRequestValidator.ValidateReceive(Receive(reference: new string('f', 129)), Key, Actor).IsValid);
    }

    [Fact]
    public void A_blank_reference_becomes_null()
    {
        Assert.Null(InventoryRequestValidator.ValidateReceive(Receive(reference: "   "), Key, Actor).Value!.Reference);
    }

    // ---------- adjust ----------

    [Theory]
    [InlineData(-1_000_000)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(1_000_000)]
    public void Adjust_delta_edges_are_accepted(long delta)
    {
        Assert.True(InventoryRequestValidator.ValidateAdjust(Adjust(delta), Key, Actor).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1_000_001)]
    [InlineData(1_000_001)]
    public void Adjust_delta_zero_or_beyond_bounds_is_rejected(long delta)
    {
        var result = InventoryRequestValidator.ValidateAdjust(Adjust(delta), Key, Actor);

        Assert.False(result.IsValid);
        Assert.Contains("quantityDelta", result.Errors.Keys);
    }

    [Theory]
    [InlineData("adjustment", StockOperationType.Adjustment)]
    [InlineData("correction", StockOperationType.Correction)]
    public void Adjust_accepts_only_adjustment_and_correction(string type, StockOperationType expected)
    {
        Assert.Equal(expected, InventoryRequestValidator.ValidateAdjust(Adjust(type: type), Key, Actor).Value!.Type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("receiving")]
    [InlineData("Adjustment")]
    [InlineData("adjustment ")]
    [InlineData("gift")]
    public void Adjust_rejects_every_other_type(string? type)
    {
        var result = InventoryRequestValidator.ValidateAdjust(Adjust(type: type), Key, Actor);

        Assert.False(result.IsValid);
        Assert.Contains("type", result.Errors.Keys);
    }

    // ---------- Idempotency-Key ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789abcde")]            // 15
    [InlineData("0123456789abcdef\n")]         // sondaki satır sonu ($ tuzağı)
    [InlineData("0123456789 abcdef")]
    [InlineData("0123456789abcdeğ")]
    public void Bad_keys_are_rejected(string? key)
    {
        var result = InventoryRequestValidator.ValidateReceive(Receive(), key, Actor);

        Assert.False(result.IsValid);
        Assert.Contains("Idempotency-Key", result.Errors.Keys);
    }

    [Fact]
    public void Keys_of_16_and_128_characters_are_accepted_and_129_is_rejected()
    {
        Assert.True(InventoryRequestValidator.ValidateReceive(Receive(), new string('a', 16), Actor).IsValid);
        Assert.True(InventoryRequestValidator.ValidateReceive(Receive(), new string('a', 128) , Actor).IsValid);
        Assert.False(InventoryRequestValidator.ValidateReceive(Receive(), new string('a', 129), Actor).IsValid);
    }

    // ---------- Kontrol karakterleri (NUL Npgsql'de 22021 => 500 olurdu) ----------

    [Theory]
    [InlineData("a\0b")]
    [InlineData("\0")]
    [InlineData("satir\nsonu")]
    [InlineData("sekme\there")]
    [InlineData("bel\u0007")]
    [InlineData("del\u007f")]
    [InlineData("c1\u0085kontrol")]
    public void Control_characters_in_reason_and_reference_are_rejected(string text)
    {
        var reason = InventoryRequestValidator.ValidateReceive(Receive(reason: text), Key, Actor);
        var reference = InventoryRequestValidator.ValidateReceive(Receive(reference: text), Key, Actor);
        var adjust = InventoryRequestValidator.ValidateAdjust(Adjust(reason: text), Key, Actor);

        Assert.Contains("reason", reason.Errors.Keys);
        Assert.Contains("reference", reference.Errors.Keys);
        Assert.Contains("reason", adjust.Errors.Keys);
    }

    [Theory]
    [InlineData("a\0b")]
    [InlineData("satir\nsonu")]
    public void Control_characters_in_a_warehouse_name_are_rejected_on_create_and_update(string name)
    {
        Assert.Contains("name", InventoryRequestValidator.ValidateCreateWarehouse(new CreateWarehouseRequest("AB-1", name)).Errors.Keys);
        Assert.Contains("name", InventoryRequestValidator.ValidateUpdateWarehouse(new UpdateWarehouseRequest(name, null)).Errors.Keys);
    }

    [Fact]
    public void Ordinary_unicode_text_and_surrounding_whitespace_are_still_accepted()
    {
        var result = InventoryRequestValidator.ValidateReceive(Receive(reason: "  Çanakkale şubesi: 12 adet İade  ", reference: "PO-ÇŞ-1"), Key, Actor);

        Assert.True(result.IsValid);
        Assert.Equal("Çanakkale şubesi: 12 adet İade", result.Value!.Reason);
    }

    // ---------- Parmak izi ----------

    private static StockCommand Command(
        int delta = 5,
        string? reason = "Mal kabul",
        string? reference = null,
        Guid? warehouse = null,
        Guid? actor = null,
        string key = Key) =>
        InventoryRequestValidator.ValidateReceive(
            new ReceiveStockRequest(warehouse ?? Warehouse, Variant, delta, reason, reference), key, actor ?? Actor).Value!;

    [Fact]
    public void The_fingerprint_is_64_lowercase_hex_and_deterministic()
    {
        var fingerprint = Command().Fingerprint();

        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
        Assert.Equal(fingerprint, Command().Fingerprint());
        InventoryRules.RequireRequestFingerprint(fingerprint); // alan kuralıyla (DB CHECK ile aynı desen) uyumlu
    }

    [Fact]
    public void The_fingerprint_ignores_surrounding_whitespace_the_actor_and_the_key()
    {
        var baseline = Command().Fingerprint();

        Assert.Equal(baseline, Command(reason: "  Mal kabul ").Fingerprint());                          // kırpılmış neden
        Assert.Equal(baseline, Command(actor: Guid.NewGuid()).Fingerprint());                            // aktör anlama girmez (bkz. StockCommand.Fingerprint)
        Assert.Equal(baseline, Command(key: "baska-anahtar-1234567").Fingerprint());                     // anahtar kendisi saklanır, özetlenmez
    }

    [Fact]
    public void Every_semantic_field_changes_the_fingerprint()
    {
        var baseline = Command().Fingerprint();
        var prints = new[]
        {
            Command(delta: 6).Fingerprint(),
            Command(reason: "Başka neden").Fingerprint(),
            Command(reference: "PO-1").Fingerprint(),
            Command(warehouse: Guid.NewGuid()).Fingerprint(),
            InventoryRequestValidator.ValidateAdjust(Adjust(5, "adjustment"), Key, Actor).Value!.Fingerprint(),  // aynı delta, başka uç/tür
            InventoryRequestValidator.ValidateAdjust(Adjust(5, "correction"), Key, Actor).Value!.Fingerprint(),
        };

        Assert.DoesNotContain(baseline, prints);
        Assert.Equal(prints.Length, prints.Distinct().Count());
    }

    [Fact]
    public void Field_boundaries_cannot_be_shifted_to_forge_the_same_fingerprint()
    {
        // Uzunluk önekli kodlama: neden/referans arasında metin kaydırmak aynı özeti üretmez.
        var one = Command(reason: "ab", reference: "c").Fingerprint();
        var two = Command(reason: "a", reference: "bc").Fingerprint();
        var nullReference = Command(reason: "ab").Fingerprint();

        Assert.NotEqual(one, two);
        Assert.NotEqual(one, nullReference);
    }

    // ---------- Depo / sorgu ----------

    [Theory]
    [InlineData("AB", true)]
    [InlineData("MAIN-01", true)]
    [InlineData("A", false)]
    [InlineData("-AB", false)]
    [InlineData("ab", false)]
    [InlineData("AB\nCD", false)]
    public void Warehouse_codes_follow_the_documented_pattern(string code, bool valid)
    {
        Assert.Equal(valid, InventoryRequestValidator.ValidateCreateWarehouse(new CreateWarehouseRequest(code, "Ana")).IsValid);
    }

    [Fact]
    public void A_warehouse_update_needs_at_least_one_field()
    {
        Assert.False(InventoryRequestValidator.ValidateUpdateWarehouse(new UpdateWarehouseRequest(null, null)).IsValid);
        Assert.True(InventoryRequestValidator.ValidateUpdateWarehouse(new UpdateWarehouseRequest(null, false)).IsValid);
        Assert.True(InventoryRequestValidator.ValidateUpdateWarehouse(new UpdateWarehouseRequest("Ad", null)).IsValid);
    }

    [Fact]
    public void List_queries_default_and_bound_the_paging()
    {
        var defaults = InventoryRequestValidator.ValidateMovementQuery(new MovementListRequest(null, null, null, null)).Value!;
        Assert.Equal((1, 20), (defaults.Page, defaults.PageSize));

        Assert.False(InventoryRequestValidator.ValidateMovementQuery(new MovementListRequest(null, null, "0", null)).IsValid);
        Assert.False(InventoryRequestValidator.ValidateMovementQuery(new MovementListRequest(null, null, null, "101")).IsValid);
        Assert.True(InventoryRequestValidator.ValidateMovementQuery(new MovementListRequest(null, null, "10000", "100")).IsValid);
        Assert.False(InventoryRequestValidator.ValidateMovementQuery(new MovementListRequest("xyz", null, null, null)).IsValid);
    }
}

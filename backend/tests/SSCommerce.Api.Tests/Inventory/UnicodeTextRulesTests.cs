using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Application.Addresses;
using SSCommerce.Application.Common;
using SSCommerce.Application.Inventory;
using SSCommerce.Domain.Customers;

namespace SSCommerce.Api.Tests.Inventory;

/// <summary>
/// Unicode "görünmez/yönlendirme" karakterleri: Cf (biçim: U+202E sağdan-sola geçersiz kılma, U+200B sıfır genişlikli boşluk, U+FEFF...),
/// Zl (U+2028) ve Zp (U+2029) tüm serbest metin alanlarında reddedilir (denetim kaydında ve arayüzde metni gizlemek/yönünü değiştirmek için kullanılır).
/// Sıradan Türkçe metin geçerli kalır. Kural kenarda da denetlenir: Trim U+2028/U+2029'u boşluk sayıp atardı.
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class UnicodeTextRulesTests(InventoryApiFixture fixture)
{
    // Kod noktaları sayıyla verilir (kaynakta görünmez karakter bırakmamak için). "#" yer tutucudur: x#y ortada, #x başta, x# sonda.
    private static readonly (int CodePoint, string Name)[] Forbidden =
    [
        (0x202E, "RLO sağdan-sola geçersiz kılma"), (0x202D, "LRO"), (0x2066, "LRI izolasyon"), (0x200B, "sıfır genişlikli boşluk"),
        (0x200D, "ZWJ"), (0x200E, "LRM"), (0x061C, "Arapça harf işareti"), (0x00AD, "yumuşak tire"), (0xFEFF, "BOM/ZWNBSP"),
        (0x2028, "satır ayırıcı Zl"), (0x2029, "paragraf ayırıcı Zp"),
    ];

    public static TheoryData<int, string> ForbiddenSamples
    {
        get
        {
            var data = new TheoryData<int, string>();
            foreach (var (codePoint, _) in Forbidden)
            {
                data.Add(codePoint, "x#y");
                data.Add(codePoint, "#x");   // kenarda: Trim U+2028/U+2029'u atardı
                data.Add(codePoint, "x#");
            }

            return data;
        }
    }

    private static string Text(int codePoint, string pattern) => pattern.Replace("#", char.ConvertFromUtf32(codePoint), StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(ForbiddenSamples))]
    public void The_shared_rule_rejects_format_and_separator_characters_including_at_the_edges(int codePoint, string pattern)
    {
        var text = Text(codePoint, pattern);
        Assert.True(TextRules.ContainsUnsafeCharacter(text));
        Assert.True(AddressRules.ContainsUnsafeCharacter(text)); // Domain kuralı Application ile aynı
    }

    [Fact]
    public void The_Domain_and_Application_rules_agree_on_every_UTF16_code_unit_and_ordinary_Turkish_text_is_valid()
    {
        for (var code = 0; code <= char.MaxValue; code++)
        {
            var text = "a" + (char)code + "b";
            Assert.True(
                TextRules.ContainsUnsafeCharacter(text) == AddressRules.ContainsUnsafeCharacter(text),
                $"U+{code:X4} için iki kural ayrışıyor");
        }

        foreach (var ok in new[] { "Çanakkale şubesi İade ığüşöç ĞÜŞÖÇİ", "Moda Cad. No: 12/3 - Daire 4", "O'Hara & Sons <b>", "a" + char.ConvertFromUtf32(0x0307) + " combining", "NBSP" + char.ConvertFromUtf32(0xA0) + "arası", "emoji 😀 ok" })
        {
            Assert.False(TextRules.ContainsUnsafeCharacter(ok), ok);
            Assert.False(AddressRules.ContainsUnsafeCharacter(ok), ok);
        }
    }

    [Theory]
    [MemberData(nameof(ForbiddenSamples))]
    public void The_stock_and_warehouse_validators_reject_them(int codePoint, string pattern)
    {
        var text = Text(codePoint, pattern);
        var key = "anahtar-0123456789";
        var actor = Guid.NewGuid();
        var receive = new ReceiveStockRequest(Guid.NewGuid(), Guid.NewGuid(), 1, text, null);
        var reference = new ReceiveStockRequest(Guid.NewGuid(), Guid.NewGuid(), 1, "ok", text);

        Assert.Contains("reason", InventoryRequestValidator.ValidateReceive(receive, key, actor).Errors.Keys);
        Assert.Contains("reference", InventoryRequestValidator.ValidateReceive(reference, key, actor).Errors.Keys);
        Assert.Contains("name", InventoryRequestValidator.ValidateCreateWarehouse(new CreateWarehouseRequest("AB-1", text)).Errors.Keys);
        Assert.Contains("name", InventoryRequestValidator.ValidateUpdateWarehouse(new UpdateWarehouseRequest(text, null)).Errors.Keys);
    }

    [Theory]
    [MemberData(nameof(ForbiddenSamples))]
    public void The_address_validator_and_the_Domain_entity_reject_them_in_every_text_field(int codePoint, string pattern)
    {
        var text = Text(codePoint, pattern);
        foreach (var field in new[] { "label", "recipientName", "district", "streetAddress" })
        {
            var request = new AddressRequest(
                field == "label" ? text : "Ev",
                field == "recipientName" ? text : "Ayşe",
                "5321234567",
                "İzmir",
                field == "district" ? text : "Konak",
                field == "streetAddress" ? text : "Sokak 1",
                null);

            Assert.Contains(field, AddressRequestValidator.Validate(request).Errors.Keys);
            Assert.Throws<AddressRuleException>(() => new CustomerAddress(
                Guid.NewGuid(), request.Label!, request.RecipientName!, "5321234567", "İzmir", request.District!, request.StreetAddress!, null));
        }
    }

    [Theory]
    [MemberData(nameof(ForbiddenSamples))]
    public async Task Address_create_gets_400_for_these_characters_and_stores_nothing(int codePoint, string pattern)
    {
        var text = Text(codePoint, pattern);
        var customer = await fixture.CustomerAsync();

        foreach (var field in new[] { "label", "recipientName", "district", "streetAddress" })
        {
            var body = new Dictionary<string, object?>
            {
                ["label"] = "Ev", ["recipientName"] = "Ayşe", ["phone"] = "05321234567", ["province"] = "İzmir",
                ["district"] = "Konak", ["streetAddress"] = "Sokak 1", ["postalCode"] = "35000",
                [field] = text,
            };
            var response = await customer.PostAsync(CartKit.AddressesUrl, body);
            await ApiAssert.ProblemAsync(response, 400, "validation_failed");
            using var json = await response.JsonAsync();
            Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        }

        Assert.Equal(0, await fixture.AddressCountAsync(customer.UserId));
    }

    [Theory]
    [MemberData(nameof(ForbiddenSamples))]
    public async Task Stock_reason_reference_and_warehouse_name_get_400_for_these_characters(int codePoint, string pattern)
    {
        var text = Text(codePoint, pattern);
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        var (variant, _) = await fixture.CreateVariantAsync();

        await ApiAssert.ProblemAsync(
            await admin.PostAsync("/api/v1/admin/inventory/receive", new { warehouseId = warehouse, variantId = variant, quantity = 1, reason = text }, InventoryApiFixture.NewKey()),
            400,
            "validation_failed");
        await ApiAssert.ProblemAsync(
            await admin.PostAsync("/api/v1/admin/inventory/adjust", new { warehouseId = warehouse, variantId = variant, quantityDelta = 1, type = "adjustment", reason = "x", reference = text }, InventoryApiFixture.NewKey()),
            400,
            "validation_failed");
        await ApiAssert.ProblemAsync(
            await admin.PostAsync("/api/v1/admin/warehouses", new { code = $"U{Guid.NewGuid():N}"[..14].ToUpperInvariant(), name = text }),
            400,
            "validation_failed");
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
    }

    [Fact]
    public async Task Ordinary_Turkish_text_with_combining_marks_nbsp_and_emoji_is_still_accepted_end_to_end()
    {
        var customer = await fixture.CustomerAsync();
        var body = new
        {
            label = "Ev İş ığüşöç",
            recipientName = "Çağlar Ünal-Şen",
            phone = "5321234567",
            province = "Çanakkale",
            district = "Biga" + char.ConvertFromUtf32(0xA0) + "Merkez",
            streetAddress = "Gül Sk. No:5/2 😀",
            postalCode = "17000",
        };

        var response = await customer.PostAsync(CartKit.AddressesUrl, body);

        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
    }
}

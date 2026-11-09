using SSCommerce.Domain.Cart;
using SSCommerce.Domain.Customers;
using DomainCart = SSCommerce.Domain.Cart.Cart;

namespace SSCommerce.Api.Tests.Domain;

/// <summary>Saf unit testler (Faz 6 sepet ve adres domain'i): veritabanı ve ağ yok.</summary>
public class CartAndAddressDomainTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static CustomerAddress Address(
        string label = "Ev",
        string recipient = "Ayşe Yılmaz",
        string phone = "5321234567",
        string province = "İstanbul",
        string district = "Kadıköy",
        string street = "Örnek Mah. Test Sok. No:1",
        string? postal = "34000",
        bool isDefault = false) =>
        new(UserId, label, recipient, phone, province, district, street, postal, isDefault);

    // ---- Cart ----

    [Fact]
    public void Cart_rule_constants_match_the_contract()
    {
        Assert.Equal(20, CartRules.MaxLineQuantity);
        Assert.Equal(50, CartRules.MaxDistinctLines);
        Assert.Equal(10, AddressRules.MaxAddressesPerUser);
    }

    [Fact]
    public void Cart_belongs_to_a_user_gets_a_uuid_v7_and_carries_no_price_or_stock()
    {
        var cart = new DomainCart(UserId);

        Assert.Equal(UserId, cart.UserId);
        Assert.Equal(7, cart.Id.Version);
        Assert.Throws<CartRuleException>(() => new DomainCart(Guid.Empty));

        // Sepette fiyat/stok alanı yoktur (D-049): public özellikler kimlik, kullanıcı ve zaman damgalarıyla sınırlı.
        var properties = typeof(DomainCart).GetProperties().Select(p => p.Name).Order().ToArray();
        Assert.Equal(["CreatedAt", "Id", "UpdatedAt", "UserId"], properties);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public void Cart_item_accepts_boundary_quantities(int quantity)
    {
        var item = new CartItem(Guid.NewGuid(), Guid.NewGuid(), quantity, 349.90m);

        Assert.Equal(quantity, item.Quantity);
        Assert.Equal(349.90m, item.UnitPriceAtAdd);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public void Cart_item_rejects_quantities_outside_one_to_twenty(int quantity)
    {
        Assert.Throws<CartRuleException>(() => new CartItem(Guid.NewGuid(), Guid.NewGuid(), quantity, 10m));
    }

    [Fact]
    public void Changing_quantity_refreshes_the_price_hint_and_a_rejected_change_changes_nothing()
    {
        var item = new CartItem(Guid.NewGuid(), Guid.NewGuid(), 2, 100.00m);

        item.ChangeQuantity(4, 120.50m);
        Assert.Equal((4, 120.50m), (item.Quantity, item.UnitPriceAtAdd));

        Assert.Throws<CartRuleException>(() => item.ChangeQuantity(21, 999m));
        Assert.Throws<CartRuleException>(() => item.ChangeQuantity(0, 999m));
        Assert.Equal((4, 120.50m), (item.Quantity, item.UnitPriceAtAdd));
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("1.234")]    // kuruştan küçük kesir
    public void Cart_item_rejects_invalid_price_hints_with_a_cart_exception(string price)
    {
        var value = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Throws<CartRuleException>(() => new CartItem(Guid.NewGuid(), Guid.NewGuid(), 1, value));
    }

    [Fact]
    public void Cart_item_requires_ids()
    {
        Assert.Throws<CartRuleException>(() => new CartItem(Guid.Empty, Guid.NewGuid(), 1, 1m));
        Assert.Throws<CartRuleException>(() => new CartItem(Guid.NewGuid(), Guid.Empty, 1, 1m));
    }

    // ---- Provinces ----

    [Fact]
    public void Province_list_has_81_distinct_entries_including_turkish_letters()
    {
        Assert.Equal(81, AddressRules.Provinces.Count);
        Assert.Equal(81, AddressRules.Provinces.Distinct(StringComparer.Ordinal).Count());
        string[] mustContain = ["İstanbul", "İzmir", "Ağrı", "Şanlıurfa", "Şırnak", "Iğdır", "Çanakkale", "Hakkâri", "Kahramanmaraş", "Düzce", "Ankara"];
        foreach (var province in mustContain)
        {
            Assert.Contains(province, AddressRules.Provinces);
        }
    }

    [Theory]
    [InlineData("İstanbul")]
    [InlineData(" Ankara ")]
    public void Known_provinces_are_accepted_and_trimmed(string province)
    {
        Assert.Equal(province.Trim(), AddressRules.RequireProvince(province));
    }

    [Theory]
    [InlineData("istanbul")]    // yazım farkı reddedilir (ordinal)
    [InlineData("Istanbul")]    // noktasız I
    [InlineData("Atlantis")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_or_misspelled_provinces_are_rejected(string? province)
    {
        Assert.Throws<AddressRuleException>(() => AddressRules.RequireProvince(province));
    }

    // ---- Phone ----

    [Theory]
    [InlineData("5321234567", "5321234567")]
    [InlineData("05321234567", "5321234567")]
    [InlineData("+905321234567", "5321234567")]
    [InlineData("0 532 123 45 67", "5321234567")]
    [InlineData(" +90 532 123 45 67 ", "5321234567")]
    [InlineData("0532 123 4567", "5321234567")]
    public void Phone_is_normalized_to_ten_digits(string input, string expected)
    {
        Assert.Equal(expected, AddressRules.NormalizePhone(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("4321234567")]          // 5 ile başlamıyor
    [InlineData("532123456")]           // 9 hane
    [InlineData("053212345678")]        // 11 hane
    [InlineData("905321234567")]        // + olmadan ülke kodu
    [InlineData("+90 0532 123 4567")]   // iki önek
    [InlineData("00905321234567")]      // 0090
    [InlineData("5321234567\n")]        // sonda satır sonu
    [InlineData("532-123-4567")]        // tire
    [InlineData("53212345a7")]
    [InlineData("+9053212345678")]
    public void Invalid_phones_are_rejected_without_echoing_the_value(string? phone)
    {
        var exception = Assert.Throws<AddressRuleException>(() => AddressRules.NormalizePhone(phone));

        if (!string.IsNullOrWhiteSpace(phone))
        {
            Assert.DoesNotContain(phone.Trim(), exception.Message, StringComparison.Ordinal);
        }
    }

    // ---- Postal code ----

    [Theory]
    [InlineData("01000")]
    [InlineData("34000")]
    [InlineData("81999")]
    [InlineData("10001")]
    [InlineData("79999")]
    public void Valid_postal_codes_are_accepted(string code)
    {
        Assert.Equal(code, AddressRules.OptionalPostalCode(code));
    }

    [Fact]
    public void Surrounding_whitespace_of_a_postal_code_is_trimmed_before_validation()
    {
        Assert.Equal("34000", AddressRules.OptionalPostalCode(" 34000\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_postal_code_is_null(string? code)
    {
        Assert.Null(AddressRules.OptionalPostalCode(code));
    }

    [Theory]
    [InlineData("00000")]
    [InlineData("00123")]
    [InlineData("82000")]
    [InlineData("99999")]
    [InlineData("3400")]
    [InlineData("340000")]
    [InlineData("34a00")]
    [InlineData("34 000")]
    public void Invalid_postal_codes_are_rejected(string code)
    {
        Assert.Throws<AddressRuleException>(() => AddressRules.OptionalPostalCode(code));
    }

    // ---- CustomerAddress ----

    [Fact]
    public void Address_normalizes_trims_and_starts_non_default_by_default()
    {
        var address = Address(label: " Ev ", recipient: " Ayşe Yılmaz ", phone: "0 532 123 45 67", district: " Kadıköy ", postal: "  ");

        Assert.Equal("Ev", address.Label);
        Assert.Equal("Ayşe Yılmaz", address.RecipientName);
        Assert.Equal("5321234567", address.Phone);
        Assert.Equal("Kadıköy", address.District);
        Assert.Null(address.PostalCode);
        Assert.False(address.IsDefault);
        Assert.Equal(7, address.Id.Version);
        Assert.Equal(UserId, address.UserId);
    }

    [Theory]
    [InlineData("label")]
    [InlineData("recipient")]
    [InlineData("district")]
    [InlineData("street")]
    public void Address_rejects_blank_and_control_characters_in_text_fields(string field)
    {
        foreach (var bad in new[] { "", "   ", "a\u0000b", "a\tb", "a\nb", "a\u0085b" })
        {
            Assert.Throws<AddressRuleException>(() => field switch
            {
                "label" => Address(label: bad),
                "recipient" => Address(recipient: bad),
                "district" => Address(district: bad),
                _ => Address(street: bad),
            });
        }
    }

    [Fact]
    public void Address_enforces_text_length_limits()
    {
        Assert.Equal(50, Address(label: new string('a', 50)).Label.Length);
        Assert.Throws<AddressRuleException>(() => Address(label: new string('a', 51)));
        Assert.Equal(100, Address(recipient: new string('a', 100)).RecipientName.Length);
        Assert.Throws<AddressRuleException>(() => Address(recipient: new string('a', 101)));
        Assert.Equal(64, Address(district: new string('a', 64)).District.Length);
        Assert.Throws<AddressRuleException>(() => Address(district: new string('a', 65)));
        Assert.Equal(300, Address(street: new string('a', 300)).StreetAddress.Length);
        Assert.Throws<AddressRuleException>(() => Address(street: new string('a', 301)));
    }

    [Fact]
    public void Address_rejects_bad_province_phone_postal_code_and_empty_user()
    {
        Assert.Throws<AddressRuleException>(() => Address(province: "Atlantis"));
        Assert.Throws<AddressRuleException>(() => Address(phone: "123"));
        Assert.Throws<AddressRuleException>(() => Address(postal: "99999"));
        Assert.Throws<AddressRuleException>(() => new CustomerAddress(Guid.Empty, "Ev", "A", "5321234567", "Ankara", "Çankaya", "Sok", null));
    }

    [Fact]
    public void Update_replaces_all_fields_but_keeps_the_default_flag_and_is_all_or_nothing()
    {
        var address = Address(isDefault: true);

        address.Update("Is", "Mehmet Kaya", "+905551112233", "Ankara", "Çankaya", "Yeni Sok. No:5", "06000");
        Assert.Equal(("Is", "Mehmet Kaya", "5551112233", "Ankara", "Çankaya", "Yeni Sok. No:5", "06000"),
            (address.Label, address.RecipientName, address.Phone, address.Province, address.District, address.StreetAddress, address.PostalCode));
        Assert.True(address.IsDefault);

        // Son alan (posta kodu) geçersiz: önceki alanlar değişmemeli.
        Assert.Throws<AddressRuleException>(() => address.Update("X", "Y", "5000000000", "İzmir", "Z", "S", "00000"));
        Assert.Equal(("Is", "5551112233", "Ankara", "06000"), (address.Label, address.Phone, address.Province, address.PostalCode));
    }

    [Fact]
    public void SetDefault_toggles_the_flag()
    {
        var address = Address();

        address.SetDefault(true);
        Assert.True(address.IsDefault);
        address.SetDefault(false);
        Assert.False(address.IsDefault);
    }
}

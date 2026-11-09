using SSCommerce.Domain.Catalog;

namespace SSCommerce.Api.Tests.Domain;

/// <summary>Saf unit testler: veritabanı ve ağ yok, milisaniyeler içinde koşar.</summary>
public class CatalogDomainTests
{
    // ---- Money: TL (decimal) <-> kuruş (long) ----

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("0.01", 1L)]
    [InlineData("0.10", 10L)]
    [InlineData("1", 100L)]
    [InlineData("349.00", 34900L)]
    [InlineData("42999.00", 4299900L)]
    [InlineData("1.500", 150L)] // sondaki sıfır ölçek 3 görünse de değer 2 basamağa sığıyor
    [InlineData("90071992547409.91", 9007199254740991L)] // Money.MaxAmount: kuruşu JS safe integer sınırı (2^53-1)
    public void ToMinor_converts_exact_amounts(string lira, long expectedMinor)
    {
        Assert.Equal(expectedMinor, Money.ToMinor(decimal.Parse(lira, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("1.234")] // ölçek 3: kuruştan küçük kesir
    [InlineData("0.001")]
    [InlineData("42999.005")]
    public void ToMinor_rejects_sub_kurus_fractions_instead_of_rounding(string lira)
    {
        Assert.Throws<CatalogRuleException>(() => Money.ToMinor(decimal.Parse(lira, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void ToMinor_rejects_negative_and_oversized_amounts()
    {
        Assert.Throws<CatalogRuleException>(() => Money.ToMinor(-0.01m));
        Assert.Throws<CatalogRuleException>(() => Money.ToMinor(Money.MaxAmount + 0.01m));
        Assert.Throws<CatalogRuleException>(() => Money.ToMinor(decimal.MaxValue));
    }

    [Theory]
    [InlineData(0L, "0.00")]
    [InlineData(1L, "0.01")]
    [InlineData(99L, "0.99")]
    [InlineData(34900L, "349.00")]
    [InlineData(4299900L, "42999.00")]
    public void FromMinor_always_returns_scale_two(long minor, string expected)
    {
        var lira = Money.FromMinor(minor);

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), lira);
        Assert.Equal(2, lira.Scale);
        Assert.Equal(minor, Money.ToMinor(lira)); // gidiş-dönüş kayıpsız
    }

    [Fact]
    public void FromMinor_rejects_negative_and_overflowing_values()
    {
        Assert.Throws<CatalogRuleException>(() => Money.FromMinor(-1));
        Assert.Throws<CatalogRuleException>(() => Money.FromMinor(long.MaxValue)); // ~9.2e16 TL: numeric(18,2) üst sınırının (~1e16 TL) üstünde
    }

    // ---- VariantOptions ----

    [Fact]
    public void Options_signature_is_sorted_and_normalized()
    {
        var options = VariantOptions.Create(new Dictionary<string, string> { [" Renk "] = " SIYAH ", ["hafiza"] = "256GB" });

        Assert.Equal("hafiza=256gb;renk=siyah", options.Signature);
    }

    [Fact]
    public void Options_signature_does_not_depend_on_input_order()
    {
        var a = VariantOptions.Create([new("renk", "siyah"), new("hafiza", "256gb")]);
        var b = VariantOptions.Create([new("hafiza", "256gb"), new("renk", "siyah")]);

        Assert.Equal(a.Signature, b.Signature);
    }

    [Fact]
    public void No_options_means_the_fixed_default_signature()
    {
        Assert.Equal("default", VariantOptions.Create(null).Signature);
        Assert.Equal("default", VariantOptions.Create([]).Signature);
        Assert.Empty(VariantOptions.None.Values);
    }

    [Fact]
    public void Keys_that_collide_after_normalization_are_rejected()
    {
        Assert.Throws<CatalogRuleException>(() => VariantOptions.Create([new("Renk", "siyah"), new("renk", "beyaz")]));
    }

    [Theory]
    [InlineData("", "siyah")]
    [InlineData("renk", "")]
    [InlineData("renk", "açık")] // ASCII dışı: gösterim display_name'in işi
    [InlineData("renk", "a b")]
    [InlineData("renk", "a;b=c")] // imza ayırıcıları değer içinde olamaz (belirsiz imza)
    [InlineData("re=nk", "siyah")]
    [InlineData("renk", "-siyah")]
    public void Invalid_option_tokens_are_rejected(string key, string value)
    {
        Assert.Throws<CatalogRuleException>(() => VariantOptions.Create([new(key, value)]));
    }

    [Fact]
    public void Option_count_and_length_limits_are_enforced()
    {
        var tooMany = Enumerable.Range(0, CatalogRules.MaxOptionCount + 1).Select(i => new KeyValuePair<string, string>($"k{i}", "v"));
        Assert.Throws<CatalogRuleException>(() => VariantOptions.Create(tooMany));

        Assert.Throws<CatalogRuleException>(() => VariantOptions.Create([new(new string('k', CatalogRules.OptionKeyMaxLength + 1), "v")]));
        Assert.Throws<CatalogRuleException>(() => VariantOptions.Create([new("k", new string('v', CatalogRules.OptionValueMaxLength + 1))]));

        var atLimit = Enumerable.Range(0, CatalogRules.MaxOptionCount).Select(i => new KeyValuePair<string, string>($"k{i}", "v"));
        Assert.Equal(CatalogRules.MaxOptionCount, VariantOptions.Create(atLimit).Values.Count);
    }

    // ---- Varlık kuralları ----

    private static Product NewProduct() => new(Guid.NewGuid(), "Ürün", "urun", isDemo: true);

    [Fact]
    public void Entity_ids_are_time_ordered_version_7_guids()
    {
        var first = new Category("A", "a");
        var second = new Category("B", "b");

        Assert.Equal(7, first.Id.Version);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Variant_with_same_options_in_one_product_is_rejected_but_different_options_are_fine()
    {
        var product = NewProduct();
        product.AddVariant("SKU-1", "Siyah", 10m, [new("renk", "siyah")]);

        Assert.Throws<CatalogRuleException>(() => product.AddVariant("SKU-2", "Siyah 2", 10m, [new("renk", "siyah")]));
        product.AddVariant("SKU-3", "Beyaz", 10m, [new("renk", "beyaz")]);
        Assert.Equal(2, product.Variants.Count);
    }

    [Fact]
    public void Two_optionless_variants_collide_on_the_default_signature()
    {
        var product = NewProduct();
        product.AddVariant("SKU-1", "Tek", 10m);

        Assert.Throws<CatalogRuleException>(() => product.AddVariant("SKU-2", "Tek 2", 10m));
    }

    [Theory]
    [InlineData("sku-1")]
    [InlineData("AB")]
    [InlineData("A B C")]
    [InlineData("-ABC")]
    public void Invalid_sku_is_rejected_by_the_domain(string sku)
    {
        Assert.Throws<CatalogRuleException>(() => NewProduct().AddVariant(sku, "Ad", 10m));
    }

    [Fact]
    public void Negative_or_sub_kurus_variant_price_is_rejected_by_the_domain()
    {
        Assert.Throws<CatalogRuleException>(() => NewProduct().AddVariant("SKU-1", "Ad", -1m));
        Assert.Throws<CatalogRuleException>(() => NewProduct().AddVariant("SKU-1", "Ad", 1.005m));
    }

    [Fact]
    public void Variant_currency_is_try()
    {
        Assert.Equal("TRY", NewProduct().AddVariant("SKU-1", "Ad", 10m).Currency);
    }

    [Fact]
    public void Only_one_primary_image_per_product()
    {
        var product = NewProduct();
        product.AddImage("/images/urun/a.webp", "A", 0, isPrimary: true);

        Assert.Throws<CatalogRuleException>(() => product.AddImage("/images/urun/b.webp", "B", 1, isPrimary: true));
        product.AddImage("/images/urun/b.webp", "B", 1);
        Assert.Equal(2, product.Images.Count);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/images/../a.webp")]
    [InlineData("/images/A.webp")]
    [InlineData("https://baska.site/images/a.webp")]
    public void Unsafe_image_paths_are_rejected_by_the_domain(string path)
    {
        Assert.Throws<CatalogRuleException>(() => NewProduct().AddImage(path, "Alt"));
    }

    [Theory]
    [InlineData("Buyuk")]
    [InlineData("a b")]
    [InlineData("-a")]
    [InlineData("a--b")]
    [InlineData("")]
    public void Invalid_slugs_are_rejected_by_the_domain(string slug)
    {
        Assert.Throws<CatalogRuleException>(() => new Category("Ad", slug));
        Assert.Throws<CatalogRuleException>(() => new Product(Guid.NewGuid(), "Ad", slug));
    }

    [Fact]
    public void Category_cannot_become_its_own_parent()
    {
        var category = new Category("Ad", "ad");

        Assert.Throws<CatalogRuleException>(() => category.SetParent(category.Id));
        category.SetParent(Guid.NewGuid());
        Assert.NotNull(category.ParentId);
    }

    [Fact]
    public void Blank_brand_becomes_null_and_text_is_trimmed()
    {
        var product = new Product(Guid.NewGuid(), "  Ad  ", "ad", brand: "   ");

        Assert.Equal("Ad", product.Name);
        Assert.Null(product.Brand);
    }

    // ---- Money: kuruş karşılığı JavaScript'in güvenli tam sayı aralığında olmalı (GOAL §3) ----

    [Fact]
    public void MaxAmount_in_kurus_is_a_JavaScript_safe_integer()
    {
        const long MaxSafeInteger = 9_007_199_254_740_991; // 2^53 - 1

        Assert.Equal(MaxSafeInteger, Money.ToMinor(Money.MaxAmount));
        Assert.True(Money.ToMinor(Money.MaxAmount) <= MaxSafeInteger);
    }

    [Fact]
    public void One_kurus_above_MaxAmount_is_rejected_because_it_would_exceed_the_JavaScript_safe_integer()
    {
        Assert.Throws<CatalogRuleException>(() => Money.EnsureValid(Money.MaxAmount + 0.01m));
        Assert.Throws<CatalogRuleException>(() => Money.FromMinor(9_007_199_254_740_992)); // 2^53
        Assert.Equal(Money.MaxAmount, Money.FromMinor(9_007_199_254_740_991));
    }

    [Fact]
    public void MaxAmount_still_fits_the_numeric_18_2_database_column()
    {
        // DB CHECK/numeric(18,2) değişmedi (yedek sınır): domain sınırı sütundan DAHA dar olmalı, geniş olmamalı.
        Assert.True(Money.MaxAmount <= 9_999_999_999_999_999.99m);
    }
}

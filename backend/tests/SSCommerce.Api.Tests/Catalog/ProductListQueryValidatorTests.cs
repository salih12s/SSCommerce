using SSCommerce.Application.Catalog;
using SSCommerce.Domain.Catalog;
using SSCommerce.Infrastructure.Catalog;

namespace SSCommerce.Api.Tests.Catalog;

/// <summary>
/// Unit testler: DB ve HTTP yok, saf fonksiyonlar (girdi → çıktı). Liste sorgusu doğrulaması, slug biçimi ve LIKE kaçışı.
/// </summary>
public class ProductListQueryValidatorTests
{
    private static ProductListRequest Req(
        string? category = null, string? q = null, string? min = null, string? max = null,
        string? sort = null, string? page = null, string? pageSize = null) =>
        new(category, q, min, max, sort, page, pageSize);

    [Fact]
    public void Empty_request_gets_defaults()
    {
        var result = ProductListQueryValidator.Validate(Req());

        Assert.True(result.IsValid);
        Assert.Equal(new ProductListQuery(null, null, null, null, ProductSort.Newest, 1, 20), result.Query);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "  ")]
    public void Blank_numeric_parameters_are_treated_as_absent(string page, string pageSize)
    {
        var result = ProductListQueryValidator.Validate(Req(page: page, pageSize: pageSize, min: " ", max: ""));

        Assert.True(result.IsValid);
        Assert.Equal(1, result.Query!.Page);
        Assert.Equal(20, result.Query.PageSize);
        Assert.Null(result.Query.MinPriceMinor);
        Assert.Null(result.Query.MaxPriceMinor);
    }

    [Theory]
    [InlineData("newest", ProductSort.Newest)]
    [InlineData("price_asc", ProductSort.PriceAsc)]
    [InlineData("price_desc", ProductSort.PriceDesc)]
    [InlineData("name_asc", ProductSort.NameAsc)]
    [InlineData(" price_asc ", ProductSort.PriceAsc)]
    public void Allowlisted_sort_keys_are_accepted(string sort, ProductSort expected)
    {
        var result = ProductListQueryValidator.Validate(Req(sort: sort));

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Query!.Sort);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("PRICE_ASC")] // allowlist büyük/küçük harf duyarlı
    [InlineData("price_asc; DROP TABLE products")]
    [InlineData("created_at")] // sütun adı değil, yalnız izinli anahtar
    [InlineData("price")]
    public void Unknown_sort_is_rejected(string sort)
    {
        var result = ProductListQueryValidator.Validate(Req(sort: sort));

        Assert.False(result.IsValid);
        Assert.Contains("sort", result.Errors.Keys);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("10001")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("1e2")]
    [InlineData("99999999999999999999")] // long taşması
    public void Invalid_page_is_rejected(string page)
    {
        var result = ProductListQueryValidator.Validate(Req(page: page));

        Assert.False(result.IsValid);
        Assert.Contains("page", result.Errors.Keys);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("10000", 10000)]
    [InlineData(" 7 ", 7)]
    public void Boundary_pages_are_accepted(string page, int expected)
    {
        var result = ProductListQueryValidator.Validate(Req(page: page));

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Query!.Page);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("51")]
    [InlineData("abc")]
    [InlineData("2147483648")]
    public void Invalid_page_size_is_rejected(string pageSize)
    {
        var result = ProductListQueryValidator.Validate(Req(pageSize: pageSize));

        Assert.False(result.IsValid);
        Assert.Contains("pageSize", result.Errors.Keys);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("50", 50)]
    public void Boundary_page_sizes_are_accepted(string pageSize, int expected)
    {
        var result = ProductListQueryValidator.Validate(Req(pageSize: pageSize));

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Query!.PageSize);
    }

    [Theory]
    [InlineData("-1", null)]
    [InlineData("abc", null)]
    [InlineData("12.5", null)]
    [InlineData(null, "-5")]
    [InlineData(null, "9223372036854775807")] // long.MaxValue: numeric(18,2) sınırını aşar
    public void Invalid_price_bounds_are_rejected(string? min, string? max)
    {
        var result = ProductListQueryValidator.Validate(Req(min: min, max: max));

        Assert.False(result.IsValid);
        Assert.True(result.Errors.ContainsKey("minPriceMinor") || result.Errors.ContainsKey("maxPriceMinor"));
    }

    [Fact]
    public void Min_greater_than_max_is_rejected_and_equal_bounds_are_accepted()
    {
        var invalid = ProductListQueryValidator.Validate(Req(min: "5001", max: "5000"));
        Assert.False(invalid.IsValid);
        Assert.Contains("minPriceMinor", invalid.Errors.Keys);

        var equal = ProductListQueryValidator.Validate(Req(min: "5000", max: "5000"));
        Assert.True(equal.IsValid);
        Assert.Equal(5000, equal.Query!.MinPriceMinor);
        Assert.Equal(5000, equal.Query.MaxPriceMinor);
    }

    [Fact]
    public void Max_price_boundary_matches_the_numeric_column()
    {
        var atLimit = ProductListQueryValidator.Validate(Req(max: ProductListQueryValidator.MaxPriceMinor.ToString()));
        Assert.True(atLimit.IsValid);

        var overLimit = ProductListQueryValidator.Validate(Req(max: (ProductListQueryValidator.MaxPriceMinor + 1).ToString()));
        Assert.False(overLimit.IsValid);

        // Kabul edilen en büyük değer Money.FromMinor'dan geçer: sorgu katmanında patlamaz.
        Assert.Equal(Money.MaxAmount, Money.FromMinor(ProductListQueryValidator.MaxPriceMinor));
    }

    [Fact]
    public void Search_text_is_trimmed_and_blank_means_no_search()
    {
        Assert.Equal("kulak", ProductListQueryValidator.Validate(Req(q: "  kulak  ")).Query!.Search);
        Assert.Null(ProductListQueryValidator.Validate(Req(q: "   ")).Query!.Search);
        Assert.Null(ProductListQueryValidator.Validate(Req(q: "")).Query!.Search);
    }

    [Fact]
    public void Search_text_length_limit_is_100_after_trimming()
    {
        var atLimit = ProductListQueryValidator.Validate(Req(q: new string('a', 100)));
        Assert.True(atLimit.IsValid);

        var paddedButShort = ProductListQueryValidator.Validate(Req(q: "  " + new string('a', 100) + "  "));
        Assert.True(paddedButShort.IsValid);

        var tooLong = ProductListQueryValidator.Validate(Req(q: new string('a', 101)));
        Assert.False(tooLong.IsValid);
        Assert.Contains("q", tooLong.Errors.Keys);
    }

    [Theory]
    [InlineData("ab\0cd")] // NUL, PostgreSQL metin parametresinde hata verir
    [InlineData("ab\ncd")]
    [InlineData("ab\tcd")]
    public void Search_text_with_control_characters_is_rejected(string q)
    {
        var result = ProductListQueryValidator.Validate(Req(q: q));

        Assert.False(result.IsValid);
        Assert.Contains("q", result.Errors.Keys);
    }

    [Theory]
    [InlineData("' OR 1=1 --")]
    [InlineData("'; DROP TABLE products; --")]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("\\")]
    public void Hostile_looking_text_is_valid_input_because_it_is_parameterised_not_rejected(string q)
    {
        var result = ProductListQueryValidator.Validate(Req(q: q));

        Assert.True(result.IsValid);
        Assert.Equal(q, result.Query!.Search);
    }

    [Fact]
    public void All_errors_are_reported_together()
    {
        var result = ProductListQueryValidator.Validate(Req(page: "0", pageSize: "99", sort: "x", min: "-1", q: new string('a', 101)));

        Assert.False(result.IsValid);
        Assert.Equal(["minPriceMinor", "page", "pageSize", "q", "sort"], result.Errors.Keys.Order());
    }

    [Fact]
    public void Turkish_culture_does_not_change_number_parsing()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            var result = ProductListQueryValidator.Validate(Req(page: "12", min: "100"));
            Assert.True(result.IsValid);
            Assert.Equal(12, result.Query!.Page);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Category_slug_is_kept_exactly_and_blank_means_no_filter()
    {
        Assert.Equal("ses-sistemleri", ProductListQueryValidator.Validate(Req(category: "ses-sistemleri")).Query!.CategorySlug);
        // Kırpılmaz: boşluklu değer biçim denetiminde (endpoint) 404 olur; tek adres kuralı.
        Assert.Equal(" ses-sistemleri ", ProductListQueryValidator.Validate(Req(category: " ses-sistemleri ")).Query!.CategorySlug);
        Assert.Null(ProductListQueryValidator.Validate(Req(category: " ")).Query!.CategorySlug);
        Assert.Null(ProductListQueryValidator.Validate(Req(category: "")).Query!.CategorySlug);
    }
}

public class CatalogSlugTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("ton-kulakustu-kulaklik")]
    [InlineData("urun-2")]
    [InlineData("123")]
    public void Well_formed_slugs(string slug) => Assert.True(CatalogSlug.IsWellFormed(slug, 160));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Büyük-Harf")]
    [InlineData("kulakustu kulaklik")]
    [InlineData("-baslar")]
    [InlineData("biter-")]
    [InlineData("cift--tire")]
    [InlineData("alt_cizgi")]
    [InlineData("../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("ürün")]
    [InlineData("abc\n")] // .NET regex'te $ sondaki \n'i kabul ederdi
    [InlineData("a\0b")]
    public void Malformed_slugs(string? slug) => Assert.False(CatalogSlug.IsWellFormed(slug, 160));

    [Fact]
    public void Length_limit_is_inclusive()
    {
        Assert.True(CatalogSlug.IsWellFormed(new string('a', 160), 160));
        Assert.False(CatalogSlug.IsWellFormed(new string('a', 161), 160));
    }
}

public class LikePatternTests
{
    [Theory]
    [InlineData("kulak", "kulak")]
    [InlineData("100%", "100\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("%_\\", "\\%\\_\\\\")]
    [InlineData("' OR 1=1 --", "' OR 1=1 --")] // tırnak LIKE joker değil; SQL'e zaten parametreyle gider
    public void Escape_prefixes_wildcards_and_backslash(string input, string expected) =>
        Assert.Equal(expected, LikePattern.Escape(input));

    [Fact]
    public void Contains_wraps_the_escaped_text()
    {
        Assert.Equal("%a\\%b%", LikePattern.Contains("a%b"));
    }
}

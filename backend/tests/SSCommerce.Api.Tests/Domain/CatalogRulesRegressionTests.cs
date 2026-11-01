using SSCommerce.Domain.Catalog;

namespace SSCommerce.Api.Tests.Domain;

/// <summary>
/// Regression: .NET regex'inde "$" sondaki "\n"i de kabul ediyordu ("abc\n" geçerli slug sayılıyordu). Artık "\z" kullanılıyor.
/// Veritabanı CHECK'i (PostgreSQL) bunu zaten reddediyordu; domain kuralı da aynı sıkılıkta olmalı.
/// </summary>
public class CatalogRulesRegressionTests
{
    [Theory]
    [InlineData("abc\n")]
    [InlineData("abc\r\n")]
    [InlineData("a-b\n")]
    public void Slug_with_trailing_newline_is_rejected(string slug) =>
        Assert.Throws<CatalogRuleException>(() => CatalogRules.RequireSlug(slug, 120, "Slug"));

    [Fact]
    public void Slug_without_trailing_newline_is_still_accepted() =>
        Assert.Equal("abc-1", CatalogRules.RequireSlug("abc-1", 120, "Slug"));

    [Fact]
    public void Sku_with_trailing_newline_is_rejected() =>
        Assert.Throws<CatalogRuleException>(() => CatalogRules.RequireSku("ABC-1\n"));

    [Fact]
    public void Sku_without_trailing_newline_is_still_accepted() =>
        Assert.Equal("ABC-1", CatalogRules.RequireSku("ABC-1"));

    [Fact]
    public void Image_path_with_trailing_newline_is_rejected() =>
        Assert.Throws<CatalogRuleException>(() => CatalogRules.RequireImagePath("/images/urun/a.webp\n", 300, "Yol"));

    [Fact]
    public void Image_path_without_trailing_newline_is_still_accepted() =>
        Assert.Equal("/images/urun/a.webp", CatalogRules.RequireImagePath("/images/urun/a.webp", 300, "Yol"));
}

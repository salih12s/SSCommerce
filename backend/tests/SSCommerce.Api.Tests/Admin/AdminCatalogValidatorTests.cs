using System.Text.Json;
using SSCommerce.Application.Administration;
using SSCommerce.Application.Catalog;
using SSCommerce.Domain.Catalog;

namespace SSCommerce.Api.Tests.Admin;

/// <summary>
/// UNIT testler: admin katalog gövdelerinin saf doğrulaması (JSON metni girdi, komut/hata çıktı). Neden unit: tip denetimi, sınır değerleri ve "alan yok" ile
/// "alan null" ayrımı HTTP ya da veritabanı gerektirmeyen saf mantıktır; yüzlerce varyasyon milisaniyede çalışır. Aynı kurallar HTTP üzerinden de bir kez sınanır
/// (AdminCatalogApiTests), burada sınırların kendisi (edge case) tek tek denenir.
/// </summary>
public class AdminCatalogValidatorTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    // ---------- version: eksik / yanlış tip / < 1 asla "kontrolü atla" demek değildir ----------

    [Theory]
    [InlineData("""{"name":"X"}""", "eksik")]
    [InlineData("""{"version":0,"name":"X"}""", "sıfır")]
    [InlineData("""{"version":-3,"name":"X"}""", "negatif")]
    [InlineData("""{"version":"2","name":"X"}""", "metin")]
    [InlineData("""{"version":1.5,"name":"X"}""", "kesirli")]
    [InlineData("""{"version":null,"name":"X"}""", "null")]
    [InlineData("""{"version":2147483648,"name":"X"}""", "int taşması")]
    [InlineData("""{"version":true,"name":"X"}""", "bool")]
    public void Patch_bodies_without_a_valid_version_are_rejected(string body, string why)
    {
        _ = why;
        var category = AdminCatalogValidator.ValidateCategoryChange(Json(body));
        var product = AdminCatalogValidator.ValidateProductChange(Json(body));
        var variant = AdminCatalogValidator.ValidateVariantChange(Json(body));
        var images = AdminCatalogValidator.ValidateImageReplacement(Json(body.Replace("\"name\":\"X\"", "\"images\":[]")));

        Assert.All(new[] { category.Errors, product.Errors, variant.Errors, images.Errors }, errors => Assert.Contains("version", errors.Keys));
        Assert.False(category.IsValid || product.IsValid || variant.IsValid || images.IsValid);
    }

    [Fact]
    public void A_positive_integer_version_is_accepted()
    {
        var result = AdminCatalogValidator.ValidateCategoryChange(Json("""{"version":7}"""));

        Assert.True(result.IsValid);
        Assert.Equal(7, result.Value!.Version);
    }

    // ---------- PATCH anlamı: alan yok = değişmez, null = temizle ----------

    [Fact]
    public void Absent_fields_stay_unset_and_explicit_null_clears_nullable_fields()
    {
        var absent = AdminCatalogValidator.ValidateProductChange(Json("""{"version":1}""")).Value!;
        var cleared = AdminCatalogValidator.ValidateProductChange(Json("""{"version":1,"brand":null,"description":null,"specifications":null}""")).Value!;

        Assert.False(absent.Brand.IsSet);
        Assert.False(absent.Description.IsSet);
        Assert.False(absent.Specifications.IsSet);
        Assert.True(cleared.Brand is { IsSet: true, Value: null });
        Assert.True(cleared.Description is { IsSet: true, Value: null });
        Assert.True(cleared.Specifications is { IsSet: true, Value: null });
    }

    [Fact]
    public void A_category_parent_can_be_cleared_with_null_but_a_name_cannot_be_null()
    {
        var root = AdminCatalogValidator.ValidateCategoryChange(Json("""{"version":1,"parentId":null}"""));
        var nullName = AdminCatalogValidator.ValidateCategoryChange(Json("""{"version":1,"name":null}"""));

        Assert.True(root.Value!.ParentId is { IsSet: true, Value: null });
        Assert.Contains("name", nullName.Errors.Keys);
    }

    [Fact]
    public void Forged_and_unknown_fields_are_ignored_slug_and_sku_never_change_through_patch()
    {
        var category = AdminCatalogValidator.ValidateCategoryChange(Json("""{"version":1,"slug":"yeni","id":"00000000-0000-0000-0000-000000000001","isDemo":true}"""));
        var variant = AdminCatalogValidator.ValidateVariantChange(Json("""{"version":1,"sku":"HACK-1","options":{"a":"b"},"productId":"x"}"""));

        Assert.True(category.IsValid);
        Assert.True(variant.IsValid);
        Assert.False(variant.Value!.PriceMinor.IsSet);
        Assert.False(variant.Value.DisplayName.IsSet);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    [InlineData("""42""")]
    [InlineData("\"metin\"")]
    public void A_body_that_is_not_an_object_is_rejected(string body)
    {
        Assert.False(AdminCatalogValidator.ValidateNewCategory(Json(body)).IsValid);
        Assert.False(AdminCatalogValidator.ValidateProductChange(Json(body)).IsValid);
    }

    // ---------- Para: priceMinor 1 .. 2^53-1 ----------

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(9_007_199_254_740_991, true)]
    [InlineData(9_007_199_254_740_992, false)]
    [InlineData(long.MaxValue, false)]
    public void Price_in_minor_units_must_be_between_1_and_2_pow_53_minus_1(long minor, bool valid)
    {
        var result = AdminCatalogValidator.ValidateVariantChange(Json($$"""{"version":1,"priceMinor":{{minor}} }"""));
        var created = AdminCatalogValidator.ValidateNewVariant(Json($$"""{"sku":"ABC-1","displayName":"Ad","priceMinor":{{minor}} }"""));

        Assert.Equal(valid, result.IsValid);
        Assert.Equal(valid, created.IsValid);
        Assert.Equal(valid, AdminCatalogValidator.TryValidatePriceMinor(minor, out _));
        if (!valid)
        {
            Assert.Contains("priceMinor", result.Errors.Keys);
        }
    }

    [Theory]
    [InlineData("""{"version":1,"priceMinor":"100"}""")]
    [InlineData("""{"version":1,"priceMinor":12.5}""")]
    [InlineData("""{"version":1,"priceMinor":null}""")]
    [InlineData("""{"version":1,"priceMinor":1e400}""")]
    [InlineData("""{"version":1,"priceMinor":true}""")]
    public void A_price_that_is_not_a_whole_number_of_kurus_is_rejected(string body)
    {
        var result = AdminCatalogValidator.ValidateVariantChange(Json(body));

        Assert.False(result.IsValid);
        Assert.Contains("priceMinor", result.Errors.Keys);
    }

    [Fact]
    public void The_maximum_price_converts_to_a_tl_amount_the_database_column_accepts()
    {
        var tl = Money.FromAdminPriceMinor(Money.MaxMinor);

        Assert.Equal(Money.MaxAmount, tl);
        Assert.Equal(Money.MaxMinor, Money.ToMinor(tl));
    }

    // ---------- Metin: kontrol karakterleri ve sınırlar ----------

    [Theory]
    [InlineData("""{"name":"Ad\u0000Soyad","slug":"ok"}""")]
    [InlineData("""{"name":"Ad\nSatir","slug":"ok"}""")]
    [InlineData("""{"name":"Ad‮ters","slug":"ok"}""")]
    [InlineData("""{"name":"Ad​gizli","slug":"ok"}""")]
    [InlineData("""{"name":"   ","slug":"ok"}""")]
    [InlineData("""{"name":"","slug":"ok"}""")]
    [InlineData("""{"slug":"ok"}""")]
    [InlineData("""{"name":"Ad","slug":"Buyuk-Harf"}""")]
    [InlineData("""{"name":"Ad","slug":" ok"}""")]
    [InlineData("""{"name":"Ad","slug":"ok-"}""")]
    [InlineData("""{"name":"Ad","slug":"türkçe"}""")]
    [InlineData("""{"name":"Ad"}""")]
    public void Category_names_and_slugs_with_control_characters_blank_or_malformed_values_are_rejected(string body)
    {
        Assert.False(AdminCatalogValidator.ValidateNewCategory(Json(body)).IsValid);
    }

    [Fact]
    public void A_category_name_over_80_characters_is_rejected_and_exactly_80_is_accepted()
    {
        var ok = AdminCatalogValidator.ValidateNewCategory(Json($$"""{"name":"{{new string('a', 80)}}","slug":"ok"}"""));
        var tooLong = AdminCatalogValidator.ValidateNewCategory(Json($$"""{"name":"{{new string('a', 81)}}","slug":"ok"}"""));

        Assert.True(ok.IsValid);
        Assert.Contains("name", tooLong.Errors.Keys);
    }

    [Fact]
    public void A_new_product_defaults_to_inactive_and_has_no_way_to_set_the_demo_flag()
    {
        var categoryId = Guid.NewGuid();
        var result = AdminCatalogValidator.ValidateNewProduct(Json($$"""{"categoryId":"{{categoryId}}","name":"Ürün","slug":"urun","isDemo":true}"""));

        Assert.True(result.IsValid);
        Assert.False(result.Value!.IsActive);
        Assert.Equal(categoryId, result.Value.CategoryId);
    }

    [Fact]
    public void A_product_needs_a_category_id_in_uuid_form()
    {
        Assert.Contains("categoryId", AdminCatalogValidator.ValidateNewProduct(Json("""{"name":"Ürün","slug":"urun"}""")).Errors.Keys);
        Assert.Contains("categoryId", AdminCatalogValidator.ValidateNewProduct(Json("""{"categoryId":"abc","name":"Ürün","slug":"urun"}""")).Errors.Keys);
        Assert.Contains("categoryId", AdminCatalogValidator.ValidateNewProduct(Json("""{"categoryId":12,"name":"Ürün","slug":"urun"}""")).Errors.Keys);
        Assert.Contains("categoryId", AdminCatalogValidator.ValidateNewProduct(Json("""{"categoryId":null,"name":"Ürün","slug":"urun"}""")).Errors.Keys);
    }

    [Fact]
    public void Specifications_must_be_string_pairs_and_fit_the_size_budget()
    {
        var categoryId = Guid.NewGuid();
        string Body(string specs) => $$"""{"categoryId":"{{categoryId}}","name":"Ürün","slug":"urun","specifications":{{specs}}}""";

        Assert.True(AdminCatalogValidator.ValidateNewProduct(Json(Body("""{"Renk":"Siyah"}"""))).IsValid);
        Assert.False(AdminCatalogValidator.ValidateNewProduct(Json(Body("""{"Adet":5}"""))).IsValid);
        Assert.False(AdminCatalogValidator.ValidateNewProduct(Json(Body("""["a"]"""))).IsValid);
        Assert.False(AdminCatalogValidator.ValidateNewProduct(Json(Body("""{"":"bos anahtar"}"""))).IsValid);

        // 50 çift x (60 anahtar + 300 değer) = 18.000 bayt > 14.000: 500 yerine 400.
        var big = string.Join(",", Enumerable.Range(0, 50).Select(i => $"\"{i:D2}{new string('k', 58)}\":\"{new string('v', 300)}\""));
        var result = AdminCatalogValidator.ValidateNewProduct(Json(Body("{" + big + "}")));
        Assert.Contains("specifications", result.Errors.Keys);
    }

    // ---------- Görseller ----------

    private static string Image(string key, bool primary, string alt = "Açıklama") => $$"""{"storageKey":"{{key}}","altText":"{{alt}}","isPrimary":{{primary.ToString().ToLowerInvariant()}} }""";

    [Fact]
    public void Images_need_exactly_one_primary_when_not_empty_and_at_most_eight()
    {
        string Body(params string[] images) => $$"""{"version":1,"images":[{{string.Join(",", images)}}]}""";
        var keys = ApprovedProductImages.All.Select(a => a.StorageKey).ToArray();

        Assert.True(AdminCatalogValidator.ValidateImageReplacement(Json(Body())).IsValid); // boş küme geçerli (hepsini kaldırır)
        Assert.True(AdminCatalogValidator.ValidateImageReplacement(Json(Body(Image(keys[0], true)))).IsValid);
        Assert.False(AdminCatalogValidator.ValidateImageReplacement(Json(Body(Image(keys[0], false)))).IsValid); // ana görsel yok
        Assert.False(AdminCatalogValidator.ValidateImageReplacement(Json(Body(Image(keys[0], true), Image(keys[1], true)))).IsValid); // iki ana
        Assert.False(AdminCatalogValidator.ValidateImageReplacement(Json(Body(Image(keys[0], true), Image(keys[0], false)))).IsValid); // aynı görsel iki kez

        var eight = keys.Take(8).Select((k, i) => Image(k, i == 0)).ToArray();
        var nine = keys.Take(9).Select((k, i) => Image(k, i == 0)).ToArray();
        Assert.True(AdminCatalogValidator.ValidateImageReplacement(Json(Body(eight))).IsValid);
        Assert.False(AdminCatalogValidator.ValidateImageReplacement(Json(Body(nine))).IsValid);
    }

    [Theory]
    [InlineData("""{"version":1,"images":"x"}""")]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"version":1,"images":[1]}""")]
    [InlineData("""{"version":1,"images":[{"altText":"a","isPrimary":true}]}""")]
    [InlineData("""{"version":1,"images":[{"storageKey":"https://evil.example/x.webp","altText":"a","isPrimary":true}]}""")]
    [InlineData("""{"version":1,"images":[{"storageKey":"/images/urun/../../etc/passwd.webp","altText":"a","isPrimary":true}]}""")]
    [InlineData("""{"version":1,"images":[{"storageKey":"/images/urun/klavye.webp","isPrimary":true}]}""")]
    [InlineData("""{"version":1,"images":[{"storageKey":"/images/urun/klavye.webp","altText":"","isPrimary":true}]}""")]
    public void Malformed_image_items_and_unsafe_paths_are_rejected_before_any_database_work(string body)
    {
        Assert.False(AdminCatalogValidator.ValidateImageReplacement(Json(body)).IsValid);
    }

    // ---------- Liste sorguları ----------

    [Theory]
    [InlineData(null, null, null, null, true)]
    [InlineData("kulak", "00000000-0000-0000-0000-000000000009", "active", "2", true)]
    [InlineData(null, null, "bogus", null, false)]
    [InlineData(null, "abc", null, null, false)]
    [InlineData(null, null, null, "0", false)]
    [InlineData(null, null, null, "x", false)]
    public void Product_list_query_parameters_are_validated(string? q, string? categoryId, string? status, string? page, bool valid)
    {
        Assert.Equal(valid, AdminCatalogValidator.ValidateProductList(q, categoryId, status, page, null).IsValid);
    }

    [Fact]
    public void Page_size_is_capped_at_fifty()
    {
        Assert.True(AdminCatalogValidator.ValidateProductList(null, null, null, null, "50").IsValid);
        Assert.False(AdminCatalogValidator.ValidateProductList(null, null, null, null, "51").IsValid);
        Assert.False(AdminCatalogValidator.ValidateVariantSearch(null, null, "51").IsValid);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("1", "1", true)]
    [InlineData("1000", "720", true)]
    [InlineData("0", null, false)]
    [InlineData("1001", null, false)]
    [InlineData(null, "0", false)]
    [InlineData(null, "721", false)]
    [InlineData("abc", null, false)]
    public void Dashboard_threshold_is_1_to_1000_and_aged_hours_1_to_720(string? threshold, string? hours, bool valid)
    {
        Assert.Equal(valid, AdminReportValidator.ValidateDashboard(threshold, hours).IsValid);
    }

    [Theory]
    [InlineData("1000", true)]
    [InlineData("1001", false)]
    [InlineData("0", false)]
    public void Report_pages_are_between_1_and_1000(string page, bool valid)
    {
        Assert.Equal(valid, AdminReportValidator.ValidateLowStock(null, page, null).IsValid);
        Assert.Equal(valid, AdminReportValidator.ValidateAudit(null, null, page, null).IsValid);
    }

    [Theory]
    [InlineData("category", true)]
    [InlineData("warehouse", true)]
    [InlineData("Category", false)]
    [InlineData("payment", false)]
    public void Audit_target_type_must_be_one_of_the_known_kinds(string targetType, bool valid)
    {
        Assert.Equal(valid, AdminReportValidator.ValidateAudit(targetType, null, null, null).IsValid);
    }

    // ---------- Rol beyaz listesi ----------

    [Theory]
    [InlineData("Admin", true)]
    [InlineData("Warehouse", true)]
    [InlineData("ADMIN", false)]
    [InlineData("admin", false)]
    [InlineData(" Admin", false)]
    [InlineData("Admin ", false)]
    [InlineData("Customer", false)]
    [InlineData("%41dmin", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Admin\0", false)]
    public void Only_the_exact_names_Admin_and_Warehouse_pass_the_role_whitelist(string? role, bool valid)
    {
        Assert.Equal(valid, StaffRoleRules.TryParse(role, out var parsed));
        Assert.Equal(valid ? role : string.Empty, parsed);
    }
}

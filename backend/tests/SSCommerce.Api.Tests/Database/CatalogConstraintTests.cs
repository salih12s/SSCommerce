using Npgsql;

namespace SSCommerce.Api.Tests.Database;

/// <summary>
/// Veritabanı kısıtlarının (UNIQUE, CHECK, FK, NOT NULL) kirli veriyi GERÇEKTEN reddettiğini kanıtlar.
/// Bilerek ham SQL kullanılır: domain sınıfları zaten yanlış girdiyi önce reddeder; buradaki soru "uygulama hatası ya da
/// elle yazılmış SQL kirli veri üretebilir mi?" sorusudur (savunma derinliği). SqlState kodları: 23505 unique, 23514 check,
/// 23503 foreign key, 23502 not null. Her test kendi benzersiz verisini kurar; sıra bağımsızdır.
/// </summary>
public class CatalogConstraintTests(MigratedDatabaseFixture fixture) : IClassFixture<MigratedDatabaseFixture>
{
    private PostgresTestDatabase Db => fixture.Database;

    // ---- Küçük veri kurucular (fixture): her çağrı benzersiz slug/SKU üretir ----

    private static string Unique() => Guid.NewGuid().ToString("N")[..12];

    private async Task<Guid> InsertCategoryAsync(string? slug = null, Guid? parentId = null)
    {
        var id = Guid.NewGuid();
        slug ??= $"kat-{Unique()}";
        await Db.ExecuteAsync(
            $"""
            INSERT INTO categories (id, name, slug, parent_id, sort_order, is_active, created_at, updated_at)
            VALUES ('{id}', 'Test kategori', '{slug}', {(parentId is null ? "NULL" : $"'{parentId}'")}, 0, true, now(), now())
            """,
            TestContext.Current.CancellationToken);
        return id;
    }

    private async Task<Guid> InsertProductAsync(Guid? categoryId = null, string? slug = null)
    {
        var id = Guid.NewGuid();
        categoryId ??= await InsertCategoryAsync();
        slug ??= $"urun-{Unique()}";
        await Db.ExecuteAsync(
            $"""
            INSERT INTO products (id, category_id, name, slug, is_active, created_at, updated_at)
            VALUES ('{id}', '{categoryId}', 'Test ürün', '{slug}', true, now(), now())
            """,
            TestContext.Current.CancellationToken);
        return id;
    }

    private static string VariantSql(
        Guid productId, string sku, string signature = "default", string price = "10.00", string currency = "TRY", string options = "{}") =>
        $"""
        INSERT INTO product_variants (id, product_id, sku, display_name, options, option_signature, price, currency, is_active, created_at, updated_at)
        VALUES ('{Guid.NewGuid()}', '{productId}', '{sku}', 'Test varyant', '{options}'::jsonb, '{signature}', {price}, '{currency}', true, now(), now())
        """;

    private static string ImageSql(Guid productId, bool isPrimary, string storageKey = "/images/urun/test.webp") =>
        $"""
        INSERT INTO product_images (id, product_id, storage_key, alt_text, sort_order, is_primary, created_at, updated_at)
        VALUES ('{Guid.NewGuid()}', '{productId}', '{storageKey}', 'Test görsel', 0, {(isPrimary ? "true" : "false")}, now(), now())
        """;

    private async Task AssertViolationAsync(string expectedSqlState, string sql, string? expectedConstraint = null)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => Db.ExecuteAsync(sql, TestContext.Current.CancellationToken));
        Assert.Equal(expectedSqlState, exception.SqlState);
        if (expectedConstraint is not null)
        {
            // Hata gerçekten beklenen kısıttan mı geldi? (Başka bir nedenle patlayıp testin yanlışlıkla geçmesini önler.)
            Assert.Equal(expectedConstraint, exception.ConstraintName);
        }
    }

    // ---- UNIQUE ----

    [Fact]
    public async Task Duplicate_sku_is_rejected_even_across_different_products()
    {
        var sku = $"DUP-{Unique().ToUpperInvariant()}";
        var productA = await InsertProductAsync();
        var productB = await InsertProductAsync();
        await Db.ExecuteAsync(VariantSql(productA, sku), TestContext.Current.CancellationToken);

        await AssertViolationAsync("23505", VariantSql(productB, sku), "ux_product_variants_sku");
    }

    [Fact]
    public async Task Duplicate_option_signature_in_same_product_is_rejected_but_allowed_in_another_product()
    {
        var productA = await InsertProductAsync();
        var productB = await InsertProductAsync();
        await Db.ExecuteAsync(VariantSql(productA, $"SIG-{Unique().ToUpperInvariant()}", "renk=siyah"), TestContext.Current.CancellationToken);

        await AssertViolationAsync(
            "23505",
            VariantSql(productA, $"SIG-{Unique().ToUpperInvariant()}", "renk=siyah"),
            "ux_product_variants_product_id_option_signature");

        // Aynı imza başka üründe serbest: kısıt (product_id, option_signature) çiftine aittir.
        await Db.ExecuteAsync(VariantSql(productB, $"SIG-{Unique().ToUpperInvariant()}", "renk=siyah"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Duplicate_category_and_product_slugs_are_rejected()
    {
        var categorySlug = $"kat-{Unique()}";
        var categoryId = await InsertCategoryAsync(categorySlug);
        await AssertViolationAsync(
            "23505",
            $"INSERT INTO categories (id, name, slug, sort_order, is_active, created_at, updated_at) VALUES ('{Guid.NewGuid()}', 'x', '{categorySlug}', 0, true, now(), now())",
            "ux_categories_slug");

        var productSlug = $"urun-{Unique()}";
        await InsertProductAsync(categoryId, productSlug);
        await AssertViolationAsync(
            "23505",
            $"INSERT INTO products (id, category_id, name, slug, is_active, created_at, updated_at) VALUES ('{Guid.NewGuid()}', '{categoryId}', 'x', '{productSlug}', true, now(), now())",
            "ux_products_slug");
    }

    [Fact]
    public async Task Second_primary_image_is_rejected_but_non_primary_and_other_products_are_fine()
    {
        var product = await InsertProductAsync();
        var otherProduct = await InsertProductAsync();
        await Db.ExecuteAsync(ImageSql(product, isPrimary: true), TestContext.Current.CancellationToken);

        await AssertViolationAsync("23505", ImageSql(product, isPrimary: true), "ux_product_images_product_id_primary");

        // Kısmi indeks yalnızca is_primary=true satırlarını kapsar.
        await Db.ExecuteAsync(ImageSql(product, isPrimary: false), TestContext.Current.CancellationToken);
        await Db.ExecuteAsync(ImageSql(product, isPrimary: false), TestContext.Current.CancellationToken);
        await Db.ExecuteAsync(ImageSql(otherProduct, isPrimary: true), TestContext.Current.CancellationToken);
    }

    // ---- CHECK ----

    [Fact]
    public async Task Negative_price_is_rejected_and_zero_is_allowed()
    {
        var product = await InsertProductAsync();

        await AssertViolationAsync(
            "23514",
            VariantSql(product, $"NEG-{Unique().ToUpperInvariant()}", "a=1", price: "-0.01"),
            "ck_product_variants_price_non_negative");

        await Db.ExecuteAsync(VariantSql(product, $"ZER-{Unique().ToUpperInvariant()}", "a=2", price: "0"), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    [InlineData("try")]
    public async Task Currency_other_than_TRY_is_rejected(string currency)
    {
        var product = await InsertProductAsync();

        await AssertViolationAsync(
            "23514",
            VariantSql(product, $"CUR-{Unique().ToUpperInvariant()}", currency: currency),
            "ck_product_variants_currency_try");
    }

    [Fact]
    public async Task Category_cannot_be_its_own_parent()
    {
        var id = Guid.NewGuid();

        await AssertViolationAsync(
            "23514",
            $"INSERT INTO categories (id, name, slug, parent_id, sort_order, is_active, created_at, updated_at) VALUES ('{id}', 'x', 'kendi-{Unique()}', '{id}', 0, true, now(), now())",
            "ck_categories_parent_not_self");
    }

    [Fact]
    public async Task Updating_a_category_to_point_at_itself_is_rejected()
    {
        var id = await InsertCategoryAsync();

        await AssertViolationAsync("23514", $"UPDATE categories SET parent_id = id WHERE id = '{id}'", "ck_categories_parent_not_self");
    }

    [Theory]
    [InlineData("Buyuk-Harf")]
    [InlineData("bosluk var")]
    [InlineData("-basta-tire")]
    [InlineData("sonda-tire-")]
    [InlineData("cift--tire")]
    [InlineData("türkçe-ı")]
    [InlineData("alt_cizgi")]
    [InlineData("")]
    public async Task Invalid_slug_is_rejected_for_categories_and_products(string slug)
    {
        await AssertViolationAsync(
            "23514",
            $"INSERT INTO categories (id, name, slug, sort_order, is_active, created_at, updated_at) VALUES ('{Guid.NewGuid()}', 'x', '{slug}', 0, true, now(), now())",
            "ck_categories_slug_format");

        var categoryId = await InsertCategoryAsync();
        await AssertViolationAsync(
            "23514",
            $"INSERT INTO products (id, category_id, name, slug, is_active, created_at, updated_at) VALUES ('{Guid.NewGuid()}', '{categoryId}', 'x', '{slug}', true, now(), now())",
            "ck_products_slug_format");
    }

    [Theory]
    [InlineData("abc-123")] // küçük harf
    [InlineData("AB")] // çok kısa
    [InlineData("-ABC")]
    [InlineData("ABC-")]
    [InlineData("AB C")]
    [InlineData("ABC_1")]
    public async Task Invalid_sku_is_rejected(string sku)
    {
        var product = await InsertProductAsync();

        await AssertViolationAsync("23514", VariantSql(product, sku), "ck_product_variants_sku_format");
    }

    [Fact]
    public async Task Sku_longer_than_40_characters_is_rejected()
    {
        var product = await InsertProductAsync();

        // 41 karakter: varchar(40) sütunu zaten reddeder (22001 string_data_right_truncation).
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => Db.ExecuteAsync(VariantSql(product, new string('A', 41)), TestContext.Current.CancellationToken));
        Assert.Equal("22001", exception.SqlState);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"metin\"")]
    [InlineData("42")]
    [InlineData("null")] // JSON null, SQL NULL değil: NOT NULL'ı geçer, jsonb_typeof CHECK'i yakalar
    public async Task Variant_options_must_be_a_json_object(string json)
    {
        var product = await InsertProductAsync();

        await AssertViolationAsync(
            "23514",
            VariantSql(product, $"OPT-{Unique().ToUpperInvariant()}", options: json),
            "ck_product_variants_options_object");
    }

    [Fact]
    public async Task Variant_options_larger_than_2048_bytes_are_rejected()
    {
        var product = await InsertProductAsync();
        var big = $"{{\"renk\":\"{new string('x', 3000)}\"}}";

        await AssertViolationAsync(
            "23514",
            VariantSql(product, $"BIG-{Unique().ToUpperInvariant()}", options: big),
            "ck_product_variants_options_object");
    }

    [Fact]
    public async Task Variant_options_cannot_be_sql_null()
    {
        var product = await InsertProductAsync();
        var sql = VariantSql(product, $"NUL-{Unique().ToUpperInvariant()}").Replace("'{}'::jsonb", "NULL");

        await AssertViolationAsync("23502", sql);
    }

    [Fact]
    public async Task Specifications_may_be_null_or_object_but_not_array_or_oversized()
    {
        var categoryId = await InsertCategoryAsync();

        string Insert(string specifications) =>
            $"INSERT INTO products (id, category_id, name, slug, specifications, is_active, created_at, updated_at) " +
            $"VALUES ('{Guid.NewGuid()}', '{categoryId}', 'x', 'spec-{Unique()}', {specifications}, true, now(), now())";

        await Db.ExecuteAsync(Insert("NULL"), TestContext.Current.CancellationToken);
        await Db.ExecuteAsync(Insert("'{\"Tür\":\"Kulaklık\"}'::jsonb"), TestContext.Current.CancellationToken);

        await AssertViolationAsync("23514", Insert("'[1,2]'::jsonb"), "ck_products_specifications_object");
        await AssertViolationAsync(
            "23514",
            Insert($"'{{\"k\":\"{new string('x', 17000)}\"}}'::jsonb"),
            "ck_products_specifications_object");
    }

    [Fact]
    public async Task Description_longer_than_4000_characters_is_rejected()
    {
        var categoryId = await InsertCategoryAsync();
        string Insert(int length) =>
            $"INSERT INTO products (id, category_id, name, slug, description, is_active, created_at, updated_at) " +
            $"VALUES ('{Guid.NewGuid()}', '{categoryId}', 'x', 'desc-{Unique()}', '{new string('a', length)}', true, now(), now())";

        await Db.ExecuteAsync(Insert(4000), TestContext.Current.CancellationToken);
        await AssertViolationAsync("23514", Insert(4001), "ck_products_description_length");
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/images/../gizli.webp")]
    [InlineData("/images//cift.webp")]
    [InlineData("http://baska.site/images/a.webp")]
    [InlineData("/images/BUYUK.webp")]
    [InlineData("/images/uzantisiz")]
    [InlineData("images/goreli.webp")]
    [InlineData("/images/a b.webp")]
    public async Task Unsafe_image_storage_key_is_rejected(string storageKey)
    {
        var product = await InsertProductAsync();

        await AssertViolationAsync("23514", ImageSql(product, isPrimary: false, storageKey), "ck_product_images_storage_key_format");
    }

    [Fact]
    public async Task Blank_required_text_is_rejected()
    {
        var categoryId = await InsertCategoryAsync();

        await AssertViolationAsync(
            "23514",
            $"INSERT INTO products (id, category_id, name, slug, is_active, created_at, updated_at) VALUES ('{Guid.NewGuid()}', '{categoryId}', '   ', 'bos-{Unique()}', true, now(), now())",
            "ck_products_name_not_blank");
    }

    // ---- FK (Restrict) ve NOT NULL ----

    [Fact]
    public async Task Product_with_variants_cannot_be_deleted()
    {
        var product = await InsertProductAsync();
        await Db.ExecuteAsync(VariantSql(product, $"FKV-{Unique().ToUpperInvariant()}"), TestContext.Current.CancellationToken);

        await AssertViolationAsync("23503", $"DELETE FROM products WHERE id = '{product}'", "fk_product_variants_product");
    }

    [Fact]
    public async Task Product_with_images_cannot_be_deleted()
    {
        var product = await InsertProductAsync();
        await Db.ExecuteAsync(ImageSql(product, isPrimary: false), TestContext.Current.CancellationToken);

        await AssertViolationAsync("23503", $"DELETE FROM products WHERE id = '{product}'", "fk_product_images_product");
    }

    [Fact]
    public async Task Category_with_products_or_children_cannot_be_deleted()
    {
        var parent = await InsertCategoryAsync();
        var child = await InsertCategoryAsync(parentId: parent);
        await InsertProductAsync(child);

        await AssertViolationAsync("23503", $"DELETE FROM categories WHERE id = '{child}'", "fk_products_category");
        await AssertViolationAsync("23503", $"DELETE FROM categories WHERE id = '{parent}'", "fk_categories_parent");
    }

    [Fact]
    public async Task Rows_pointing_at_missing_parents_are_rejected()
    {
        await AssertViolationAsync("23503", VariantSql(Guid.NewGuid(), $"ORP-{Unique().ToUpperInvariant()}"), "fk_product_variants_product");
        await AssertViolationAsync(
            "23503",
            $"INSERT INTO products (id, category_id, name, slug, is_active, created_at, updated_at) VALUES ('{Guid.NewGuid()}', '{Guid.NewGuid()}', 'x', 'yetim-{Unique()}', true, now(), now())",
            "fk_products_category");
    }

    [Fact]
    public async Task Leaf_rows_can_be_deleted_and_is_demo_defaults_to_false()
    {
        var product = await InsertProductAsync();
        var isDemo = await Db.ScalarAsync<bool>($"SELECT is_demo FROM products WHERE id = '{product}'", TestContext.Current.CancellationToken);
        Assert.False(isDemo);

        // Restrict yalnızca bağlı kayıt varken engeller; yaprak satır silinebilir.
        await Db.ExecuteAsync($"DELETE FROM products WHERE id = '{product}'", TestContext.Current.CancellationToken);
    }
}

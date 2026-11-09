using System.Net;
using System.Text.Json;

namespace SSCommerce.Api.Tests.Catalog;

/// <summary>
/// Integration + API testi: gerçek PostgreSQL (izole DB), tam HTTP hattı. Veri: <see cref="CatalogApiFixture"/> (kendi fixture'ımız).
/// Bu sınıf OKUMA doğruluğunu sınar: liste, sıralama, filtre, arama, detay, pasif veri sızıntısı, kuruş doğruluğu.
/// </summary>
[Collection(CatalogApiCollection.Name)]
public class CatalogReadApiTests(CatalogApiFixture fixture)
{
    // ---------- Kategoriler ----------

    [Fact]
    public async Task Categories_returns_only_active_ordered_by_sort_order_name_then_id()
    {
        var (response, json) = await fixture.Development.GetJsonAsync("/api/v1/categories");
        using var _ = json;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var items = json.RootElement.EnumerateArray().ToArray();

        var slugs = items.Select(i => i.GetProperty("slug").GetString()!).ToArray();
        Assert.DoesNotContain("fx-pasif", slugs);
        Assert.DoesNotContain("fx-eski", slugs);

        // sort_order: ikiz(5) x2, elektronik(10) < ev(10) (ad), aksesuar(20) < telefon(20) (ad), kilif(30), yetim(50)
        Assert.Equal(
            ["fx-ikiz", "fx-elektronik", "fx-ev", "fx-aksesuar", "fx-telefon", "fx-telefon-kilif", "fx-yetim"],
            slugs.Select(s => s.StartsWith("fx-ikiz-", StringComparison.Ordinal) ? "fx-ikiz" : s).Distinct().ToArray());

        // Eşit (sort_order, name) çiftinde id sırası belirler.
        var twins = items.Where(i => i.GetProperty("slug").GetString()!.StartsWith("fx-ikiz-", StringComparison.Ordinal))
            .Select(i => i.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(2, twins.Length);
        Assert.True(CatalogJson.CompareLikePostgres(twins[0], twins[1]) < 0, "Eşit sort_order+name için id artan olmalı.");
    }

    [Fact]
    public async Task Categories_shape_has_parentSlug_only_when_parent_is_active_and_no_admin_fields()
    {
        var (_, json) = await fixture.Development.GetJsonAsync("/api/v1/categories");
        using var _json = json;

        var bySlug = json.RootElement.EnumerateArray().ToDictionary(i => i.GetProperty("slug").GetString()!);

        Assert.Equal(JsonValueKind.Null, bySlug["fx-elektronik"].GetProperty("parentSlug").ValueKind);
        Assert.Equal("fx-elektronik", bySlug["fx-telefon"].GetProperty("parentSlug").GetString());
        Assert.Equal("fx-telefon", bySlug["fx-telefon-kilif"].GetProperty("parentSlug").GetString());
        // Üst kategorisi pasif olan kategori: pasif üstün slug'ı sızdırılmaz.
        Assert.Equal(JsonValueKind.Null, bySlug["fx-yetim"].GetProperty("parentSlug").ValueKind);

        var sample = bySlug["fx-telefon"];
        Assert.Equal(
            ["id", "name", "parentSlug", "slug", "sortOrder"],
            sample.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("Telefon", sample.GetProperty("name").GetString());
        Assert.Equal(20, sample.GetProperty("sortOrder").GetInt32());
        CatalogJson.AssertNoForbiddenKeys(json.RootElement);
    }

    // ---------- Liste: happy path + sayfalama ----------

    [Fact]
    public async Task List_happy_path_returns_visible_products_with_full_item_shape_and_pagination_metadata()
    {
        var (response, json) = await fixture.Development.GetJsonAsync("/api/v1/products");
        using var _ = json;
        var root = json.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(20, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(CatalogApiFixture.VisibleProductCount, root.GetProperty("totalCount").GetInt64());
        Assert.Equal(1, root.GetProperty("totalPages").GetInt32());
        Assert.Equal(CatalogApiFixture.VisibleProductCount, root.GetProperty("items").GetArrayLength());
        Assert.Equal(["items", "page", "pageSize", "totalCount", "totalPages"], root.EnumerateObject().Select(p => p.Name).Order().ToArray());

        var telefonA = root.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("slug").GetString() == "fx-telefon-a");
        Assert.Equal(
            ["category", "currency", "id", "inStock", "isDemo", "name", "primaryImage", "slug", "startingPriceMinor"],
            telefonA.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("Telefon A", telefonA.GetProperty("name").GetString());
        Assert.Equal("fx-telefon", telefonA.GetProperty("category").GetProperty("slug").GetString());
        Assert.Equal("Telefon", telefonA.GetProperty("category").GetProperty("name").GetString());
        Assert.Equal("TRY", telefonA.GetProperty("currency").GetString());
        Assert.True(telefonA.GetProperty("isDemo").GetBoolean());
        Assert.Equal(1_000_000, telefonA.GetProperty("startingPriceMinor").GetInt64()); // min(10000.00, 12000.00) TL
        Assert.Equal("/images/test/a-on.webp", telefonA.GetProperty("primaryImage").GetProperty("url").GetString());
        Assert.Equal("Telefon A önden", telefonA.GetProperty("primaryImage").GetProperty("altText").GetString());
        CatalogJson.AssertNoForbiddenKeys(root);
    }

    [Fact]
    public async Task Product_without_primary_image_has_null_primaryImage()
    {
        var (_, json) = await fixture.Development.GetJsonAsync("/api/v1/products?q=Telefon%20B");
        using var _json = json;

        var item = json.RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, item.GetProperty("primaryImage").ValueKind);
    }

    [Fact]
    public async Task Pagination_metadata_is_correct_and_pages_do_not_overlap()
    {
        var (_, page1) = await fixture.Development.GetJsonAsync("/api/v1/products?pageSize=4&page=1");
        var (_, page3) = await fixture.Development.GetJsonAsync("/api/v1/products?pageSize=4&page=3");
        using var _1 = page1;
        using var _3 = page3;

        Assert.Equal(9, page1.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(3, page1.RootElement.GetProperty("totalPages").GetInt32()); // ceil(9/4)
        Assert.Equal(4, page1.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(4, page1.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, page3.RootElement.GetProperty("items").GetArrayLength()); // 9 - 8
        Assert.Equal(3, page3.RootElement.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task Page_beyond_the_last_returns_200_with_empty_items_and_correct_total()
    {
        var (response, json) = await fixture.Development.GetJsonAsync("/api/v1/products?page=10000&pageSize=50");
        using var _ = json;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(9, json.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(1, json.RootElement.GetProperty("totalPages").GetInt32());
        Assert.Equal(10000, json.RootElement.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task Empty_result_has_zero_total_and_zero_pages()
    {
        var (response, json) = await fixture.Development.GetJsonAsync("/api/v1/products?q=hicbirseyeuymayanmetin");
        using var _ = json;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, json.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(0, json.RootElement.GetProperty("totalPages").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("items").GetArrayLength());
    }

    // ---------- Sıralama ----------

    [Fact]
    public async Task Sort_newest_is_the_default_and_orders_by_created_at_desc_with_id_desc_tiebreak()
    {
        var (_, defaultJson) = await fixture.Development.GetJsonAsync("/api/v1/products");
        var (_, explicitJson) = await fixture.Development.GetJsonAsync("/api/v1/products?sort=newest");
        using var _d = defaultJson;
        using var _e = explicitJson;

        Assert.Equal(explicitJson.RootElement.Slugs(), defaultJson.RootElement.Slugs());

        var slugs = defaultJson.RootElement.Slugs();
        Assert.Equal(
            ["fx-alt-cizgi", "fx-pil-yuzde", "fx-torun-urun", "fx-yarim-pasif", "fx-isik-lambasi"],
            slugs.Take(5).ToArray());
        Assert.Equal(["fx-telefon-b", "fx-telefon-a"], slugs.Skip(7).ToArray());

        // Kılıf A/B aynı created_at: id azalan.
        var tied = defaultJson.RootElement.GetProperty("items").EnumerateArray().Skip(5).Take(2).ToArray();
        Assert.Equal(
            ["fx-kilif-a", "fx-kilif-b"],
            tied.Select(i => i.GetProperty("slug").GetString()!).Order().ToArray());
        Assert.True(CatalogJson.CompareLikePostgres(tied[0].GetProperty("id").GetGuid(), tied[1].GetProperty("id").GetGuid()) > 0);
    }

    [Fact]
    public async Task Sort_price_asc_orders_by_starting_price_with_id_tiebreak_and_ignores_inactive_variants()
    {
        var (_, json) = await fixture.Development.GetJsonAsync("/api/v1/products?sort=price_asc");
        using var _ = json;
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(
            ["fx-pil-yuzde", "fx-alt-cizgi", "fx-torun-urun"],
            items.Take(3).Select(i => i.GetProperty("slug").GetString()!).ToArray());
        Assert.Equal(
            ["fx-isik-lambasi", "fx-yarim-pasif", "fx-telefon-a", "fx-telefon-b"],
            items.Skip(5).Select(i => i.GetProperty("slug").GetString()!).ToArray());

        // Pasif 1.00 TL'lik varyant "Yarım Pasif" ürününü başa taşımamalı: başlangıç fiyatı 500.00 TL.
        Assert.Equal(50_000, items.Single(i => i.GetProperty("slug").GetString() == "fx-yarim-pasif").GetProperty("startingPriceMinor").GetInt64());

        // 199.90 TL eşitliği: id artan.
        var tied = items.Skip(3).Take(2).ToArray();
        Assert.All(tied, t => Assert.Equal(19990, t.GetProperty("startingPriceMinor").GetInt64()));
        Assert.True(CatalogJson.CompareLikePostgres(tied[0].GetProperty("id").GetGuid(), tied[1].GetProperty("id").GetGuid()) < 0);
    }

    [Fact]
    public async Task Sort_price_desc_orders_by_starting_price_desc_with_id_asc_tiebreak()
    {
        var (_, json) = await fixture.Development.GetJsonAsync("/api/v1/products?sort=price_desc");
        using var _ = json;
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(
            ["fx-telefon-b", "fx-telefon-a", "fx-yarim-pasif", "fx-isik-lambasi"],
            items.Take(4).Select(i => i.GetProperty("slug").GetString()!).ToArray());
        Assert.Equal(
            ["fx-torun-urun", "fx-alt-cizgi", "fx-pil-yuzde"],
            items.Skip(6).Select(i => i.GetProperty("slug").GetString()!).ToArray());

        var tied = items.Skip(4).Take(2).ToArray();
        Assert.All(tied, t => Assert.Equal(19990, t.GetProperty("startingPriceMinor").GetInt64()));
        Assert.True(CatalogJson.CompareLikePostgres(tied[0].GetProperty("id").GetGuid(), tied[1].GetProperty("id").GetGuid()) < 0);
    }

    [Fact]
    public async Task Sort_name_asc_orders_by_name()
    {
        var (_, json) = await fixture.Development.GetJsonAsync("/api/v1/products?sort=name_asc");
        using var _ = json;

        Assert.Equal(
            ["Alt_Cizgi Kablo", "Işık Lambası", "Kılıf A", "Kılıf B", "Pil 100% Şarj", "Telefon A", "Telefon B", "Torun Kategori Ürünü", "Yarım Pasif Ürün"],
            json.RootElement.Names());
    }

    [Theory]
    [InlineData("newest")]
    [InlineData("price_asc")]
    [InlineData("price_desc")]
    [InlineData("name_asc")]
    public async Task Every_sort_is_deterministic_and_paging_through_it_yields_each_product_exactly_once(string sort)
    {
        var (_, all) = await fixture.Development.GetJsonAsync($"/api/v1/products?sort={sort}&pageSize=50");
        using var _all = all;
        var expected = all.RootElement.Ids();

        var (_, again) = await fixture.Development.GetJsonAsync($"/api/v1/products?sort={sort}&pageSize=50");
        using var _again = again;
        Assert.Equal(expected, again.RootElement.Ids()); // aynı istek => aynı sıra

        var collected = new List<Guid>();
        for (var page = 1; page <= 5; page++)
        {
            var (_, part) = await fixture.Development.GetJsonAsync($"/api/v1/products?sort={sort}&pageSize=2&page={page}");
            using var _part = part;
            collected.AddRange(part.RootElement.Ids());
        }

        Assert.Equal(expected, collected); // sayfa sayfa gezince kayma/tekrar/eksik yok
        Assert.Equal(collected.Count, collected.Distinct().Count());
    }

    // ---------- Kategori filtresi ----------

    [Fact]
    public async Task Category_filter_includes_the_category_and_its_direct_children()
    {
        var (_, json) = await fixture.Development.GetJsonAsync("/api/v1/products?category=fx-telefon&sort=name_asc");
        using var _ = json;

        // fx-telefon: Telefon A, Telefon B, Yarım Pasif + doğrudan alt kategori fx-telefon-kilif: Torun
        Assert.Equal(["fx-telefon-a", "fx-telefon-b", "fx-torun-urun", "fx-yarim-pasif"], json.RootElement.Slugs());
        Assert.Equal(4, json.RootElement.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public async Task Parent_category_includes_direct_children_but_not_grandchildren_and_not_inactive_children()
    {
        var (_, json) = await fixture.Development.GetJsonAsync("/api/v1/products?category=fx-elektronik&sort=name_asc");
        using var _ = json;

        // Çocuklar: aksesuar (Kılıf A/B), telefon (Telefon A/B, Yarım). Torun (fx-telefon-kilif) ve pasif fx-eski HARİÇ.
        Assert.Equal(
            ["fx-kilif-a", "fx-kilif-b", "fx-telefon-a", "fx-telefon-b", "fx-yarim-pasif"],
            json.RootElement.Slugs());
    }

    [Fact]
    public async Task Leaf_category_filter_returns_only_its_own_products()
    {
        var (_, json) = await fixture.Development.GetJsonAsync("/api/v1/products?category=fx-ev&sort=name_asc");
        using var _ = json;

        Assert.Equal(["fx-alt-cizgi", "fx-isik-lambasi", "fx-pil-yuzde"], json.RootElement.Slugs());
    }

    [Theory]
    [InlineData("fx-yok-boyle-kategori")] // bilinmeyen
    [InlineData("fx-pasif")]              // pasif
    [InlineData("fx-eski")]               // pasif alt kategori
    [InlineData("Fx-Ev")]                 // biçim bozuk (büyük harf)
    [InlineData("..%2F..%2Fetc")]         // yol gezinme denemesi
    [InlineData("fx-ev%0A")]              // sonda satır sonu
    [InlineData("fx%00ev")]               // NUL
    public async Task Unknown_inactive_or_malformed_category_returns_404_category_not_found(string category)
    {
        var (response, json) = await fixture.Development.GetJsonAsync($"/api/v1/products?category={category}");
        using var _ = json;

        CatalogJson.AssertProblem(response, json.RootElement, 404, "category_not_found");
    }

    // ---------- Arama ----------

    [Theory]
    [InlineData("telefon", 2)]
    [InlineData("TELEFON", 2)]          // büyük/küçük harf duyarsız
    [InlineData("  telefon  ", 2)]      // kırpılır
    [InlineData("kılıf", 2)]
    [InlineData("pasif", 1)]            // yalnız görünür "Yarım Pasif Ürün"; "Gizli Pasif ..." ürünleri sızmaz
    [InlineData("lumen", 1)]            // marka alanı da aranır
    [InlineData("LUMEN", 1)]
    [InlineData("gizli", 0)]            // gizli ürünlerin hiçbiri bulunmaz
    [InlineData("   ", 9)]              // boş q yok sayılır
    public async Task Search_matches_name_and_brand_case_insensitively(string q, int expectedCount)
    {
        var (response, json) = await fixture.Development.GetJsonAsync($"/api/v1/products?q={Uri.EscapeDataString(q)}");
        using var _ = json;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedCount, json.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(expectedCount, json.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Search_is_case_insensitive_for_Turkish_letters_that_map_one_to_one()
    {
        // ü/Ü, ş/Ş, ı/I... çiftlerinden "Ü" ve "Ş" ILIKE ile küçük harfe katlanır (PostgreSQL lower(), UTF-8 yerel ayar).
        Assert.Equal(1, await CountAsync("TORUN KATEGORİ ÜRÜNÜ")); // İ -> i
        Assert.Equal(1, await CountAsync("torun kategori ürünü"));
        Assert.Equal(1, await CountAsync("ŞARJ"));
    }

    [Fact]
    public async Task Search_Turkish_dotless_I_behaviour_is_documented_and_follows_the_database_locale()
    {
        // BELGELENEN DAVRANIŞ: ILIKE, veritabanının LC_CTYPE'ına göre küçük harfe çevirir; uygulama kendi Türkçe katlamasını YAPMAZ.
        //   en_US/C.UTF-8 gibi Türkçe olmayan yerel ayarlarda: lower('I') = 'i'  ('ı' ASLA 'I' ile eşleşmez).
        //   tr_TR yerel ayarında: lower('I') = 'ı'  (tersi).
        // Fixture adı "Işık Lambası": harfleri I-ş-ı-k (başta BÜYÜK noktasız I, ortada KÜÇÜK noktasız ı).
        // Pratik sonuç (Türkçe olmayan yerel ayar): ASCII klavyeyle yazılan "ISIK" ile "Işık" bulunamaz, "isik" de bulunamaz
        // (ş/s farklı harf). Bu bilinen bir sınırlamadır; çözüm (unaccent/normalize edilmiş arama sütunu) şema değişikliği ister.
        var lcCtype = await fixture.Database.ScalarAsync<string>(
            "SELECT datctype FROM pg_database WHERE datname = current_database()", TestContext.Current.CancellationToken);
        var turkishLocale = lcCtype?.StartsWith("tr", StringComparison.OrdinalIgnoreCase) == true;

        Assert.Equal(1, await CountAsync("Işık"));   // birebir aynı yazım: her yerel ayarda bulunur
        Assert.Equal(1, await CountAsync("IŞık"));   // aynı harfler
        Assert.Equal(turkishLocale ? 1 : 0, await CountAsync("IŞIK"));  // son 'I' küçük 'ı' ile eşleşir mi? yalnız tr_TR'de
        Assert.Equal(turkishLocale ? 1 : 0, await CountAsync("ışık"));  // baştaki 'ı' büyük 'I' ile eşleşir mi? yalnız tr_TR'de
        Assert.Equal(turkishLocale ? 0 : 1, await CountAsync("işık"));  // baştaki noktalı 'i' büyük 'I' ile eşleşir mi? yalnız Türkçe olmayanda
        Assert.Equal(0, await CountAsync("isik"));   // ş ile s farklı harftir (aksan duyarsızlık YOK)
    }

    private async Task<long> CountAsync(string q)
    {
        var (_, json) = await fixture.Development.GetJsonAsync($"/api/v1/products?q={Uri.EscapeDataString(q)}");
        using var _ = json;
        return json.RootElement.GetProperty("totalCount").GetInt64();
    }

    [Fact]
    public async Task Percent_and_underscore_are_matched_literally_not_as_wildcards()
    {
        var (_, percent) = await fixture.Development.GetJsonAsync("/api/v1/products?q=%25");   // q=%
        var (_, underscore) = await fixture.Development.GetJsonAsync("/api/v1/products?q=_");
        var (_, both) = await fixture.Development.GetJsonAsync($"/api/v1/products?q={Uri.EscapeDataString("100%")}");
        var (_, noMatch) = await fixture.Development.GetJsonAsync($"/api/v1/products?q={Uri.EscapeDataString("Alt%Kablo")}");
        var (_, underscoreNoMatch) = await fixture.Development.GetJsonAsync($"/api/v1/products?q={Uri.EscapeDataString("Alt_Kablo")}");
        var (_, backslash) = await fixture.Development.GetJsonAsync("/api/v1/products?q=%5C"); // q=\
        using var _p = percent;
        using var _u = underscore;
        using var _b = both;
        using var _n = noMatch;
        using var _un = underscoreNoMatch;
        using var _bs = backslash;

        // Jokersiz davranış: yalnız adında gerçekten % / _ geçen ürün. Joker olsaydı 9 ürünün hepsi dönerdi.
        Assert.Equal(["fx-pil-yuzde"], percent.RootElement.Slugs());
        Assert.Equal(["fx-alt-cizgi"], underscore.RootElement.Slugs());
        Assert.Equal(["fx-pil-yuzde"], both.RootElement.Slugs());
        Assert.Empty(noMatch.RootElement.Slugs());           // "Alt%Kablo" joker değil: "Alt_Cizgi Kablo" eşleşmez
        Assert.Empty(underscoreNoMatch.RootElement.Slugs()); // "Alt_Kablo" tek-karakter jokeri değil
        Assert.Empty(backslash.RootElement.Slugs());         // kaçış karakteri de literal; SQL hatası vermez
    }

    [Theory]
    [InlineData("' OR 1=1 --")]
    [InlineData("'; DROP TABLE products; --")]
    [InlineData("\" OR \"\"=\"")]
    [InlineData("') UNION SELECT sku FROM product_variants --")]
    [InlineData("%' OR '1'='1")]
    public async Task SQL_injection_input_is_treated_as_plain_text_and_leaks_nothing(string q)
    {
        var (response, json) = await fixture.Development.GetJsonAsync($"/api/v1/products?q={Uri.EscapeDataString(q)}");
        using var _ = json;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, json.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Empty(json.RootElement.Slugs());

        // Tablolar yerinde ve veri bozulmadı.
        Assert.Equal(14, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM products", TestContext.Current.CancellationToken));
        Assert.Equal(CatalogApiFixture.VisibleProductCount, await CountAsync(string.Empty));
    }

    [Fact]
    public async Task Search_and_category_and_price_filters_combine_with_AND()
    {
        var (_, json) = await fixture.Development.GetJsonAsync("/api/v1/products?category=fx-elektronik&q=telefon&maxPriceMinor=2000000");
        using var _ = json;

        Assert.Equal(["fx-telefon-a"], json.RootElement.Slugs()); // Telefon B (42999 TL) fiyat sınırı dışında
    }

    // ---------- Fiyat filtresi ----------

    [Fact]
    public async Task Price_filter_uses_starting_price_inclusive_bounds_in_kurus()
    {
        var (_, between) = await fixture.Development.GetJsonAsync("/api/v1/products?minPriceMinor=7500&maxPriceMinor=19990&sort=price_asc");
        var (_, minOnly) = await fixture.Development.GetJsonAsync("/api/v1/products?minPriceMinor=1000000&sort=price_asc");
        var (_, maxOnly) = await fixture.Development.GetJsonAsync("/api/v1/products?maxPriceMinor=2550&sort=price_asc");
        var (_, none) = await fixture.Development.GetJsonAsync("/api/v1/products?minPriceMinor=99999999");
        using var _b = between;
        using var _mi = minOnly;
        using var _ma = maxOnly;
        using var _n = none;

        // 75.00 (Kablo), 80.00 (Torun), 199.90 (Kılıf A/B): uçlar dahil
        Assert.Equal(4, between.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(["fx-telefon-a", "fx-telefon-b"], minOnly.RootElement.Slugs());   // 10000.00 dahil
        Assert.Equal(["fx-pil-yuzde"], maxOnly.RootElement.Slugs());                    // 25.50 dahil
        Assert.Empty(none.RootElement.Slugs());
    }

    [Fact]
    public async Task Price_filter_ignores_inactive_variant_prices()
    {
        // Yarım Pasif: aktif 500.00, pasif 1.00. 100 TL altı filtresinde GELMEMELİ (pasif varyant fiyata katılmaz).
        var (_, json) = await fixture.Development.GetJsonAsync("/api/v1/products?maxPriceMinor=10000");
        using var _ = json;

        Assert.DoesNotContain("fx-yarim-pasif", json.RootElement.Slugs());
    }

    // ---------- Detay ----------

    [Fact]
    public async Task Detail_returns_full_contract_with_sorted_active_variants_and_ordered_images()
    {
        var (response, json) = await fixture.Development.GetJsonAsync("/api/v1/products/fx-telefon-a");
        using var _ = json;
        var root = json.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["brand", "category", "currency", "description", "id", "images", "inStock", "isDemo", "name", "slug", "specifications", "startingPriceMinor", "variants"],
            root.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("Telefon A", root.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("brand").ValueKind);
        Assert.Equal("Açıklama metni", root.GetProperty("description").GetString());
        Assert.Equal("fx-telefon", root.GetProperty("category").GetProperty("slug").GetString());
        Assert.Equal("TRY", root.GetProperty("currency").GetString());
        Assert.True(root.GetProperty("isDemo").GetBoolean());
        Assert.Equal(1_000_000, root.GetProperty("startingPriceMinor").GetInt64());

        // Özellik anahtarları olduğu gibi (camelCase'e çevrilmez, Türkçe karakter korunur).
        var specs = root.GetProperty("specifications");
        Assert.Equal("6.1 inç", specs.GetProperty("Ekran").GetString());
        Assert.Equal("Telefon", specs.GetProperty("Tür").GetString());

        // Varyantlar: (fiyat, sku) sıralı. 10000.00 eşitliğinde SKU ordinal: FX-TA-0 < FX-TA-1.
        var variants = root.GetProperty("variants").EnumerateArray().ToArray();
        Assert.Equal(["FX-TA-0", "FX-TA-1", "FX-TA-2"], variants.Select(v => v.GetProperty("sku").GetString()!).ToArray());
        Assert.Equal([1_000_000L, 1_000_000L, 1_200_000L], variants.Select(v => v.GetProperty("priceMinor").GetInt64()).ToArray());
        Assert.Equal(["availableQuantity", "currency", "displayName", "id", "inStock", "options", "priceMinor", "sku"], variants[0].EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("siyah", variants[0].GetProperty("options").GetProperty("renk").GetString());
        Assert.Equal("TRY", variants[0].GetProperty("currency").GetString());

        // Görseller sort_order sırasında; ana görsel işaretli.
        var images = root.GetProperty("images").EnumerateArray().ToArray();
        Assert.Equal(["/images/test/a-yan.webp", "/images/test/a-on.webp"], images.Select(i => i.GetProperty("url").GetString()!).ToArray());
        Assert.Equal([false, true], images.Select(i => i.GetProperty("isPrimary").GetBoolean()).ToArray());
        Assert.Equal([0, 1], images.Select(i => i.GetProperty("sortOrder").GetInt32()).ToArray());
        Assert.Equal(["altText", "isPrimary", "sortOrder", "url"], images[0].EnumerateObject().Select(p => p.Name).Order().ToArray());

        CatalogJson.AssertNoForbiddenKeys(root);
    }

    [Fact]
    public async Task Detail_exposes_brand_and_omits_inactive_variants()
    {
        var (_, lamp) = await fixture.Development.GetJsonAsync("/api/v1/products/fx-isik-lambasi");
        var (_, half) = await fixture.Development.GetJsonAsync("/api/v1/products/fx-yarim-pasif");
        using var _l = lamp;
        using var _h = half;

        Assert.Equal("Lumen", lamp.RootElement.GetProperty("brand").GetString());
        Assert.Equal(JsonValueKind.Null, lamp.RootElement.GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Null, lamp.RootElement.GetProperty("specifications").ValueKind);
        Assert.Equal(0, lamp.RootElement.GetProperty("images").GetArrayLength());

        var variants = half.RootElement.GetProperty("variants").EnumerateArray().ToArray();
        Assert.Single(variants);
        Assert.Equal("FX-YP-1", variants[0].GetProperty("sku").GetString());
        Assert.Equal(50_000, half.RootElement.GetProperty("startingPriceMinor").GetInt64());
    }

    [Theory]
    [InlineData("fx-telefon-b", 4299900L)]   // 42999.00 TL -> 4299900 kuruş
    [InlineData("fx-kilif-a", 19990L)]       // 199.90 TL: kayan nokta tuzağı (199.9 * 100 = 19990.000000000004)
    [InlineData("fx-pil-yuzde", 2550L)]      // 25.50 TL
    [InlineData("fx-alt-cizgi", 7500L)]
    public async Task Price_is_returned_in_exact_kurus_in_both_list_and_detail(string slug, long expectedMinor)
    {
        var (_, detail) = await fixture.Development.GetJsonAsync($"/api/v1/products/{slug}");
        var (_, list) = await fixture.Development.GetJsonAsync($"/api/v1/products?pageSize=50");
        using var _d = detail;
        using var _l = list;

        Assert.Equal(JsonValueKind.Number, detail.RootElement.GetProperty("startingPriceMinor").ValueKind);
        Assert.Equal(expectedMinor, detail.RootElement.GetProperty("startingPriceMinor").GetInt64());
        Assert.Equal(expectedMinor, detail.RootElement.GetProperty("variants")[0].GetProperty("priceMinor").GetInt64());

        var listed = list.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("slug").GetString() == slug);
        Assert.Equal(expectedMinor, listed.GetProperty("startingPriceMinor").GetInt64());
    }

    [Fact]
    public async Task Detail_works_for_every_listed_product_and_matches_the_list_price()
    {
        var (_, list) = await fixture.Development.GetJsonAsync("/api/v1/products?pageSize=50");
        using var _l = list;

        foreach (var item in list.RootElement.GetProperty("items").EnumerateArray())
        {
            var slug = item.GetProperty("slug").GetString()!;
            var (response, detail) = await fixture.Development.GetJsonAsync($"/api/v1/products/{slug}");
            using var _d = detail;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(item.GetProperty("id").GetGuid(), detail.RootElement.GetProperty("id").GetGuid());
            Assert.Equal(item.GetProperty("startingPriceMinor").GetInt64(), detail.RootElement.GetProperty("startingPriceMinor").GetInt64());
        }
    }

    // ---------- Detay 404 ----------

    [Theory]
    [InlineData("fx-yok-boyle-urun")]            // bilinmeyen
    [InlineData("fx-gizli-pasif-urun")]          // pasif ürün
    [InlineData("fx-gizli-varyantsiz")]          // varyantı yok
    [InlineData("fx-gizli-pasif-varyant")]       // yalnız pasif varyantı var
    [InlineData("fx-gizli-pasif-kategori")]      // kategorisi pasif
    [InlineData("fx-gizli-eski-kategori")]       // alt kategorisi pasif
    public async Task Missing_inactive_or_unsellable_product_returns_404_product_not_found(string slug)
    {
        var (response, json) = await fixture.Development.GetJsonAsync($"/api/v1/products/{slug}");
        using var _ = json;

        CatalogJson.AssertProblem(response, json.RootElement, 404, "product_not_found");
    }

    [Theory]
    [InlineData("FX-TELEFON-A")]                 // büyük harf: biçim bozuk (slug'lar küçük harf)
    [InlineData("fx telefon a")]
    [InlineData("fx-telefon-a%0A")]              // sonda satır sonu
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    [InlineData("ürün")]
    [InlineData("-fx-telefon-a")]
    [InlineData("fx--telefon-a")]
    public async Task Malformed_slug_returns_404_product_not_found_without_touching_the_database(string slug)
    {
        var (response, json) = await fixture.Development.GetJsonAsync($"/api/v1/products/{slug}");
        using var _ = json;

        CatalogJson.AssertProblem(response, json.RootElement, 404, "product_not_found");
    }

    [Fact]
    public async Task Over_long_slug_returns_404()
    {
        var (response, json) = await fixture.Development.GetJsonAsync($"/api/v1/products/{new string('a', 161)}");
        using var _ = json;

        CatalogJson.AssertProblem(response, json.RootElement, 404, "product_not_found");
    }

    // ---------- Sızıntı: hiçbir listede gizli ürün yok ----------

    [Fact]
    public async Task Hidden_products_never_appear_in_any_listing()
    {
        var (_, all) = await fixture.Development.GetJsonAsync("/api/v1/products?pageSize=50");
        var (_, byElectronics) = await fixture.Development.GetJsonAsync("/api/v1/products?category=fx-elektronik&pageSize=50");
        var (_, byHome) = await fixture.Development.GetJsonAsync("/api/v1/products?category=fx-ev&pageSize=50");
        using var _a = all;
        using var _e = byElectronics;
        using var _h = byHome;

        foreach (var doc in new[] { all, byElectronics, byHome })
        {
            Assert.DoesNotContain(doc.RootElement.Slugs(), s => s.StartsWith("fx-gizli-", StringComparison.Ordinal));
            CatalogJson.AssertNoForbiddenKeys(doc.RootElement);
        }

        Assert.Equal(9, all.RootElement.GetProperty("totalCount").GetInt64());
    }
}

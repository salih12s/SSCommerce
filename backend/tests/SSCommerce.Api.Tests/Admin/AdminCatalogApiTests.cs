using System.Net;
using System.Text.Json;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Carts;
using SSCommerce.Api.Tests.Inventory;

namespace SSCommerce.Api.Tests.Admin;

/// <summary>
/// INTEGRATION / API testleri (gerçek PostgreSQL + tam HTTP hattı): admin katalog yönetimi. Neden integration: sürüm karşılaştırma kilitli satırda yapılır,
/// benzersizlik ve döngü/derinlik kuralları veritabanı kısıtlarındandır ve denetim satırı aynı transaction'dadır; bunların hiçbiri mock'la kanıtlanamaz.
/// Her test KENDİ kayıtlarını kurar (isolation). Sürüm kuralları: gövdede <c>version</c> zorunlu, kilitli satırla karşılaştırılır, no-op sürümü artırmaz.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class AdminCatalogApiTests(InventoryApiFixture fixture)
{
    // ---------- Kategori ----------

    [Fact]
    public async Task Creating_a_category_returns_201_with_version_1_and_an_audit_row_in_the_same_transaction()
    {
        var admin = await fixture.AdminAsync();
        var slug = $"kat-{AdminKit.Uniq()}";

        var response = await admin.PostAsync(AdminKit.Url("/categories"), new { name = "  Yeni Kategori  ", slug, sortOrder = 7 });

        using var json = await response.JsonAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var category = json.RootElement;
        Assert.Equal("Yeni Kategori", category.GetProperty("name").GetString()); // kırpıldı
        Assert.Equal(slug, category.GetProperty("slug").GetString());
        Assert.Equal(1, category.GetProperty("version").GetInt32());
        Assert.Equal(7, category.GetProperty("sortOrder").GetInt32());
        Assert.True(category.GetProperty("isActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, category.GetProperty("parentId").ValueKind);
        Assert.Equal(0, category.GetProperty("productCount").GetInt32());
        var id = category.GetProperty("id").GetGuid();
        Assert.Equal(1, await fixture.AuditCountAsync("category.create", id));
        Assert.Equal(admin.UserId, await fixture.ScalarAsync<Guid>($"SELECT actor_user_id FROM admin_audit_log WHERE target_id = '{id}'"));
    }

    [Fact]
    public async Task A_duplicate_category_slug_is_409_category_slug_taken_and_an_unknown_parent_is_404_category_not_found()
    {
        var admin = await fixture.AdminAsync();
        var (_, _, slug) = await admin.NewCategoryAsync();

        var taken = await admin.PostAsync(AdminKit.Url("/categories"), new { name = "Başka", slug });
        var orphan = await admin.PostAsync(AdminKit.Url("/categories"), new { name = "Yetim", slug = $"kat-{AdminKit.Uniq()}", parentId = Guid.NewGuid() });

        await ApiAssert.ProblemAsync(taken, 409, "category_slug_taken");
        await ApiAssert.ProblemAsync(orphan, 404, "category_not_found");
    }

    [Fact]
    public async Task Patching_a_category_bumps_the_version_by_exactly_one_writes_old_and_new_values_to_the_audit_and_never_changes_the_slug()
    {
        var admin = await fixture.AdminAsync();
        var (id, version, slug) = await admin.NewCategoryAsync();

        var response = await admin.PatchAsync(AdminKit.Url($"/categories/{id}"), new { version, name = "Yeni Ad", sortOrder = 3, isActive = false, slug = "hacked-slug" });

        using var json = await response.JsonAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(version + 1, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("Yeni Ad", json.RootElement.GetProperty("name").GetString());
        Assert.False(json.RootElement.GetProperty("isActive").GetBoolean());
        Assert.Equal(slug, json.RootElement.GetProperty("slug").GetString()); // slug değişmez
        var changes = await fixture.ScalarAsync<string>($"SELECT changes::text FROM admin_audit_log WHERE action = 'category.update' AND target_id = '{id}'");
        using var audit = JsonDocument.Parse(changes!);
        Assert.True(audit.RootElement.GetProperty("old").GetProperty("isActive").GetBoolean());
        Assert.False(audit.RootElement.GetProperty("new").GetProperty("isActive").GetBoolean());
        Assert.StartsWith("Kategori kat-", audit.RootElement.GetProperty("old").GetProperty("name").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_category_version_is_409_version_conflict_with_the_current_version_and_changes_nothing()
    {
        var admin = await fixture.AdminAsync();
        var (id, version, _) = await admin.NewCategoryAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsync(AdminKit.Url($"/categories/{id}"), new { version, name = "Birinci" })).StatusCode);
        var auditBefore = await fixture.AuditTotalAsync();

        var stale = await admin.PatchAsync(AdminKit.Url($"/categories/{id}"), new { version, name = "Eski sürümle" });

        using var json = await stale.JsonAsync();
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("version_conflict", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(version + 1, json.RootElement.GetProperty("currentVersion").GetInt32());
        Assert.Equal("Birinci", await fixture.ScalarAsync<string>($"SELECT name FROM categories WHERE id = '{id}'"));
        Assert.Equal(auditBefore, await fixture.AuditTotalAsync());
    }

    [Theory]
    [InlineData("""{"name":"Ad"}""")]
    [InlineData("""{"version":0,"name":"Ad"}""")]
    [InlineData("""{"version":"1","name":"Ad"}""")]
    [InlineData("""{"version":1.5,"name":"Ad"}""")]
    public async Task A_patch_without_a_valid_version_is_400_and_never_skips_the_check(string body)
    {
        var admin = await fixture.AdminAsync();
        var (id, _, _) = await admin.NewCategoryAsync();

        var response = await admin.SendAsync(HttpMethod.Patch, AdminKit.Url($"/categories/{id}"), body);

        await ApiAssert.ProblemAsync(response, 400, "validation_failed");
        Assert.Equal(1, await fixture.VersionOfAsync("categories", id));
    }

    [Fact]
    public async Task A_patch_that_changes_nothing_keeps_the_version_and_writes_no_audit_row()
    {
        var admin = await fixture.AdminAsync();
        var (id, version, _) = await admin.NewCategoryAsync(name: "Aynı Ad");
        var auditBefore = await fixture.AuditTotalAsync();

        var response = await admin.PatchAsync(AdminKit.Url($"/categories/{id}"), new { version, name = "Aynı Ad", isActive = true });

        using var json = await response.JsonAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(version, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(version, await fixture.VersionOfAsync("categories", id));
        Assert.Equal(auditBefore, await fixture.AuditTotalAsync());
    }

    [Fact]
    public async Task An_explicit_null_parent_makes_a_category_a_root_and_an_absent_parent_leaves_it_alone()
    {
        var admin = await fixture.AdminAsync();
        var (parent, _, _) = await admin.NewCategoryAsync();
        var (child, version, _) = await admin.NewCategoryAsync(parent);

        var absent = await admin.PatchAsync(AdminKit.Url($"/categories/{child}"), new { version, sortOrder = 9 });
        Assert.Equal(parent, await fixture.ScalarAsync<Guid?>($"SELECT parent_id FROM categories WHERE id = '{child}'")); // alan yok = değişmez
        var cleared = await admin.SendAsync(HttpMethod.Patch, AdminKit.Url($"/categories/{child}"), $$"""{"version":{{version + 1}},"parentId":null}""");

        Assert.Equal(HttpStatusCode.OK, absent.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null(await fixture.ScalarAsync<Guid?>($"SELECT parent_id FROM categories WHERE id = '{child}'"));
    }

    [Fact]
    public async Task A_category_cannot_become_its_own_parent_or_a_parent_of_its_ancestor()
    {
        var admin = await fixture.AdminAsync();
        var (a, versionA, _) = await admin.NewCategoryAsync();
        var (b, _, _) = await admin.NewCategoryAsync(a);
        var (c, _, _) = await admin.NewCategoryAsync(b);

        var self = await admin.PatchAsync(AdminKit.Url($"/categories/{a}"), new { version = versionA, parentId = a });
        var cycle = await admin.PatchAsync(AdminKit.Url($"/categories/{a}"), new { version = versionA, parentId = c });

        await ApiAssert.ProblemAsync(self, 409, "category_cycle");
        await ApiAssert.ProblemAsync(cycle, 409, "category_cycle");
        Assert.Null(await fixture.ScalarAsync<Guid?>($"SELECT parent_id FROM categories WHERE id = '{a}'"));
        Assert.Equal(versionA, await fixture.VersionOfAsync("categories", a));
    }

    [Fact]
    public async Task The_category_tree_may_be_five_levels_deep_and_a_sixth_level_is_409_category_too_deep()
    {
        var admin = await fixture.AdminAsync();
        var parent = (Guid?)null;
        for (var level = 1; level <= 5; level++)
        {
            parent = (await admin.NewCategoryAsync(parent)).Id;
        }

        var tooDeep = await admin.PostAsync(AdminKit.Url("/categories"), new { name = "Altıncı", slug = $"kat-{AdminKit.Uniq()}", parentId = parent });

        await ApiAssert.ProblemAsync(tooDeep, 409, "category_too_deep");
    }

    [Fact]
    public async Task Moving_a_deep_subtree_under_a_deep_parent_is_rejected_with_category_too_deep_but_a_shallow_move_works()
    {
        var admin = await fixture.AdminAsync();
        // Ağaç A: köken -> 2. -> 3. düzey (alt ağaç yüksekliği 3 olan "S" 3 düzeyli).
        var (s1, versionS1, _) = await admin.NewCategoryAsync();
        var (s2, _, _) = await admin.NewCategoryAsync(s1);
        await admin.NewCategoryAsync(s2);
        // Ağaç B: 3 düzey derinliğinde bir hedef (altına S taşınırsa 3 + 3 = 6 > 5).
        var (b1, _, _) = await admin.NewCategoryAsync();
        var (b2, _, _) = await admin.NewCategoryAsync(b1);
        var (b3, _, _) = await admin.NewCategoryAsync(b2);

        var tooDeep = await admin.PatchAsync(AdminKit.Url($"/categories/{s1}"), new { version = versionS1, parentId = b3 });
        var fits = await admin.PatchAsync(AdminKit.Url($"/categories/{s1}"), new { version = versionS1, parentId = b1 }); // 1 + 3 = 4 <= 5

        await ApiAssert.ProblemAsync(tooDeep, 409, "category_too_deep");
        Assert.Equal(HttpStatusCode.OK, fits.StatusCode);
    }

    [Fact]
    public async Task Two_admins_swapping_parents_in_parallel_never_create_a_cycle()
    {
        var admin = await fixture.AdminAsync();
        var other = await fixture.AdminAsync();
        for (var round = 0; round < 8; round++)
        {
            var (a, versionA, _) = await admin.NewCategoryAsync();
            var (b, versionB, _) = await admin.NewCategoryAsync();

            var results = await Orders.OrderKit.RunParallelAsync(2, i => i == 0
                ? admin.PatchAsync(AdminKit.Url($"/categories/{a}"), new { version = versionA, parentId = b })
                : other.PatchAsync(AdminKit.Url($"/categories/{b}"), new { version = versionB, parentId = a }));

            Assert.All(results, r => Assert.True(r.Status is HttpStatusCode.OK or HttpStatusCode.Conflict, $"beklenmeyen durum {r.Status}"));
            Assert.True(results.Count(r => r.Status == HttpStatusCode.OK) >= 1, "en az biri başarılı olmalı (iki düğüm sıraya girer)");
            var bothParents = await fixture.ScalarAsync<long>(
                $"SELECT count(*) FROM categories x JOIN categories y ON x.parent_id = y.id AND y.parent_id = x.id WHERE x.id IN ('{a}', '{b}')");
            Assert.Equal(0, bothParents);
            var cycleCodes = results.Where(r => r.Status == HttpStatusCode.Conflict).Select(r => r.Body.RootElement.GetProperty("code").GetString());
            Assert.All(cycleCodes, code => Assert.Equal("category_cycle", code));
        }
    }

    [Fact]
    public async Task The_category_list_includes_inactive_ones_counts_products_and_orders_by_sort_order_then_name()
    {
        var admin = await fixture.AdminAsync();
        var (inactive, _, _) = await admin.NewCategoryAsync(isActive: false);
        var (active, _, _) = await admin.NewCategoryAsync();
        await admin.NewProductAsync(active);
        await admin.NewProductAsync(active);

        using var list = await (await admin.GetAsync(AdminKit.Url("/categories"))).JsonAsync();

        var rows = list.RootElement.EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Array, list.RootElement.ValueKind);
        Assert.False(rows.Single(r => r.GetProperty("id").GetGuid() == inactive).GetProperty("isActive").GetBoolean());
        Assert.Equal(2, rows.Single(r => r.GetProperty("id").GetGuid() == active).GetProperty("productCount").GetInt32());
        var keys = rows.Select(r => (r.GetProperty("sortOrder").GetInt32(), r.GetProperty("name").GetString()!)).ToList();
        Assert.Equal(keys.OrderBy(k => k.Item1).ToList().Select(k => k.Item1), keys.Select(k => k.Item1));
        Assert.True(rows.Count <= 500);
    }

    // ---------- Ürün ----------

    [Fact]
    public async Task A_new_product_is_inactive_by_default_never_demo_and_invisible_on_the_public_storefront()
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();
        var slug = $"urun-{AdminKit.Uniq()}";

        var response = await admin.PostAsync(AdminKit.Url("/products"), new { categoryId = category, name = "Yeni Ürün", slug, isDemo = true, brand = "Marka", description = "Açıklama" });

        using var json = await response.JsonAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var product = json.RootElement;
        Assert.False(product.GetProperty("isActive").GetBoolean());
        Assert.False(product.GetProperty("isDemo").GetBoolean()); // sahte alan yok sayıldı
        Assert.Equal(1, product.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Array, product.GetProperty("variants").ValueKind);
        Assert.Equal(JsonValueKind.Array, product.GetProperty("images").ValueKind);
        var publicResponse = await fixture.Anonymous().GetAsync($"/api/v1/products/{slug}");
        Assert.Equal(HttpStatusCode.NotFound, publicResponse.StatusCode);
        Assert.Equal(1, await fixture.AuditCountAsync("product.create", product.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Product_slug_conflicts_and_unknown_categories_use_their_own_409_and_404_codes()
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();
        var (_, _, slug) = await admin.NewProductAsync(category);

        var taken = await admin.PostAsync(AdminKit.Url("/products"), new { categoryId = category, name = "Kopya", slug });
        var orphan = await admin.PostAsync(AdminKit.Url("/products"), new { categoryId = Guid.NewGuid(), name = "Yetim", slug = $"urun-{AdminKit.Uniq()}" });

        await ApiAssert.ProblemAsync(taken, 409, "product_slug_taken");
        await ApiAssert.ProblemAsync(orphan, 404, "category_not_found");
    }

    [Fact]
    public async Task Patching_a_product_changes_fields_bumps_the_version_once_moves_category_and_clears_nullable_fields_with_null()
    {
        var admin = await fixture.AdminAsync();
        var (first, _, _) = await admin.NewCategoryAsync();
        var (second, _, _) = await admin.NewCategoryAsync();
        var create = await admin.PostAsync(AdminKit.Url("/products"), new
        {
            categoryId = first, name = "Ürün", slug = $"urun-{AdminKit.Uniq()}", brand = "Marka", description = "Uzun açıklama",
            specifications = new Dictionary<string, string> { ["Renk"] = "Siyah" },
        });
        var created = await create.Expect(HttpStatusCode.Created, p => (p.GetProperty("id").GetGuid(), p.GetProperty("version").GetInt32()));

        var changed = await admin.SendAsync(
            HttpMethod.Patch,
            AdminKit.Url($"/products/{created.Item1}"),
            $$"""{"version":{{created.Item2}},"categoryId":"{{second}}","name":"Yeni Ürün Adı","brand":null,"description":null,"specifications":null,"isActive":true}""");

        using var json = await changed.JsonAsync();
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var p = json.RootElement;
        Assert.Equal(created.Item2 + 1, p.GetProperty("version").GetInt32());
        Assert.Equal(second, p.GetProperty("categoryId").GetGuid());
        Assert.Equal("Yeni Ürün Adı", p.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("brand").ValueKind);
        Assert.Equal(JsonValueKind.Null, p.GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Null, p.GetProperty("specifications").ValueKind);
        Assert.True(p.GetProperty("isActive").GetBoolean());
        Assert.Equal(1, await fixture.AuditCountAsync("product.update", created.Item1));
    }

    [Fact]
    public async Task Moving_a_product_to_an_unknown_category_is_404_and_changes_nothing()
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();
        var (product, version, _) = await admin.NewProductAsync(category);

        var response = await admin.PatchAsync(AdminKit.Url($"/products/{product}"), new { version, categoryId = Guid.NewGuid(), name = "Değişmemeli" });

        await ApiAssert.ProblemAsync(response, 404, "category_not_found");
        Assert.Equal(version, await fixture.VersionOfAsync("products", product));
    }

    [Fact]
    public async Task Two_parallel_patches_with_the_same_version_give_exactly_one_200_and_one_409()
    {
        var admin = await fixture.AdminAsync();
        var other = await fixture.AdminAsync();
        for (var round = 0; round < 6; round++)
        {
            var (category, _, _) = await admin.NewCategoryAsync();
            var (product, version, _) = await admin.NewProductAsync(category);

            var results = await Orders.OrderKit.RunParallelAsync(2, i => (i == 0 ? admin : other)
                .PatchAsync(AdminKit.Url($"/products/{product}"), new { version, name = $"Ad {i}" }));

            Assert.Single(results, r => r.Status == HttpStatusCode.OK);
            var loser = Assert.Single(results, r => r.Status == HttpStatusCode.Conflict);
            Assert.Equal("version_conflict", loser.Body.RootElement.GetProperty("code").GetString());
            Assert.Equal(version + 1, loser.Body.RootElement.GetProperty("currentVersion").GetInt32());
            Assert.Equal(version + 1, await fixture.VersionOfAsync("products", product)); // tam +1, çift artış yok
            Assert.Equal(1, await fixture.AuditCountAsync("product.update", product));
        }
    }

    [Fact]
    public async Task Product_list_supports_search_status_and_category_filters_with_a_page_size_cap()
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();
        var token = AdminKit.Uniq();
        var (active, _, _) = await admin.NewProductAsync(category, isActive: true);
        var (inactive, _, _) = await admin.NewProductAsync(category, isActive: false);
        var variant = await admin.NewVariantAsync(inactive);
        await fixture.ExecuteAsync($"UPDATE products SET name = 'Arama {token} etiketi' WHERE id = '{active}'");

        using var byName = await (await admin.GetAsync(AdminKit.Url($"/products?q={token}"))).JsonAsync();
        using var bySku = await (await admin.GetAsync(AdminKit.Url($"/products?q={variant.Sku}"))).JsonAsync();
        using var inactiveOnly = await (await admin.GetAsync(AdminKit.Url($"/products?categoryId={category}&status=inactive"))).JsonAsync();
        using var all = await (await admin.GetAsync(AdminKit.Url($"/products?categoryId={category}&status=all"))).JsonAsync();
        var tooBig = await admin.GetAsync(AdminKit.Url("/products?pageSize=51"));
        var badStatus = await admin.GetAsync(AdminKit.Url("/products?status=bogus"));

        Assert.Equal(active, Assert.Single(byName.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(inactive, Assert.Single(bySku.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(inactive, Assert.Single(inactiveOnly.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(2, all.RootElement.GetProperty("totalCount").GetInt32());
        var summary = bySku.RootElement.GetProperty("items")[0];
        Assert.Equal(1, summary.GetProperty("variantCount").GetInt32());
        Assert.Equal(10_000, summary.GetProperty("minPriceMinor").GetInt64());
        await ApiAssert.ProblemAsync(tooBig, 400, "validation_failed");
        await ApiAssert.ProblemAsync(badStatus, 400, "validation_failed");
    }

    [Fact]
    public async Task The_product_detail_includes_inactive_variants_and_every_version_and_an_unknown_id_is_404_a_bad_id_400()
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();
        var (product, _, _) = await admin.NewProductAsync(category);
        await admin.NewVariantAsync(product, isActive: false);

        using var detail = await (await admin.GetAsync(AdminKit.Url($"/products/{product}"))).JsonAsync();
        var missing = await admin.GetAsync(AdminKit.Url($"/products/{Guid.NewGuid()}"));
        var malformed = await admin.GetAsync(AdminKit.Url("/products/not-a-uuid"));

        var variant = Assert.Single(detail.RootElement.GetProperty("variants").EnumerateArray());
        Assert.False(variant.GetProperty("isActive").GetBoolean());
        Assert.Equal(1, variant.GetProperty("version").GetInt32());
        Assert.Equal(category, detail.RootElement.GetProperty("categoryId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(detail.RootElement.GetProperty("categoryName").GetString()));
        await ApiAssert.ProblemAsync(missing, 404, "product_not_found");
        await ApiAssert.ProblemAsync(malformed, 400, "validation_failed");
    }

    // ---------- Görseller ----------

    [Fact]
    public async Task The_image_asset_list_is_the_twelve_approved_local_files_with_credit_keys_and_authors()
    {
        var admin = await fixture.AdminAsync();

        using var json = await (await admin.GetAsync(AdminKit.Url("/catalog/image-assets"))).JsonAsync();

        var assets = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(12, assets.Count);
        Assert.All(assets, a =>
        {
            Assert.StartsWith("/images/urun/", a.GetProperty("storageKey").GetString(), StringComparison.Ordinal);
            Assert.Equal(a.GetProperty("storageKey").GetString(), a.GetProperty("creditKey").GetString());
            Assert.False(string.IsNullOrWhiteSpace(a.GetProperty("author").GetString()));
        });
    }

    [Fact]
    public async Task Replacing_images_with_approved_assets_sets_order_and_primary_bumps_the_product_version_and_audits()
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();
        var (product, version, _) = await admin.NewProductAsync(category);

        var response = await admin.PutAsync(AdminKit.Url($"/products/{product}/images"), new
        {
            version,
            images = new object[]
            {
                new { storageKey = "/images/urun/klavye.webp", altText = "Klavye", isPrimary = false },
                new { storageKey = "/images/urun/monitor.webp", altText = "Monitör", isPrimary = true },
            },
        });

        using var json = await response.JsonAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(version + 1, json.RootElement.GetProperty("version").GetInt32());
        var images = json.RootElement.GetProperty("images").EnumerateArray().ToList();
        Assert.Equal(["/images/urun/klavye.webp", "/images/urun/monitor.webp"], images.Select(i => i.GetProperty("storageKey").GetString()));
        Assert.Equal([0, 1], images.Select(i => i.GetProperty("sortOrder").GetInt32()));
        Assert.Equal("/images/urun/monitor.webp", images.Single(i => i.GetProperty("isPrimary").GetBoolean()).GetProperty("url").GetString());
        Assert.Equal(2, await fixture.CountAsync($"SELECT count(*) FROM product_images WHERE product_id = '{product}' AND credit_key = storage_key"));
        Assert.Equal(1, await fixture.AuditCountAsync("product.images_replace", product));

        // Boş küme tüm görselleri kaldırır.
        var cleared = await admin.PutAsync(AdminKit.Url($"/products/{product}/images"), new { version = version + 1, images = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(0, await fixture.CountAsync($"SELECT count(*) FROM product_images WHERE product_id = '{product}'"));
    }

    [Theory]
    [InlineData("/images/hero/masa-ustu-teknoloji.webp")]
    [InlineData("/images/urun/yok-boyle-bir-dosya.webp")]
    public async Task An_image_outside_the_approved_list_is_409_image_not_approved_and_changes_nothing(string storageKey)
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();
        var (product, version, _) = await admin.NewProductAsync(category);

        var response = await admin.PutAsync(AdminKit.Url($"/products/{product}/images"), new { version, images = new[] { new { storageKey, altText = "Alt", isPrimary = true } } });

        await ApiAssert.ProblemAsync(response, 409, "image_not_approved");
        Assert.Equal(version, await fixture.VersionOfAsync("products", product));
        Assert.Equal(0, await fixture.CountAsync($"SELECT count(*) FROM product_images WHERE product_id = '{product}'"));
    }

    [Theory]
    [InlineData("https://evil.example/x.webp")]
    [InlineData("/images/urun/../../etc/passwd.webp")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/images/urun/KLAVYE.webp")]
    public async Task An_external_url_or_a_path_traversal_is_400_validation_failed_not_a_database_error(string storageKey)
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();
        var (product, version, _) = await admin.NewProductAsync(category);

        var response = await admin.PutAsync(AdminKit.Url($"/products/{product}/images"), new { version, images = new[] { new { storageKey, altText = "Alt", isPrimary = true } } });

        await ApiAssert.ProblemAsync(response, 400, "validation_failed");
    }

    [Fact]
    public async Task Replacing_images_with_a_stale_version_is_409_version_conflict()
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();
        var (product, version, _) = await admin.NewProductAsync(category);
        await admin.PatchAsync(AdminKit.Url($"/products/{product}"), new { version, name = "Sürümü ilerlet" });

        var response = await admin.PutAsync(AdminKit.Url($"/products/{product}/images"), new { version, images = Array.Empty<object>() });

        await ApiAssert.ProblemAsync(response, 409, "version_conflict");
    }

    // ---------- Varyant ----------

    [Fact]
    public async Task A_variant_is_created_with_a_price_in_kurus_and_the_sku_and_options_are_unique()
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();
        var (product, _, _) = await admin.NewProductAsync(category);
        var sku = AdminKit.NewSku();
        var options = new Dictionary<string, string> { ["renk"] = "siyah" };

        var created = await admin.PostAsync(AdminKit.Url($"/products/{product}/variants"), new { sku, displayName = "Siyah", priceMinor = 129_990, options });
        var sameSku = await admin.PostAsync(AdminKit.Url($"/products/{product}/variants"), new { sku, displayName = "Başka", priceMinor = 100, options = new Dictionary<string, string> { ["renk"] = "beyaz" } });
        var sameOptions = await admin.PostAsync(AdminKit.Url($"/products/{product}/variants"), new { sku = AdminKit.NewSku(), displayName = "Kopya", priceMinor = 100, options });
        var unknownProduct = await admin.PostAsync(AdminKit.Url($"/products/{Guid.NewGuid()}/variants"), new { sku = AdminKit.NewSku(), displayName = "Yetim", priceMinor = 100 });

        using var json = await created.JsonAsync();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(129_990, json.RootElement.GetProperty("priceMinor").GetInt64());
        Assert.Equal("siyah", json.RootElement.GetProperty("options").GetProperty("renk").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(1299.90m, await fixture.ScalarAsync<decimal>($"SELECT price FROM product_variants WHERE sku = '{sku}'"));
        await ApiAssert.ProblemAsync(sameSku, 409, "sku_taken");
        await ApiAssert.ProblemAsync(sameOptions, 409, "variant_options_taken");
        await ApiAssert.ProblemAsync(unknownProduct, 404, "product_not_found");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(9_007_199_254_740_992)]
    public async Task A_price_outside_1_to_2_pow_53_minus_1_kurus_is_400_for_create_and_patch(long priceMinor)
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();
        var (product, _, _) = await admin.NewProductAsync(category);
        var (variant, _, version) = await admin.NewVariantAsync(product, 5_000);

        var created = await admin.PostAsync(AdminKit.Url($"/products/{product}/variants"), new { sku = AdminKit.NewSku(), displayName = "X", priceMinor });
        var patched = await admin.PatchAsync(AdminKit.Url($"/variants/{variant}"), new { version, priceMinor });

        await ApiAssert.ProblemAsync(created, 400, "validation_failed");
        await ApiAssert.ProblemAsync(patched, 400, "validation_failed");
        Assert.Equal(50.00m, await fixture.ScalarAsync<decimal>($"SELECT price FROM product_variants WHERE id = '{variant}'"));
    }

    [Fact]
    public async Task The_highest_allowed_price_is_stored_and_read_back_without_precision_loss()
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();
        var (product, _, _) = await admin.NewProductAsync(category);

        var response = await admin.PostAsync(AdminKit.Url($"/products/{product}/variants"), new { sku = AdminKit.NewSku(), displayName = "Pahalı", priceMinor = 9_007_199_254_740_991 });

        using var json = await response.JsonAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(9_007_199_254_740_991, json.RootElement.GetProperty("priceMinor").GetInt64());
    }

    [Fact]
    public async Task Variant_patch_is_versioned_cannot_touch_sku_or_options_and_returns_404_for_an_unknown_variant()
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();
        var (product, _, _) = await admin.NewProductAsync(category);
        var (variant, sku, version) = await admin.NewVariantAsync(product, 5_000, new Dictionary<string, string> { ["renk"] = "kirmizi" });

        var response = await admin.PatchAsync(AdminKit.Url($"/variants/{variant}"), new { version, displayName = "Yeni Ad", priceMinor = 7_500, isActive = false, sku = "HACK-9", options = new { renk = "mavi" } });
        var stale = await admin.PatchAsync(AdminKit.Url($"/variants/{variant}"), new { version, priceMinor = 9_999 });
        var missing = await admin.PatchAsync(AdminKit.Url($"/variants/{Guid.NewGuid()}"), new { version = 1, priceMinor = 100 });

        using var json = await response.JsonAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(version + 1, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(sku, json.RootElement.GetProperty("sku").GetString());
        Assert.Equal("kirmizi", json.RootElement.GetProperty("options").GetProperty("renk").GetString());
        Assert.Equal(7_500, json.RootElement.GetProperty("priceMinor").GetInt64());
        Assert.False(json.RootElement.GetProperty("isActive").GetBoolean());
        await ApiAssert.ProblemAsync(stale, 409, "version_conflict");
        await ApiAssert.ProblemAsync(missing, 404, "variant_not_found");
        Assert.Equal(75.00m, await fixture.ScalarAsync<decimal>($"SELECT price FROM product_variants WHERE id = '{variant}'"));
    }

    [Fact]
    public async Task The_variant_search_finds_by_sku_or_product_name_and_reports_the_total_on_hand()
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();
        var (product, _, _) = await admin.NewProductAsync(category);
        var (variant, sku, _) = await admin.NewVariantAsync(product);
        var warehouse = await fixture.CreateWarehouseAsync();
        await fixture.SeedBalanceAsync(warehouse, variant, 17);

        using var json = await (await admin.GetAsync(AdminKit.Url($"/variants?q={sku}"))).JsonAsync();

        var hit = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(variant, hit.GetProperty("id").GetGuid());
        Assert.Equal(17, hit.GetProperty("onHandTotal").GetInt64());
        Assert.False(hit.GetProperty("productIsActive").GetBoolean());
        Assert.True(hit.GetProperty("isActive").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("totalCount").GetInt32());
    }

    // ---------- Gövde sağlamlığı ----------

    public static TheoryData<string, string> BadBodies => new()
    {
        { "malformed json", "{" },
        { "empty body", "" },
        { "array", "[]" },
        { "null literal", "null" },
        { "wrong type name", """{"name":12,"slug":"x"}""" },
        { "wrong type parent", """{"name":"X","slug":"x","parentId":"abc"}""" },
        { "wrong type active", """{"name":"X","slug":"x","isActive":"yes"}""" },
        { "huge sort order", """{"name":"X","slug":"x","sortOrder":99999999999}""" },
        { "control char in name", "{\"name\":\"Ad\\u0000\",\"slug\":\"x\"}" },
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task Malformed_wrongly_typed_or_hostile_category_bodies_get_400_validation_failed_never_500(string caseName, string body)
    {
        _ = caseName;
        var admin = await fixture.AdminAsync();

        var response = await admin.SendAsync(HttpMethod.Post, AdminKit.Url("/categories"), body);

        await ApiAssert.ProblemAsync(response, 400, "validation_failed");
    }

    [Fact]
    public async Task A_product_body_over_64_kb_is_rejected_but_a_large_valid_description_below_the_limit_is_accepted()
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync();

        var huge = await admin.PostAsync(AdminKit.Url("/products"), new { categoryId = category, name = "Dev", slug = $"urun-{AdminKit.Uniq()}", description = new string('a', 70_000) });
        var tooLongDescription = await admin.PostAsync(AdminKit.Url("/products"), new { categoryId = category, name = "Dev", slug = $"urun-{AdminKit.Uniq()}", description = new string('a', 4_001) });
        var large = await admin.PostAsync(AdminKit.Url("/products"), new { categoryId = category, name = "Büyük", slug = $"urun-{AdminKit.Uniq()}", description = new string('a', 4_000) });

        await ApiAssert.ProblemAsync(huge, 400, "validation_failed");
        await ApiAssert.ProblemAsync(tooLongDescription, 400, "validation_failed");
        Assert.Equal(HttpStatusCode.Created, large.StatusCode);
    }

    [Fact]
    public async Task A_non_json_content_type_is_415_and_a_category_body_over_16_kb_is_rejected()
    {
        var admin = await fixture.AdminAsync();

        var text = await admin.SendAsync(HttpMethod.Post, AdminKit.Url("/categories"), """{"name":"X","slug":"x"}""", contentType: "text/plain");
        var big = await admin.SendAsync(HttpMethod.Post, AdminKit.Url("/categories"), $$"""{"name":"X","slug":"x","pad":"{{new string('p', 17_000)}}"}""");

        await ApiAssert.ProblemAsync(text, 415, "unsupported_media_type");
        await ApiAssert.ProblemAsync(big, 400, "validation_failed");
    }

    [Fact]
    public async Task The_public_catalog_stays_active_only_while_admin_lists_show_inactive_records()
    {
        var admin = await fixture.AdminAsync();
        var (category, _, _) = await admin.NewCategoryAsync(isActive: true);
        var (product, version, slug) = await admin.NewProductAsync(category, isActive: false);
        await admin.NewVariantAsync(product);
        var anonymous = fixture.Anonymous();

        var hidden = await anonymous.GetAsync($"/api/v1/products/{slug}");
        await admin.PatchAsync(AdminKit.Url($"/products/{product}"), new { version, isActive = true });
        var shown = await anonymous.GetAsync($"/api/v1/products/{slug}");

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
    }
}

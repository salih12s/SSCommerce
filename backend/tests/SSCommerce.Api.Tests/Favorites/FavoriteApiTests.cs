using System.Net;
using System.Text.Json;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.Api.Tests.Carts;

namespace SSCommerce.Api.Tests.Favorites;

/// <summary>
/// Favori uçlarının sözleşmesi (MD-1, docs/PLAN_CUSTOMER_EXPERIENCE.md): API testi = HTTP kapısından, gerçek PostgreSQL'e karşı.
/// Mock'la görülemeyen şeyler (benzersizlik, izolasyon, FK, sınır) burada doğrulanır. Her test KENDİ kullanıcısını ve ürünlerini kurar.
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class FavoriteApiTests(InventoryApiFixture fixture)
{
    // ---------- Happy path + şema ----------

    [Fact]
    public async Task Put_list_ids_and_delete_work_end_to_end_with_the_documented_shape()
    {
        var customer = await fixture.CustomerAsync();
        var product = (await fixture.NewProductAsync())[0];

        var put = await customer.AddFavoriteAsync(product);

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(string.Empty, await put.TextAsync());
        Assert.Equal(1, await fixture.FavoriteCountAsync(customer.UserId));

        using (var list = await customer.ListFavoritesAsync())
        {
            Assert.Equal(new[] { "items" }, list.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
            var item = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
            // Sözleşme: ProductListItemDto'nun TÜM alanları aynı ad VE sırayla (id = ürün kimliği) + addedAt + available.
            Assert.Equal(
                new[] { "id", "slug", "name", "category", "primaryImage", "startingPriceMinor", "currency", "isDemo", "inStock", "addedAt", "available" },
                item.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(new[] { "slug", "name" }, item.GetProperty("category").EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("primaryImage").ValueKind);
            Assert.Equal(product, item.GetProperty("id").GetGuid());
            Assert.True(item.GetProperty("available").GetBoolean());
            Assert.Equal("TRY", item.GetProperty("currency").GetString());
            Assert.Equal(JsonValueKind.Number, item.GetProperty("startingPriceMinor").ValueKind); // kuruş, tam sayı
            Assert.Equal(10000L, item.GetProperty("startingPriceMinor").GetInt64());               // 100,00 TL
            var addedAt = item.GetProperty("addedAt").GetDateTimeOffset();
            Assert.Equal(TimeSpan.Zero, addedAt.Offset);                                           // UTC
            Assert.InRange((DateTimeOffset.UtcNow - addedAt).Duration().TotalMinutes, 0, 5);
        }

        Assert.Equal(new[] { product }, await customer.FavoriteIdsAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await customer.RemoveFavoriteAsync(product)).StatusCode);
        Assert.Equal(0, await fixture.FavoriteCountAsync(customer.UserId));
        using var empty = await customer.ListFavoritesAsync();
        Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray());
        Assert.Empty(await customer.FavoriteIdsAsync());
    }

    [Fact]
    public async Task An_empty_list_is_an_empty_array_not_null()
    {
        var customer = await fixture.CustomerAsync();

        var response = await customer.GetAsync(FavoriteKit.Url);
        var ids = await customer.GetAsync($"{FavoriteKit.Url}/ids");

        Assert.Equal("""{"items":[]}""", await response.TextAsync());
        Assert.Equal("""{"productIds":[]}""", await ids.TextAsync());
    }

    [Fact]
    public async Task Both_lists_are_ordered_newest_first_with_a_fixed_clock()
    {
        var customer = await fixture.CustomerAsync();
        var products = await fixture.CreateProductsAsync(4);
        await fixture.SeedFavoritesAsync(customer.UserId, products); // dakika dakika artan sabit zamanlar: products[3] en yeni

        using var list = await customer.ListFavoritesAsync();

        var listed = list.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(products.Reverse().ToArray(), listed);
        Assert.Equal(products.Reverse().ToArray(), await customer.FavoriteIdsAsync());
        Assert.Equal(
            FavoriteKit.SeedBase.AddMinutes(3),
            list.RootElement.GetProperty("items")[0].GetProperty("addedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Responses_are_never_cacheable()
    {
        var customer = await fixture.CustomerAsync();
        var product = (await fixture.NewProductAsync())[0];

        var list = await customer.GetAsync(FavoriteKit.Url);
        var ids = await customer.GetAsync($"{FavoriteKit.Url}/ids");
        var put = await customer.AddFavoriteAsync(product);
        var anonymous = await fixture.Anonymous().GetAsync(FavoriteKit.Url);

        foreach (var response in new[] { list, ids, put, anonymous }) // 401 dahil: kişisel uçlarda hiçbir yanıt önbelleğe girmez
        {
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        }
    }

    // ---------- Katalogla tutarlılık (şema + birim) ----------

    [Fact]
    public async Task A_favorite_item_has_the_same_fields_and_values_as_the_same_product_in_the_catalog_list()
    {
        var customer = await fixture.CustomerAsync();
        // İlk varyant PASİF ve daha ucuz (100,00), ikincisi aktif (101,00): katalog yalnız aktif varyantlardan hesaplar -> 10100.
        var (_, name, productId, variants) = await fixture.CreateProductAsync([false, true]);
        var warehouse = await fixture.CreateWarehouseAsync();
        await fixture.SeedBalanceAsync(warehouse, variants[1].Id, 3);
        Assert.Equal(HttpStatusCode.NoContent, (await customer.AddFavoriteAsync(productId)).StatusCode);

        var catalog = await fixture.Anonymous().GetAsync($"/api/v1/products?q={Uri.EscapeDataString(name)}");
        using var catalogJson = await catalog.JsonAsync();
        var catalogItem = Assert.Single(catalogJson.RootElement.GetProperty("items").EnumerateArray());
        using var favorites = await customer.ListFavoritesAsync();
        var favorite = Assert.Single(favorites.RootElement.GetProperty("items").EnumerateArray());

        Assert.Equal(10100L, catalogItem.GetProperty("startingPriceMinor").GetInt64());
        Assert.Equal(catalogItem.GetProperty("startingPriceMinor").GetInt64(), favorite.GetProperty("startingPriceMinor").GetInt64());
        // Katalog öğesinin her alanı favoride AYNI ad ve değerle bulunur; fazladan yalnız addedAt ve available vardır.
        foreach (var property in catalogItem.EnumerateObject())
        {
            Assert.True(favorite.TryGetProperty(property.Name, out var value), $"Favoride alan yok: {property.Name}");
            Assert.Equal(property.Value.GetRawText(), value.GetRawText());
        }

        // Aynı ad VE aynı sıra; sonuna yalnız addedAt ve available eklenir.
        Assert.Equal(
            catalogItem.EnumerateObject().Select(p => p.Name).Concat(["addedAt", "available"]).ToArray(),
            favorite.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.True(favorite.GetProperty("inStock").GetBoolean());
    }

    // ---------- 401 ----------

    [Fact]
    public async Task Every_endpoint_rejects_an_anonymous_caller_with_401()
    {
        var anonymous = fixture.Anonymous();
        var product = (await fixture.NewProductAsync())[0];

        await ApiAssert.ProblemAsync(await anonymous.GetAsync(FavoriteKit.Url), 401, "unauthenticated");
        await ApiAssert.ProblemAsync(await anonymous.GetAsync($"{FavoriteKit.Url}/ids"), 401, "unauthenticated");
        await ApiAssert.ProblemAsync(await anonymous.AddFavoriteAsync(product), 401, "unauthenticated");
        await ApiAssert.ProblemAsync(await anonymous.RemoveFavoriteAsync(product), 401, "unauthenticated");
    }

    // ---------- 404 ----------

    [Fact]
    public async Task Putting_an_unknown_inactive_or_unsellable_product_is_404_product_not_found_and_writes_nothing()
    {
        var customer = await fixture.CustomerAsync();
        var inactiveProduct = (await fixture.NewProductAsync())[0];
        await fixture.SetProductActiveAsync(inactiveProduct, false);
        var inactiveCategory = (await fixture.NewProductAsync())[0];
        await fixture.SetCategoryActiveOfProductAsync(inactiveCategory, false);
        var noActiveVariant = (await fixture.NewProductAsync())[0];
        await fixture.SetAllVariantsActiveAsync(noActiveVariant, false);

        foreach (var id in new[] { Guid.NewGuid(), Guid.Empty, inactiveProduct, inactiveCategory, noActiveVariant })
        {
            await ApiAssert.ProblemAsync(await customer.AddFavoriteAsync(id), 404, "product_not_found");
        }

        Assert.Equal(0, await fixture.FavoriteCountAsync(customer.UserId));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("12345")]
    [InlineData("%20")]
    [InlineData("00000000-0000-0000-0000-00000000000g")]
    [InlineData("1' OR '1'='1")]
    public async Task Putting_a_malformed_product_id_is_404_product_not_found_not_400_or_500(string productId)
    {
        var customer = await fixture.CustomerAsync();

        var response = await customer.PutAsync($"{FavoriteKit.Url}/{Uri.EscapeDataString(productId)}", null);

        await ApiAssert.ProblemAsync(response, 404, "product_not_found");
        Assert.Equal(0, await fixture.FavoriteCountAsync(customer.UserId));
    }

    [Fact]
    public async Task A_product_that_leaves_sale_stays_in_the_list_as_unavailable_and_can_be_removed_but_not_re_added()
    {
        var customer = await fixture.CustomerAsync();
        // CreateProductsAsync tek kategori kurar; kategori pasifliği diğerlerini de etkilemesin diye o ürün ayrı kategoride.
        var sameCategory = await fixture.CreateProductsAsync(3);
        var otherCategory = (await fixture.NewProductAsync())[0];
        var products = new[] { sameCategory[0], otherCategory, sameCategory[1], sameCategory[2] };
        foreach (var product in products)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await customer.AddFavoriteAsync(product)).StatusCode);
        }

        await fixture.SetProductActiveAsync(products[0], false);              // ürün pasif
        await fixture.SetCategoryActiveOfProductAsync(products[1], false);    // kategori pasif
        await fixture.SetAllVariantsActiveAsync(products[2], false);          // aktif varyant kalmadı (fiyatı 101,00)
        // products[3] satışta kalır.

        using (var list = await customer.ListFavoritesAsync())
        {
            var items = list.RootElement.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid());
            Assert.Equal(4, items.Count);                                       // hiçbiri listeden kaybolmaz
            Assert.False(items[products[0]].GetProperty("available").GetBoolean());
            Assert.False(items[products[1]].GetProperty("available").GetBoolean());
            Assert.False(items[products[2]].GetProperty("available").GetBoolean());
            Assert.True(items[products[3]].GetProperty("available").GetBoolean());
            Assert.All(products.Take(3), p => Assert.False(items[p].GetProperty("inStock").GetBoolean()));
            // Aktif varyantı kalmayan ürünün fiyatı 0'a düşmez: son bilinen fiyat (kuruş).
            Assert.Equal(10100L, items[products[2]].GetProperty("startingPriceMinor").GetInt64());
        }

        await ApiAssert.ProblemAsync(await customer.AddFavoriteAsync(products[0]), 404, "product_not_found"); // yeniden eklenemez
        Assert.Equal(HttpStatusCode.NoContent, (await customer.RemoveFavoriteAsync(products[0])).StatusCode);   // ama kaldırılabilir
        Assert.Equal(3, await fixture.FavoriteCountAsync(customer.UserId));
    }

    // ---------- Idempotency ----------

    [Fact]
    public async Task Putting_the_same_product_twice_returns_204_both_times_and_keeps_a_single_untouched_row()
    {
        var customer = await fixture.CustomerAsync();
        var product = (await fixture.NewProductAsync())[0];
        Assert.Equal(HttpStatusCode.NoContent, (await customer.AddFavoriteAsync(product)).StatusCode);
        var createdAt = await fixture.ScalarAsync<DateTime>($"SELECT created_at FROM customer_favorites WHERE user_id = '{customer.UserId}'");

        var second = await customer.AddFavoriteAsync(product);

        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(1, await fixture.FavoriteRowCountAsync(customer.UserId, product));
        // "Satır yazılmaz": eklenme anı ikinci çağrıyla öne alınmaz (listedeki sıra bozulmaz).
        Assert.Equal(createdAt, await fixture.ScalarAsync<DateTime>($"SELECT created_at FROM customer_favorites WHERE user_id = '{customer.UserId}'"));
    }

    [Fact]
    public async Task Deleting_is_idempotent_for_a_product_that_is_not_a_favorite_and_for_unknown_or_malformed_ids()
    {
        var customer = await fixture.CustomerAsync();
        var product = (await fixture.NewProductAsync())[0];
        var other = (await fixture.NewProductAsync())[0];
        await customer.AddFavoriteAsync(other);

        Assert.Equal(HttpStatusCode.NoContent, (await customer.RemoveFavoriteAsync(product)).StatusCode);            // hiç eklenmemiş
        Assert.Equal(HttpStatusCode.NoContent, (await customer.RemoveFavoriteAsync(Guid.NewGuid())).StatusCode);     // olmayan ürün
        Assert.Equal(HttpStatusCode.NoContent, (await customer.DeleteAsync($"{FavoriteKit.Url}/not-a-guid")).StatusCode);
        await customer.AddFavoriteAsync(product);
        Assert.Equal(HttpStatusCode.NoContent, (await customer.RemoveFavoriteAsync(product)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await customer.RemoveFavoriteAsync(product)).StatusCode);            // ikinci kez

        Assert.Equal(new[] { other }, await customer.FavoriteIdsAsync());                                              // başkasına dokunmadı
    }

    // ---------- 409 sınır ----------

    [Fact]
    public async Task The_101st_distinct_product_is_409_favorite_limit_but_an_existing_one_is_still_204_at_the_limit()
    {
        var customer = await fixture.CustomerAsync();
        var products = await fixture.CreateProductsAsync(101);
        await fixture.SeedFavoritesAsync(customer.UserId, products.Take(100));
        Assert.Equal(100, await fixture.FavoriteCountAsync(customer.UserId));

        await ApiAssert.ProblemAsync(await customer.AddFavoriteAsync(products[100]), 409, "favorite_limit");
        Assert.Equal(100, await fixture.FavoriteCountAsync(customer.UserId));
        Assert.Equal(0, await fixture.FavoriteRowCountAsync(customer.UserId, products[100]));

        // Sınırda zaten favoride olan ürünün PUT'u idempotent başarıdır.
        Assert.Equal(HttpStatusCode.NoContent, (await customer.AddFavoriteAsync(products[0])).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await customer.AddFavoriteAsync(products[99])).StatusCode);
        Assert.Equal(100, await fixture.FavoriteCountAsync(customer.UserId));

        // Silince yer açılır; yeni ürün eklenebilir, sonra sınır yine tutar.
        Assert.Equal(HttpStatusCode.NoContent, (await customer.RemoveFavoriteAsync(products[50])).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await customer.AddFavoriteAsync(products[100])).StatusCode);
        Assert.Equal(100, await fixture.FavoriteCountAsync(customer.UserId));
        await ApiAssert.ProblemAsync(await customer.AddFavoriteAsync(products[50]), 409, "favorite_limit");
    }

    [Fact]
    public async Task The_limit_is_checked_after_existence_so_a_404_wins_over_a_409()
    {
        var customer = await fixture.CustomerAsync();
        var products = await fixture.CreateProductsAsync(100);
        await fixture.SeedFavoritesAsync(customer.UserId, products);

        await ApiAssert.ProblemAsync(await customer.AddFavoriteAsync(Guid.NewGuid()), 404, "product_not_found");
    }

    // ---------- İzolasyon ----------

    [Fact]
    public async Task One_users_favorites_are_invisible_and_untouchable_for_another_user()
    {
        var alice = await fixture.CustomerAsync();
        var bob = await fixture.CustomerAsync();
        var products = await fixture.CreateProductsAsync(3);
        foreach (var product in products)
        {
            await alice.AddFavoriteAsync(product);
        }

        // B, A'nın favorilerini göremez.
        using (var bobList = await bob.ListFavoritesAsync())
        {
            Assert.Empty(bobList.RootElement.GetProperty("items").EnumerateArray());
        }

        Assert.Empty(await bob.FavoriteIdsAsync());

        // B, A'nın ürününü "silmeye" çalışırsa yalnız KENDİ (boş) kaydı etkilenir; A'nın satırı yerinde kalır.
        Assert.Equal(HttpStatusCode.NoContent, (await bob.RemoveFavoriteAsync(products[0])).StatusCode);
        Assert.Equal(3, await fixture.FavoriteCountAsync(alice.UserId));

        // B aynı ürünü eklerse B'nin kendi satırı olur; A'nınkine dokunmaz. Aynı ürün iki kullanıcıda bağımsız bulunur.
        Assert.Equal(HttpStatusCode.NoContent, (await bob.AddFavoriteAsync(products[0])).StatusCode);
        Assert.Equal(new[] { products[0] }, await bob.FavoriteIdsAsync());
        Assert.Equal(3, (await alice.FavoriteIdsAsync()).Length);
        Assert.Equal(1, await fixture.FavoriteCountAsync(bob.UserId));
        Assert.Equal(1, await fixture.FavoriteRowCountAsync(alice.UserId, products[0]));

        // A, B'nin eklediğini silerse B'nin satırı kalır.
        await alice.RemoveFavoriteAsync(products[0]);
        Assert.Equal(new[] { products[0] }, await bob.FavoriteIdsAsync());
    }

    [Fact]
    public async Task A_user_id_in_the_url_query_or_body_never_selects_whose_favorites_are_written()
    {
        var alice = await fixture.CustomerAsync();
        var bob = await fixture.CustomerAsync();
        var product = (await fixture.NewProductAsync())[0];

        var response = await bob.SendAsync(
            HttpMethod.Put,
            $"{FavoriteKit.Url}/{product}?userId={alice.UserId}",
            JsonSerializer.Serialize(new { userId = alice.UserId, user_id = alice.UserId }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await fixture.FavoriteCountAsync(alice.UserId));
        Assert.Equal(1, await fixture.FavoriteCountAsync(bob.UserId));
        Assert.Empty(await alice.FavoriteIdsAsync());
    }

    [Fact]
    public async Task Favorites_are_removed_with_their_user_but_the_product_stays()
    {
        var customer = await fixture.CustomerAsync();
        var product = (await fixture.NewProductAsync())[0];
        await customer.AddFavoriteAsync(product);

        await fixture.ExecuteAsync($"DELETE FROM \"AspNetUsers\" WHERE \"Id\" = '{customer.UserId}'");

        Assert.Equal(0, await fixture.FavoriteCountAsync(customer.UserId));
        Assert.Equal(1, await fixture.ScalarAsync<long>($"SELECT count(*) FROM products WHERE id = '{product}'"));
    }

    // ---------- CSRF / Origin ----------

    [Fact]
    public async Task Writes_without_a_csrf_token_are_rejected_with_400_csrf_invalid_and_change_nothing()
    {
        var customer = await fixture.CustomerAsync();
        var products = await fixture.CreateProductsAsync(2);
        await customer.AddFavoriteAsync(products[0]);

        await ApiAssert.ProblemAsync(
            await customer.PutAsync($"{FavoriteKit.Url}/{products[1]}", null, sendCsrf: false), 400, "csrf_invalid");
        await ApiAssert.ProblemAsync(
            await customer.DeleteAsync($"{FavoriteKit.Url}/{products[0]}", sendCsrf: false), 400, "csrf_invalid");

        Assert.Equal(new[] { products[0] }, await customer.FavoriteIdsAsync()); // ne eklendi ne silindi
    }

    [Fact]
    public async Task Writes_from_a_foreign_origin_are_rejected_with_403_and_change_nothing()
    {
        var customer = await fixture.CustomerAsync(fixture.ProductionFactory);
        var products = await fixture.CreateProductsAsync(2);
        await customer.AddFavoriteAsync(products[0]);

        await ApiAssert.ProblemAsync(
            await customer.PutAsync($"{FavoriteKit.Url}/{products[1]}", null, origin: "https://evil.test"), 403, "origin_not_allowed");
        await ApiAssert.ProblemAsync(
            await customer.DeleteAsync($"{FavoriteKit.Url}/{products[0]}", origin: "https://evil.test"), 403, "origin_not_allowed");
        Assert.Equal(HttpStatusCode.NoContent,
            (await customer.PutAsync($"{FavoriteKit.Url}/{products[1]}", null, origin: InventoryApiFixture.ProductionOrigin)).StatusCode);

        Assert.Equal(2, await fixture.FavoriteCountAsync(customer.UserId));
    }

    // ---------- Hız sınırı (mutation) ----------

    private Task<ApiClient> LimitedCustomerAsync(out Func<ValueTask> dispose)
    {
        var factory = ApiFactory.Create(
            "Development",
            fixture.Database.ConnectionString,
            extraConfiguration: new Dictionary<string, string?>(InventoryApiFixture.HighLimits)
            {
                ["Security:RateLimit:MutationPermitLimit"] = "3",
                ["Security:RateLimit:WindowSeconds"] = "600",
            });
        dispose = factory.DisposeAsync;
        return fixture.LoginAsync(factory, SSCommerce.Infrastructure.Identity.AppRoles.Customer);
    }

    [Fact]
    public async Task Put_beyond_the_mutation_limit_is_429_rate_limited_and_writes_nothing_while_reads_are_not_counted()
    {
        var customer = await LimitedCustomerAsync(out var dispose);
        try
        {
            var products = await fixture.CreateProductsAsync(4);

            var statuses = new List<HttpStatusCode>();
            for (var i = 0; i < 3; i++)
            {
                statuses.Add((await customer.AddFavoriteAsync(products[i])).StatusCode);
            }

            for (var i = 0; i < 5; i++)
            {
                Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync(FavoriteKit.Url)).StatusCode); // okuma sayılmaz
            }

            var limited = await customer.AddFavoriteAsync(products[3]);

            Assert.All(statuses, s => Assert.Equal(HttpStatusCode.NoContent, s));
            await ApiAssert.ProblemAsync(limited, 429, "rate_limited");
            Assert.True(limited.Headers.RetryAfter?.Delta is { } delta && delta.TotalSeconds is > 0 and <= 600);
            Assert.Equal(0, await fixture.FavoriteRowCountAsync(customer.UserId, products[3])); // 429 hiçbir şey yazmadı
        }
        finally
        {
            await dispose();
        }
    }

    [Fact]
    public async Task Delete_beyond_the_mutation_limit_is_429_rate_limited_and_removes_nothing()
    {
        var customer = await LimitedCustomerAsync(out var dispose);
        try
        {
            var products = await fixture.CreateProductsAsync(2);
            await fixture.SeedFavoritesAsync(customer.UserId, products);

            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(HttpStatusCode.NoContent, (await customer.RemoveFavoriteAsync(Guid.NewGuid())).StatusCode); // 1-3
            }

            var limited = await customer.RemoveFavoriteAsync(products[0]);

            await ApiAssert.ProblemAsync(limited, 429, "rate_limited");
            Assert.Equal(2, await fixture.FavoriteCountAsync(customer.UserId));
        }
        finally
        {
            await dispose();
        }
    }

    // ---------- Kanonik olmayan guid biçimleri ----------

    [Theory]
    [InlineData("N")] // tiresiz
    [InlineData("B")] // süslü parantez
    [InlineData("P")] // parantez
    public async Task Non_canonical_guid_formats_are_404_on_put_and_204_noop_on_delete(string format)
    {
        var customer = await fixture.CustomerAsync();
        var product = (await fixture.NewProductAsync())[0];
        await customer.AddFavoriteAsync(product);
        var other = (await fixture.NewProductAsync())[0];
        var form = Uri.EscapeDataString(product.ToString(format));
        var otherForm = Uri.EscapeDataString(other.ToString(format));

        await ApiAssert.ProblemAsync(await customer.PutAsync($"{FavoriteKit.Url}/{otherForm}", null), 404, "product_not_found");
        Assert.Equal(HttpStatusCode.NoContent, (await customer.DeleteAsync($"{FavoriteKit.Url}/{form}")).StatusCode);

        Assert.Equal(0, await fixture.FavoriteRowCountAsync(customer.UserId, other));
        Assert.Equal(1, await fixture.FavoriteRowCountAsync(customer.UserId, product)); // kanonik olmayan DELETE hiçbir şeyi silmedi
    }

    [Fact]
    public async Task Reading_does_not_need_a_csrf_token()
    {
        var customer = await fixture.CustomerAsync();

        var response = await customer.SendAsync(HttpMethod.Get, FavoriteKit.Url, sendCsrf: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------- Bağlı veriler ----------

    [Fact]
    public async Task A_favorite_row_does_not_block_deactivating_the_product_and_the_foreign_key_still_protects_hard_deletes()
    {
        var customer = await fixture.CustomerAsync();
        var product = (await fixture.NewProductAsync())[0];
        await customer.AddFavoriteAsync(product);

        await fixture.SetProductActiveAsync(product, false); // normal yol: pasifleştirme sorunsuz

        var exception = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(
            () => fixture.ExecuteAsync($"DELETE FROM product_variants WHERE product_id = '{product}'; DELETE FROM products WHERE id = '{product}'"));
        Assert.Equal("23503", exception.SqlState); // RESTRICT: ürün favorideyken fiziksel silinemez
        Assert.Equal(1, await fixture.FavoriteCountAsync(customer.UserId));
    }
}

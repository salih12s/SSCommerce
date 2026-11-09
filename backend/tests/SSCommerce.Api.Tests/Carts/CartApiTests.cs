using System.Net;
using System.Text.Json;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.Domain.Cart;

namespace SSCommerce.Api.Tests.Carts;

/// <summary>Sepet uçlarının sözleşmesi: okuma, ekleme/artırma, mutlak adet, silme, fiyat bütünlüğü, durumlar, doğrulama. Her test kendi kullanıcısı ve verisiyle çalışır.</summary>
[Collection(InventoryApiCollection.Name)]
public class CartApiTests(InventoryApiFixture fixture)
{
    // ---------- Okuma ----------

    [Fact]
    public async Task Reading_a_cart_that_does_not_exist_returns_an_empty_cart_and_creates_nothing()
    {
        var customer = await fixture.CustomerAsync();

        var response = await customer.GetCartAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await response.JsonAsync();
        Assert.Equal(0, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(0, json.RootElement.GetProperty("itemCount").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("subtotalMinor").GetInt64());
        Assert.Equal("TRY", json.RootElement.GetProperty("currency").GetString());
        Assert.False(json.RootElement.GetProperty("hasIssues").GetBoolean());
        Assert.Equal(0, await fixture.CartCountAsync(customer.UserId)); // GET durum değiştirmez
        Assert.Equal(0, await fixture.LineCountAsync(customer.UserId));
    }

    [Fact]
    public async Task Adding_returns_the_full_cart_shape_with_server_computed_money_in_kurus()
    {
        var customer = await fixture.CustomerAsync();
        var (variant, _, sku) = await fixture.StockedVariantAsync(50);

        var response = await customer.AddToCartAsync(variant, 3);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await response.JsonAsync();
        var root = json.RootElement;
        Assert.Equal(
            new[] { "currency", "hasIssues", "itemCount", "items", "subtotalMinor" },
            root.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var item = CartKit.Item(json, variant);
        Assert.Equal(
            new[] { "availableQuantity", "id", "imageAlt", "imageUrl", "lineSubtotalMinor", "previousUnitPriceMinor", "priceChanged", "productName", "productSlug", "quantity", "sku", "status", "unitPriceMinor", "variantId", "variantName" }
                .Order().ToArray(),
            item.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(sku, item.GetProperty("sku").GetString());
        Assert.Equal(3, item.GetProperty("quantity").GetInt32());
        Assert.Equal(10_000, item.GetProperty("unitPriceMinor").GetInt64());          // 100.00 TL
        Assert.Equal(30_000, item.GetProperty("lineSubtotalMinor").GetInt64());
        Assert.Equal(30_000, root.GetProperty("subtotalMinor").GetInt64());
        Assert.Equal(3, root.GetProperty("itemCount").GetInt32());
        Assert.Equal("available", item.GetProperty("status").GetString());
        Assert.Equal(20, item.GetProperty("availableQuantity").GetInt32());            // 50 -> cap 20
        Assert.Equal(JsonValueKind.Null, item.GetProperty("previousUnitPriceMinor").ValueKind);
        Assert.False(item.GetProperty("priceChanged").GetBoolean());
    }

    [Fact]
    public async Task Adding_the_same_variant_again_increments_the_quantity_in_one_row_and_never_touches_stock()
    {
        var customer = await fixture.CustomerAsync();
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);

        await customer.AddToCartAsync(variant, 2);
        using var second = await (await customer.AddToCartAsync(variant, 3)).JsonAsync();

        Assert.Equal(1, second.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(5, CartKit.Item(second, variant).GetProperty("quantity").GetInt32());
        Assert.Equal(5, second.RootElement.GetProperty("itemCount").GetInt32());
        Assert.Equal(1, await fixture.LineCountAsync(customer.UserId));
        Assert.Equal(1, await fixture.CartCountAsync(customer.UserId));
        // Sepet stok AYIRMAZ: bakiye, sürüm ve defter değişmez.
        Assert.Equal(10, await fixture.OnHandAsync(warehouse, variant));
        Assert.Equal(1, await fixture.VersionAsync(warehouse, variant));
        Assert.Equal(0, await fixture.MovementCountAsync(variant));
    }

    [Fact]
    public async Task Lines_come_back_in_a_stable_order_by_creation()
    {
        var customer = await fixture.CustomerAsync();
        var variants = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var (variant, _, _) = await fixture.StockedVariantAsync(5);
            variants.Add(variant);
            await customer.AddToCartAsync(variant);
        }

        for (var round = 0; round < 3; round++)
        {
            using var cart = await (await customer.GetCartAsync()).JsonAsync();
            Assert.Equal(variants, cart.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("variantId").GetGuid()).ToList());
        }
    }

    // ---------- PATCH / DELETE ----------

    [Fact]
    public async Task Patch_sets_an_absolute_quantity_and_delete_removes_the_line_returning_the_cart()
    {
        var customer = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(20);
        var (other, _, _) = await fixture.StockedVariantAsync(20);
        await customer.AddToCartAsync(variant, 4);
        await customer.AddToCartAsync(other, 1);
        var itemId = await customer.ItemIdAsync(variant);

        using var set = await (await customer.SetQuantityAsync(itemId, 9)).JsonAsync();
        Assert.Equal(9, CartKit.Item(set, variant).GetProperty("quantity").GetInt32());   // mutlak, toplama değil
        using var lower = await (await customer.SetQuantityAsync(itemId, 2)).JsonAsync();
        Assert.Equal(2, CartKit.Item(lower, variant).GetProperty("quantity").GetInt32());

        var removed = await customer.RemoveItemAsync(itemId);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        using var after = await removed.JsonAsync();
        Assert.Equal(1, after.RootElement.GetProperty("items").GetArrayLength());
        Assert.Null(await fixture.LineQuantityAsync(customer.UserId, variant));
        await ApiAssert.ProblemAsync(await customer.RemoveItemAsync(itemId), 404, "cart_item_not_found"); // ikinci silme
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("12345")]
    public async Task Malformed_or_unknown_item_ids_get_404_cart_item_not_found(string id)
    {
        var customer = await fixture.CustomerAsync();

        await ApiAssert.ProblemAsync(await customer.PatchAsync($"{CartKit.ItemsUrl}/{id}", new { quantity = 1 }), 404, "cart_item_not_found");
        await ApiAssert.ProblemAsync(await customer.DeleteAsync($"{CartKit.ItemsUrl}/{id}"), 404, "cart_item_not_found");
        await ApiAssert.ProblemAsync(await customer.SetQuantityAsync(Guid.NewGuid(), 1), 404, "cart_item_not_found");
        await ApiAssert.ProblemAsync(await customer.RemoveItemAsync(Guid.NewGuid()), 404, "cart_item_not_found");
        Assert.Equal(0, await fixture.CartCountAsync(customer.UserId)); // PATCH/DELETE sepet OLUŞTURMAZ
    }

    // ---------- Fiyat bütünlüğü ----------

    [Fact]
    public async Task Client_sent_prices_totals_discounts_and_user_ids_are_ignored()
    {
        var customer = await fixture.CustomerAsync();
        var stranger = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(10);

        var response = await customer.SendAsync(
            HttpMethod.Post,
            CartKit.ItemsUrl,
            JsonSerializer.Serialize(new
            {
                variantId = variant,
                quantity = 2,
                priceMinor = 1,
                unitPriceMinor = 1,
                unitPrice = 0.01,
                subtotalMinor = 1,
                lineSubtotalMinor = 1,
                discount = 99,
                userId = stranger.UserId,
                cartId = Guid.NewGuid(),
                status = "available",
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await response.JsonAsync();
        Assert.Equal(10_000, CartKit.Item(json, variant).GetProperty("unitPriceMinor").GetInt64());
        Assert.Equal(20_000, json.RootElement.GetProperty("subtotalMinor").GetInt64());
        Assert.Equal(1, await fixture.LineCountAsync(customer.UserId));
        Assert.Equal(0, await fixture.CartCountAsync(stranger.UserId)); // gövdedeki userId yok sayıldı

        // PATCH gövdesindeki fiyat alanları da yok sayılır.
        var itemId = await customer.ItemIdAsync(variant);
        var patch = await customer.PatchAsync($"{CartKit.ItemsUrl}/{itemId}", new { quantity = 3, priceMinor = 1, subtotalMinor = 1, userId = stranger.UserId });
        using var patched = await patch.JsonAsync();
        Assert.Equal(30_000, patched.RootElement.GetProperty("subtotalMinor").GetInt64());
    }

    [Fact]
    public async Task A_catalog_price_change_after_adding_is_reflected_with_priceChanged_and_the_previous_price_and_the_subtotal_uses_current_prices()
    {
        var customer = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var (steady, _, _) = await fixture.StockedVariantAsync(10);
        await customer.AddToCartAsync(variant, 2);   // eklendiğinde 100.00 TL
        await customer.AddToCartAsync(steady, 1);    // 100.00 TL, değişmez
        await fixture.SetPriceAsync(variant, 150.50m);

        using var cart = await (await customer.GetCartAsync()).JsonAsync();

        var item = CartKit.Item(cart, variant);
        Assert.Equal(15_050, item.GetProperty("unitPriceMinor").GetInt64());               // GÜNCEL fiyat
        Assert.Equal(30_100, item.GetProperty("lineSubtotalMinor").GetInt64());
        Assert.True(item.GetProperty("priceChanged").GetBoolean());
        Assert.Equal(10_000, item.GetProperty("previousUnitPriceMinor").GetInt64());      // eklendiği andaki
        Assert.False(CartKit.Item(cart, steady).GetProperty("priceChanged").GetBoolean());
        Assert.Equal(30_100 + 10_000, cart.RootElement.GetProperty("subtotalMinor").GetInt64()); // yalnız güncel satırların toplamı

        // Adet değişince ipucu fiyat güncel fiyatla yenilenir: artık "değişti" değil.
        var itemId = item.GetProperty("id").GetGuid();
        using var refreshed = await (await customer.SetQuantityAsync(itemId, 3)).JsonAsync();
        var after = CartKit.Item(refreshed, variant);
        Assert.False(after.GetProperty("priceChanged").GetBoolean());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("previousUnitPriceMinor").ValueKind);
        Assert.Equal(45_150, after.GetProperty("lineSubtotalMinor").GetInt64());
    }

    [Fact]
    public async Task A_sum_of_many_large_but_valid_lines_beyond_the_money_limit_gets_409_cart_total_too_large_not_500()
    {
        var customer = await fixture.CustomerAsync();
        var (a, _, _) = await fixture.StockedVariantAsync(5);
        var (b, _, _) = await fixture.StockedVariantAsync(5);
        await fixture.SetPriceAsync(a, 50_000_000_000_000m);
        await fixture.SetPriceAsync(b, 50_000_000_000_000m);
        Assert.Equal(HttpStatusCode.OK, (await customer.AddToCartAsync(a, 1)).StatusCode);

        // İkinci satır toplamı 1e14'e çıkarır (> 90.07 trilyon): reddedilir ve geri alınır.
        await ApiAssert.ProblemAsync(await customer.AddToCartAsync(b, 1), 409, "cart_total_too_large");
        Assert.Equal(1, await fixture.LineCountAsync(customer.UserId));
        // Tek satır da adetle taşar.
        await ApiAssert.ProblemAsync(await customer.AddToCartAsync(a, 1), 409, "cart_total_too_large");
        Assert.Equal(1, await fixture.LineQuantityAsync(customer.UserId, a));

        // Fiyat sonradan yükselirse okuma 409 verir ama satır silinebilir (sepet kilitlenmez).
        await fixture.SeedCartLinesAsync(customer.UserId, [b]);
        await ApiAssert.ProblemAsync(await customer.GetCartAsync(), 409, "cart_total_too_large");
        var bItem = await fixture.ScalarAsync<Guid>(
            $"SELECT i.id FROM cart_items i JOIN carts c ON c.id = i.cart_id WHERE c.user_id = '{customer.UserId}' AND i.product_variant_id = '{b}'");
        Assert.Equal(HttpStatusCode.OK, (await customer.RemoveItemAsync(bItem)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetCartAsync()).StatusCode);
    }

    // ---------- Durumlar ----------

    [Fact]
    public async Task A_missing_variant_gets_404_variant_not_found_and_creates_no_line()
    {
        var customer = await fixture.CustomerAsync();

        await ApiAssert.ProblemAsync(await customer.AddToCartAsync(Guid.NewGuid()), 404, "variant_not_found");

        Assert.Equal(0, await fixture.LineCountAsync(customer.UserId));
    }

    [Fact]
    public async Task An_inactive_variant_product_or_category_gets_409_variant_inactive()
    {
        var customer = await fixture.CustomerAsync();
        var (inactiveVariant, _, _) = await fixture.StockedVariantAsync(5);
        var (inactiveProduct, _, _) = await fixture.StockedVariantAsync(5);
        var (inactiveCategory, _, _) = await fixture.StockedVariantAsync(5);
        await fixture.SetVariantActiveAsync(inactiveVariant, false);
        await fixture.SetProductActiveOfVariantAsync(inactiveProduct, false);
        await fixture.SetCategoryActiveOfVariantAsync(inactiveCategory, false);

        await ApiAssert.ProblemAsync(await customer.AddToCartAsync(inactiveVariant), 409, "variant_inactive");
        await ApiAssert.ProblemAsync(await customer.AddToCartAsync(inactiveProduct), 409, "variant_inactive");
        await ApiAssert.ProblemAsync(await customer.AddToCartAsync(inactiveCategory), 409, "variant_inactive");

        Assert.Equal(0, await fixture.LineCountAsync(customer.UserId));
    }

    [Fact]
    public async Task Adding_an_out_of_stock_variant_gets_409_insufficient_stock_with_availableQuantity_zero()
    {
        var customer = await fixture.CustomerAsync();
        var (noBalance, _, _) = await fixture.StockedVariantAsync(0);
        var (inactiveWarehouseOnly, _) = await fixture.CreateVariantAsync();
        var hidden = await fixture.CreateWarehouseAsync(active: false);
        await fixture.SeedBalanceAsync(hidden, inactiveWarehouseOnly, 40); // yalnız PASİF depoda stok: vitrin gibi sepet de "yok" görür

        foreach (var variant in new[] { noBalance, inactiveWarehouseOnly })
        {
            var response = await customer.AddToCartAsync(variant);
            await ApiAssert.ProblemAsync(response, 409, "insufficient_stock");
            using var json = await response.JsonAsync();
            Assert.Equal(0, json.RootElement.GetProperty("availableQuantity").GetInt32());
        }

        Assert.Equal(0, await fixture.LineCountAsync(customer.UserId));
    }

    [Fact]
    public async Task Adding_more_than_the_stock_gets_409_insufficient_stock_with_the_capped_available_quantity()
    {
        var customer = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(3);

        var response = await customer.AddToCartAsync(variant, 5);

        await ApiAssert.ProblemAsync(response, 409, "insufficient_stock");
        using var json = await response.JsonAsync();
        Assert.Equal(3, json.RootElement.GetProperty("availableQuantity").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await customer.AddToCartAsync(variant, 3)).StatusCode);        // tam stok serbest
        await ApiAssert.ProblemAsync(await customer.AddToCartAsync(variant, 1), 409, "insufficient_stock"); // artış stok üstü
        Assert.Equal(3, await fixture.LineQuantityAsync(customer.UserId, variant));
    }

    [Fact]
    public async Task The_availability_the_cart_uses_equals_the_public_catalog_two_warehouses_summed_inactive_ignored_and_capped_at_20()
    {
        var customer = await fixture.CustomerAsync();
        var (variant, firstWarehouse, _) = await fixture.StockedVariantAsync(8);
        var second = await fixture.CreateWarehouseAsync();
        var inactive = await fixture.CreateWarehouseAsync(active: false);
        await fixture.SeedBalanceAsync(second, variant, 9);
        await fixture.SeedBalanceAsync(inactive, variant, 500);
        _ = firstWarehouse;

        // 8 + 9 = 17 (pasif depo sayılmaz). 17 eklenir, 18 olmaz.
        Assert.Equal(HttpStatusCode.OK, (await customer.AddToCartAsync(variant, 17)).StatusCode);
        var over = await customer.AddToCartAsync(variant, 1);
        await ApiAssert.ProblemAsync(over, 409, "insufficient_stock");
        using var json = await over.JsonAsync();
        Assert.Equal(17, json.RootElement.GetProperty("availableQuantity").GetInt32());

        using var cart = await (await customer.GetCartAsync()).JsonAsync();
        Assert.Equal(17, CartKit.Item(cart, variant).GetProperty("availableQuantity").GetInt32());
    }

    [Fact]
    public async Task The_quantity_per_line_is_capped_at_20_and_a_line_count_above_50_is_rejected()
    {
        var customer = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(50);
        Assert.Equal(HttpStatusCode.OK, (await customer.AddToCartAsync(variant, 20)).StatusCode);
        await ApiAssert.ProblemAsync(await customer.AddToCartAsync(variant, 1), 409, "cart_quantity_limit");
        var itemId = await customer.ItemIdAsync(variant);
        await ApiAssert.ProblemAsync(await customer.SetQuantityAsync(itemId, 21), 400, "validation_failed");
        Assert.Equal(20, await fixture.LineQuantityAsync(customer.UserId, variant));

        // 50 farklı satır: 51.si reddedilir, mevcut satıra ekleme (adet limiti dolmadıkça) sürer.
        var lineCustomer = await fixture.CustomerAsync();
        var product = await fixture.CreateProductAsync(Enumerable.Repeat(true, 51).ToList());
        var warehouse = await fixture.CreateWarehouseAsync();
        foreach (var (id, _) in product.Variants)
        {
            await fixture.SeedBalanceAsync(warehouse, id, 5);
        }

        await fixture.SeedCartLinesAsync(lineCustomer.UserId, product.Variants.Take(50).Select(v => v.Id));
        Assert.Equal(50, await fixture.LineCountAsync(lineCustomer.UserId));
        await ApiAssert.ProblemAsync(await lineCustomer.AddToCartAsync(product.Variants[50].Id), 409, "cart_line_limit");
        Assert.Equal(HttpStatusCode.OK, (await lineCustomer.AddToCartAsync(product.Variants[0].Id)).StatusCode);
        Assert.Equal(50, await fixture.LineCountAsync(lineCustomer.UserId));
    }

    [Fact]
    public async Task Stock_lost_after_adding_shows_out_of_stock_or_insufficient_stock_lines_and_the_prices_still_count()
    {
        var customer = await fixture.CustomerAsync();
        var (gone, goneWarehouse, _) = await fixture.StockedVariantAsync(5);
        var (partial, partialWarehouse, _) = await fixture.StockedVariantAsync(10);
        var (fine, _, _) = await fixture.StockedVariantAsync(10);
        await customer.AddToCartAsync(gone, 2);
        await customer.AddToCartAsync(partial, 6);
        await customer.AddToCartAsync(fine, 1);
        await fixture.SetStockAsync(goneWarehouse, gone, 0);
        await fixture.SetStockAsync(partialWarehouse, partial, 4);

        using var cart = await (await customer.GetCartAsync()).JsonAsync();

        Assert.Equal("out_of_stock", CartKit.Item(cart, gone).GetProperty("status").GetString());
        Assert.Equal(0, CartKit.Item(cart, gone).GetProperty("availableQuantity").GetInt32());
        Assert.Equal("insufficient_stock", CartKit.Item(cart, partial).GetProperty("status").GetString());
        Assert.Equal(4, CartKit.Item(cart, partial).GetProperty("availableQuantity").GetInt32());
        Assert.Equal("available", CartKit.Item(cart, fine).GetProperty("status").GetString());
        Assert.True(cart.RootElement.GetProperty("hasIssues").GetBoolean());
        // Stoksuz satırlar da güncel fiyatla ara toplama girer (yalnız unavailable girmez): 2 + 6 + 1 adet × 100 TL.
        Assert.Equal(90_000, cart.RootElement.GetProperty("subtotalMinor").GetInt64());
        Assert.Equal(9, cart.RootElement.GetProperty("itemCount").GetInt32());
    }

    [Fact]
    public async Task A_variant_deactivated_after_adding_becomes_unavailable_with_null_prices_and_is_excluded_from_the_subtotal()
    {
        var customer = await fixture.CustomerAsync();
        var (variantInactive, _, _) = await fixture.StockedVariantAsync(10);
        var (productInactive, _, _) = await fixture.StockedVariantAsync(10);
        var (categoryInactive, _, _) = await fixture.StockedVariantAsync(10);
        var (active, _, _) = await fixture.StockedVariantAsync(10);
        foreach (var variant in new[] { variantInactive, productInactive, categoryInactive, active })
        {
            await customer.AddToCartAsync(variant, 2);
        }

        await fixture.SetVariantActiveAsync(variantInactive, false);
        await fixture.SetProductActiveOfVariantAsync(productInactive, false);
        await fixture.SetCategoryActiveOfVariantAsync(categoryInactive, false);

        using var cart = await (await customer.GetCartAsync()).JsonAsync();

        foreach (var variant in new[] { variantInactive, productInactive, categoryInactive })
        {
            var item = CartKit.Item(cart, variant);
            Assert.Equal("unavailable", item.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("unitPriceMinor").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("lineSubtotalMinor").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("previousUnitPriceMinor").ValueKind);
            Assert.False(item.GetProperty("priceChanged").GetBoolean());
        }

        Assert.Equal(20_000, cart.RootElement.GetProperty("subtotalMinor").GetInt64());   // yalnız aktif satır
        Assert.Equal(8, cart.RootElement.GetProperty("itemCount").GetInt32());           // adetler pasif satırlar dahil
        Assert.True(cart.RootElement.GetProperty("hasIssues").GetBoolean());
    }

    [Fact]
    public async Task Decreasing_a_quantity_is_always_allowed_even_when_stock_is_gone_or_the_variant_is_inactive_but_increasing_is_not()
    {
        var customer = await fixture.CustomerAsync();
        var (variant, warehouse, _) = await fixture.StockedVariantAsync(10);
        await customer.AddToCartAsync(variant, 8);
        var itemId = await customer.ItemIdAsync(variant);
        await fixture.SetStockAsync(warehouse, variant, 2);

        Assert.Equal(HttpStatusCode.OK, (await customer.SetQuantityAsync(itemId, 5)).StatusCode);   // 5 > 2 stok ama AZALTMA
        Assert.Equal(HttpStatusCode.OK, (await customer.SetQuantityAsync(itemId, 5)).StatusCode);   // aynı adet
        var increase = await customer.SetQuantityAsync(itemId, 6);
        await ApiAssert.ProblemAsync(increase, 409, "insufficient_stock");
        using (var json = await increase.JsonAsync())
        {
            Assert.Equal(2, json.RootElement.GetProperty("availableQuantity").GetInt32());
        }

        await fixture.SetVariantActiveAsync(variant, false);
        Assert.Equal(HttpStatusCode.OK, (await customer.SetQuantityAsync(itemId, 3)).StatusCode);   // pasif varyantta da azaltılır
        await ApiAssert.ProblemAsync(await customer.SetQuantityAsync(itemId, 4), 409, "variant_inactive");
        Assert.Equal(3, await fixture.LineQuantityAsync(customer.UserId, variant));
        Assert.Equal(HttpStatusCode.OK, (await customer.RemoveItemAsync(itemId)).StatusCode);        // silme de serbest
    }

    // ---------- Doğrulama ----------

    public static TheoryData<string, string> InvalidAddBodies => new()
    {
        { "quantity 0", """{"variantId":"{V}","quantity":0}""" },
        { "quantity negative", """{"variantId":"{V}","quantity":-1}""" },
        { "quantity 21", """{"variantId":"{V}","quantity":21}""" },
        { "quantity int32 overflow", """{"variantId":"{V}","quantity":2147483648}""" },
        { "quantity string", """{"variantId":"{V}","quantity":"3"}""" },
        { "quantity float", """{"variantId":"{V}","quantity":1.5}""" },
        { "quantity null", """{"variantId":"{V}","quantity":null}""" },
        { "quantity missing", """{"variantId":"{V}"}""" },
        { "variantId missing", """{"quantity":1}""" },
        { "variantId empty guid", """{"variantId":"00000000-0000-0000-0000-000000000000","quantity":1}""" },
        { "variantId not a guid", """{"variantId":"abc","quantity":1}""" },
        { "variantId number", """{"variantId":12,"quantity":1}""" },
        { "empty object", """{}""" },
        { "empty body", "" },
        { "array", "[]" },
        { "malformed", "{" },
    };

    [Theory]
    [MemberData(nameof(InvalidAddBodies))]
    public async Task Invalid_add_bodies_get_400_validation_failed_and_write_nothing(string caseName, string template)
    {
        _ = caseName;
        var customer = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(10);

        var response = await customer.SendAsync(HttpMethod.Post, CartKit.ItemsUrl, template.Replace("{V}", variant.ToString()));

        await ApiAssert.ProblemAsync(response, 400, "validation_failed");
        Assert.Equal(0, await fixture.CartCountAsync(customer.UserId));
    }

    [Theory]
    [InlineData("""{"quantity":0}""")]
    [InlineData("""{"quantity":-3}""")]
    [InlineData("""{"quantity":21}""")]
    [InlineData("""{"quantity":"2"}""")]
    [InlineData("""{"quantity":null}""")]
    [InlineData("""{}""")]
    [InlineData("")]
    public async Task Invalid_patch_bodies_get_400_and_leave_the_line_unchanged(string body)
    {
        var customer = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        await customer.AddToCartAsync(variant, 4);
        var itemId = await customer.ItemIdAsync(variant);

        await ApiAssert.ProblemAsync(await customer.SendAsync(HttpMethod.Patch, $"{CartKit.ItemsUrl}/{itemId}", body), 400, "validation_failed");

        Assert.Equal(4, await fixture.LineQuantityAsync(customer.UserId, variant));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_body_over_16_KB_gets_400_in_both_environments(bool production)
    {
        var customer = await fixture.CustomerAsync(production ? fixture.ProductionFactory : fixture.DevelopmentFactory);
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        var body = $$"""{"variantId":"{{variant}}","quantity":1,"note":"{{new string('x', 17 * 1024)}}"}""";

        await ApiAssert.ProblemAsync(await customer.SendAsync(HttpMethod.Post, CartKit.ItemsUrl, body), 400, "validation_failed");
        await ApiAssert.ProblemAsync(await customer.SendAsync(HttpMethod.Post, CartKit.ItemsUrl, "{", contentType: "text/plain"), 415, "unsupported_media_type");
        Assert.Equal(0, await fixture.CartCountAsync(customer.UserId));
    }

    [Fact]
    public void Cart_rule_constants_match_the_contract()
    {
        Assert.Equal(20, CartRules.MaxLineQuantity);
        Assert.Equal(50, CartRules.MaxDistinctLines);
    }
}

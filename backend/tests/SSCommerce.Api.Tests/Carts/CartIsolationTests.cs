using System.Net;
using System.Text.Json;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Inventory;

namespace SSCommerce.Api.Tests.Carts;

/// <summary>
/// Kullanıcılar arası YALITIM: başkasının kaynağı 404 (403 değil: var olduğunu sızdırmaz) ve sahibin verisi değişmez.
/// IDOR (başka kullanıcının kimliğini tahmin ederek erişme) bu testlerin asıl konusudur.
/// </summary>
[Collection(InventoryApiCollection.Name)]
public class CartIsolationTests(InventoryApiFixture fixture)
{
    [Fact]
    public async Task User_B_cannot_see_patch_or_delete_user_As_cart_line_and_As_data_is_unchanged()
    {
        var a = await fixture.CustomerAsync();
        var b = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(20);
        await a.AddToCartAsync(variant, 5);
        var aItem = await a.ItemIdAsync(variant);

        // B kendi sepetinde A'nın satırını göremez.
        using (var bCart = await (await b.GetCartAsync()).JsonAsync())
        {
            Assert.Equal(0, bCart.RootElement.GetProperty("items").GetArrayLength());
        }

        await ApiAssert.ProblemAsync(await b.SetQuantityAsync(aItem, 1), 404, "cart_item_not_found");
        await ApiAssert.ProblemAsync(await b.RemoveItemAsync(aItem), 404, "cart_item_not_found");
        // B'nin kendi sepeti olsa bile A'nın satır kimliği yine 404.
        var (other, _, _) = await fixture.StockedVariantAsync(20);
        await b.AddToCartAsync(other, 1);
        await ApiAssert.ProblemAsync(await b.SetQuantityAsync(aItem, 2), 404, "cart_item_not_found");
        await ApiAssert.ProblemAsync(await b.RemoveItemAsync(aItem), 404, "cart_item_not_found");

        Assert.Equal(5, await fixture.LineQuantityAsync(a.UserId, variant));
        using var aCart = await (await a.GetCartAsync()).JsonAsync();
        Assert.Equal(5, CartKit.Item(aCart, variant).GetProperty("quantity").GetInt32());
        Assert.Equal(1, await fixture.LineQuantityAsync(b.UserId, other));
    }

    [Fact]
    public async Task The_same_variant_in_two_users_carts_is_two_independent_lines()
    {
        var a = await fixture.CustomerAsync();
        var b = await fixture.CustomerAsync();
        var (variant, _, _) = await fixture.StockedVariantAsync(20);

        await a.AddToCartAsync(variant, 3);
        await b.AddToCartAsync(variant, 4);

        Assert.Equal(3, await fixture.LineQuantityAsync(a.UserId, variant));
        Assert.Equal(4, await fixture.LineQuantityAsync(b.UserId, variant));
        Assert.NotEqual(await a.ItemIdAsync(variant), await b.ItemIdAsync(variant));
    }

    [Fact]
    public async Task User_B_cannot_use_set_default_update_delete_or_preview_with_user_As_address_and_As_address_is_unchanged()
    {
        var a = await fixture.CustomerAsync();
        var b = await fixture.CustomerAsync();
        var aFirst = await a.CreateAddressAsync("A1");
        var aSecond = await a.CreateAddressAsync("A2");
        var bOwn = await b.CreateAddressAsync("B1");
        var (variant, _, _) = await fixture.StockedVariantAsync(10);
        await b.AddToCartAsync(variant, 1);
        var before = await fixture.ScalarAsync<string>(
            $"SELECT md5(string_agg(id::text || label || phone || is_default::text || updated_at::text, ',' ORDER BY id)) FROM customer_addresses WHERE user_id = '{a.UserId}'");

        await ApiAssert.ProblemAsync(await b.PostAsync($"{CartKit.AddressesUrl}/{aSecond}/default", null), 404, "address_not_found");
        await ApiAssert.ProblemAsync(await b.PutAsync($"{CartKit.AddressesUrl}/{aFirst}", CartKit.ValidAddress("Ele geçirildi", phone: "05559998877")), 404, "address_not_found");
        await ApiAssert.ProblemAsync(await b.DeleteAsync($"{CartKit.AddressesUrl}/{aFirst}"), 404, "address_not_found");
        await ApiAssert.ProblemAsync(await b.PreviewAsync(aFirst), 404, "address_not_found");

        Assert.Equal(
            before,
            await fixture.ScalarAsync<string>(
                $"SELECT md5(string_agg(id::text || label || phone || is_default::text || updated_at::text, ',' ORDER BY id)) FROM customer_addresses WHERE user_id = '{a.UserId}'"));
        Assert.Equal(2, await fixture.AddressCountAsync(a.UserId));
        // B'nin listesinde A'nın adresi yok; B'nin kendi adresiyle preview çalışır.
        using var list = await (await b.GetAsync(CartKit.AddressesUrl)).JsonAsync();
        Assert.Equal([bOwn], list.RootElement.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(HttpStatusCode.OK, (await b.PreviewAsync(bOwn)).StatusCode);
    }

    [Fact]
    public async Task A_userId_or_ownerId_in_an_address_body_is_ignored_the_address_always_belongs_to_the_caller()
    {
        var a = await fixture.CustomerAsync();
        var b = await fixture.CustomerAsync();

        var response = await a.SendAsync(
            HttpMethod.Post,
            CartKit.AddressesUrl,
            JsonSerializer.Serialize(new
            {
                label = "Ev",
                recipientName = "Ayşe",
                phone = "5321234567",
                province = "Mersin",
                district = "Tarsus",
                streetAddress = "Sokak 5",
                postalCode = "33000",
                userId = b.UserId,
                ownerId = b.UserId,
                isDefault = false,
            }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await fixture.AddressCountAsync(a.UserId));
        Assert.Equal(0, await fixture.AddressCountAsync(b.UserId));
        using var json = await response.JsonAsync();
        Assert.True(json.RootElement.GetProperty("isDefault").GetBoolean()); // isDefault:false yok sayıldı: ilk adres varsayılan
    }

    [Fact]
    public async Task Admin_and_Warehouse_users_with_their_own_real_carts_cannot_touch_each_others_lines_and_all_carts_are_unchanged()
    {
        // Üç rol de GERÇEK birer sepete ve kendi satırına sahip. Rol ayrıcalığı başkasının satırına erişim vermez (D-051: sahiplik her sorguda user_id ile).
        var customer = await fixture.CustomerAsync();
        var admin = await fixture.AdminAsync();
        var warehouse = await fixture.LoginAsync(fixture.DevelopmentFactory, SSCommerce.Infrastructure.Identity.AppRoles.Warehouse);
        var (customerVariant, _, _) = await fixture.StockedVariantAsync(20);
        var (adminVariant, _, _) = await fixture.StockedVariantAsync(20);
        var (warehouseVariant, _, _) = await fixture.StockedVariantAsync(20);
        await customer.AddToCartAsync(customerVariant, 2);
        await admin.AddToCartAsync(adminVariant, 3);
        await warehouse.AddToCartAsync(warehouseVariant, 4);
        var customerItem = await customer.ItemIdAsync(customerVariant);
        var adminItem = await admin.ItemIdAsync(adminVariant);
        var warehouseItem = await warehouse.ItemIdAsync(warehouseVariant);

        // (saldırgan, hedef satır) çiftleri: her rol her diğerinin satırına karşı.
        var attempts = new (ApiClient Attacker, Guid TargetItem)[]
        {
            (admin, customerItem), (admin, warehouseItem),
            (warehouse, customerItem), (warehouse, adminItem),
            (customer, adminItem), (customer, warehouseItem),
        };
        foreach (var (attacker, target) in attempts)
        {
            await ApiAssert.ProblemAsync(await attacker.SetQuantityAsync(target, 1), 404, "cart_item_not_found");
            await ApiAssert.ProblemAsync(await attacker.SetQuantityAsync(target, 9), 404, "cart_item_not_found");
            await ApiAssert.ProblemAsync(await attacker.RemoveItemAsync(target), 404, "cart_item_not_found");
        }

        // Üç sepet de birebir aynı: DB satırları ve API okuması.
        Assert.Equal(2, await fixture.LineQuantityAsync(customer.UserId, customerVariant));
        Assert.Equal(3, await fixture.LineQuantityAsync(admin.UserId, adminVariant));
        Assert.Equal(4, await fixture.LineQuantityAsync(warehouse.UserId, warehouseVariant));
        foreach (var (user, variant, quantity) in new[] { (customer, customerVariant, 2), (admin, adminVariant, 3), (warehouse, warehouseVariant, 4) })
        {
            using var cart = await (await user.GetCartAsync()).JsonAsync();
            Assert.Equal(1, cart.RootElement.GetProperty("items").GetArrayLength());
            Assert.Equal(quantity, CartKit.Item(cart, variant).GetProperty("quantity").GetInt32());
            Assert.Equal(quantity * 10_000L, cart.RootElement.GetProperty("subtotalMinor").GetInt64());
            Assert.Equal(1, await fixture.LineCountAsync(user.UserId));
        }
    }
}

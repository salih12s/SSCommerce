using System.Text.Json;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Api.Tests.Carts;

/// <summary>Sepet/adres/checkout testleri için küçük yardımcılar: istemci kısayolları ve veri kurucular (her test kendi verisini kurar).</summary>
internal static class CartKit
{
    public const string CartUrl = "/api/v1/cart";
    public const string ItemsUrl = "/api/v1/cart/items";
    public const string AddressesUrl = "/api/v1/addresses";
    public const string PreviewUrl = "/api/v1/checkout/preview";

    public static Task<HttpResponseMessage> AddToCartAsync(this ApiClient client, Guid variantId, long quantity = 1) =>
        client.PostAsync(ItemsUrl, new { variantId, quantity });

    public static Task<HttpResponseMessage> SetQuantityAsync(this ApiClient client, Guid itemId, long quantity) =>
        client.PatchAsync($"{ItemsUrl}/{itemId}", new { quantity });

    public static Task<HttpResponseMessage> RemoveItemAsync(this ApiClient client, Guid itemId) =>
        client.DeleteAsync($"{ItemsUrl}/{itemId}");

    public static Task<HttpResponseMessage> GetCartAsync(this ApiClient client) => client.GetAsync(CartUrl);

    public static Task<HttpResponseMessage> PreviewAsync(this ApiClient client, Guid addressId) =>
        client.PostAsync(PreviewUrl, new { addressId });

    /// <summary>Geçerli, tam bir adres gövdesi. <paramref name="seed"/> adresleri birbirinden ayırt etmek içindir.</summary>
    public static object ValidAddress(string label = "Ev", string? phone = null, string? postalCode = "34000") => new
    {
        label,
        recipientName = "Ayşe Yılmaz",
        phone = phone ?? "05321234567",
        province = "İstanbul",
        district = "Kadıköy",
        streetAddress = "Moda Caddesi No: 12 Daire 3",
        postalCode,
    };

    public static async Task<Guid> CreateAddressAsync(this ApiClient client, string label = "Ev")
    {
        var response = await client.PostAsync(AddressesUrl, ValidAddress(label));
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        using var json = await response.JsonAsync();
        return json.RootElement.GetProperty("id").GetGuid();
    }

    public static JsonElement Item(JsonDocument cart, Guid variantId) =>
        cart.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("variantId").GetGuid() == variantId);

    public static async Task<Guid> ItemIdAsync(this ApiClient client, Guid variantId)
    {
        using var cart = await (await client.GetCartAsync()).JsonAsync();
        return Item(cart, variantId).GetProperty("id").GetGuid();
    }

    // ---------- Fixture (veri) ----------

    /// <summary>Aktif depoda <paramref name="stock"/> adet stoklu, görünür bir varyant kurar. Stok HAREKET bırakmadan doğrudan yazılır.</summary>
    public static async Task<(Guid VariantId, Guid WarehouseId, string Sku)> StockedVariantAsync(this InventoryApiFixture fixture, int stock = 50)
    {
        var (variant, sku) = await fixture.CreateVariantAsync();
        var warehouse = await fixture.CreateWarehouseAsync();
        if (stock > 0)
        {
            await fixture.SeedBalanceAsync(warehouse, variant, stock);
        }

        return (variant, warehouse, sku);
    }

    public static Task SetPriceAsync(this InventoryApiFixture fixture, Guid variantId, decimal price) =>
        fixture.ExecuteAsync(FormattableString.Invariant($"UPDATE product_variants SET price = {price}, updated_at = now() WHERE id = '{variantId}'"));

    public static Task SetVariantActiveAsync(this InventoryApiFixture fixture, Guid variantId, bool active) =>
        fixture.ExecuteAsync($"UPDATE product_variants SET is_active = {(active ? "true" : "false")} WHERE id = '{variantId}'");

    public static Task SetProductActiveOfVariantAsync(this InventoryApiFixture fixture, Guid variantId, bool active) =>
        fixture.ExecuteAsync($"UPDATE products SET is_active = {(active ? "true" : "false")} WHERE id = (SELECT product_id FROM product_variants WHERE id = '{variantId}')");

    public static Task SetCategoryActiveOfVariantAsync(this InventoryApiFixture fixture, Guid variantId, bool active) =>
        fixture.ExecuteAsync(
            $"UPDATE categories SET is_active = {(active ? "true" : "false")} WHERE id = (SELECT p.category_id FROM products p JOIN product_variants v ON v.product_id = p.id WHERE v.id = '{variantId}')");

    public static Task SetStockAsync(this InventoryApiFixture fixture, Guid warehouseId, Guid variantId, int quantity) =>
        fixture.ExecuteAsync($"UPDATE inventory_balances SET on_hand_quantity = {quantity} WHERE warehouse_id = '{warehouseId}' AND product_variant_id = '{variantId}'");

    public static async Task<long> CartCountAsync(this InventoryApiFixture fixture, Guid userId) =>
        await fixture.ScalarAsync<long>($"SELECT count(*) FROM carts WHERE user_id = '{userId}'");

    public static async Task<long> LineCountAsync(this InventoryApiFixture fixture, Guid userId) =>
        await fixture.ScalarAsync<long>($"SELECT count(*) FROM cart_items i JOIN carts c ON c.id = i.cart_id WHERE c.user_id = '{userId}'");

    public static async Task<long?> LineQuantityAsync(this InventoryApiFixture fixture, Guid userId, Guid variantId) =>
        await fixture.ScalarAsync<long?>(
            $"SELECT i.quantity FROM cart_items i JOIN carts c ON c.id = i.cart_id WHERE c.user_id = '{userId}' AND i.product_variant_id = '{variantId}'");

    public static async Task<long> AddressCountAsync(this InventoryApiFixture fixture, Guid userId) =>
        await fixture.ScalarAsync<long>($"SELECT count(*) FROM customer_addresses WHERE user_id = '{userId}'");

    public static async Task<long> DefaultAddressCountAsync(this InventoryApiFixture fixture, Guid userId) =>
        await fixture.ScalarAsync<long>($"SELECT count(*) FROM customer_addresses WHERE user_id = '{userId}' AND is_default");

    /// <summary>Çok sayıda satırı HTTP'siz, doğrudan SQL ile kurar (limit testleri için hız). Sepet satırı yoksa sepet de oluşturulur.</summary>
    public static async Task SeedCartLinesAsync(this InventoryApiFixture fixture, Guid userId, IEnumerable<Guid> variantIds, int quantity = 1)
    {
        await fixture.ExecuteAsync($"INSERT INTO carts (id, user_id, created_at, updated_at) VALUES ('{Guid.CreateVersion7()}', '{userId}', now(), now()) ON CONFLICT (user_id) DO NOTHING");
        foreach (var variantId in variantIds)
        {
            await fixture.ExecuteAsync(
                $"INSERT INTO cart_items (id, cart_id, product_variant_id, quantity, unit_price_at_add, created_at, updated_at) " +
                $"SELECT '{Guid.CreateVersion7()}', c.id, '{variantId}', {quantity}, v.price, now(), now() FROM carts c, product_variants v WHERE c.user_id = '{userId}' AND v.id = '{variantId}'");
        }
    }

    public static async Task<ApiClient> CustomerAsync(this InventoryApiFixture fixture, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? factory = null) =>
        await fixture.LoginAsync(factory ?? fixture.DevelopmentFactory, AppRoles.Customer);
}

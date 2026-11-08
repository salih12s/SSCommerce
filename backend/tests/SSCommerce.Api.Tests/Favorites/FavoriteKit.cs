using System.Text.Json;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.Domain.Catalog;

namespace SSCommerce.Api.Tests.Favorites;

/// <summary>Favori testleri için yardımcılar. Her test kendi kullanıcısını ve ürünlerini kurar (isolation); zaman damgaları sabit tabandan türetilir (saate bağlı değil).</summary>
internal static class FavoriteKit
{
    public const string Url = "/api/v1/favorites";

    /// <summary>Tohumlanan favorilerin sabit başlangıç anı: sıralama testi saate/uykuya bağlı olmasın.</summary>
    public static readonly DateTimeOffset SeedBase = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static Task<HttpResponseMessage> AddFavoriteAsync(this ApiClient client, Guid productId) => client.PutAsync($"{Url}/{productId}", null);

    public static Task<HttpResponseMessage> RemoveFavoriteAsync(this ApiClient client, Guid productId) => client.DeleteAsync($"{Url}/{productId}");

    public static async Task<JsonDocument> ListFavoritesAsync(this ApiClient client)
    {
        var response = await client.GetAsync(Url);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    public static async Task<Guid[]> FavoriteIdsAsync(this ApiClient client)
    {
        var response = await client.GetAsync($"{Url}/ids");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var json = await response.JsonAsync();
        return json.RootElement.GetProperty("productIds").EnumerateArray().Select(e => Guid.Parse(e.GetString()!)).ToArray();
    }

    public static async Task<long> FavoriteCountAsync(this InventoryApiFixture fixture, Guid userId) =>
        await fixture.ScalarAsync<long>($"SELECT count(*) FROM customer_favorites WHERE user_id = '{userId}'");

    public static async Task<long> FavoriteRowCountAsync(this InventoryApiFixture fixture, Guid userId, Guid productId) =>
        await fixture.ScalarAsync<long>($"SELECT count(*) FROM customer_favorites WHERE user_id = '{userId}' AND product_id = '{productId}'");

    /// <summary>
    /// Görünür (aktif ürün + aktif kategori + aktif varyant) <paramref name="count"/> ürünü TEK kategori ve TEK kayıt gönderimiyle kurar
    /// (limit testleri 100 ürün ister; ürün başına ayrı kategori/gidiş-dönüş yavaş olurdu).
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> CreateProductsAsync(this InventoryApiFixture fixture, int count)
    {
        await using var db = fixture.Database.CreateDbContext();
        var suffix = Guid.NewGuid().ToString("N");
        var category = new Category($"Favori Kategorisi {suffix}", $"fav-{suffix}");
        db.Categories.Add(category);
        var ids = new List<Guid>(count);
        for (var i = 0; i < count; i++)
        {
            var product = new Product(category.Id, $"Favori Urunu {suffix} {i}", $"fav-urun-{suffix}-{i}", isDemo: true);
            product.AddVariant($"F{suffix[..12].ToUpperInvariant()}-{i}", $"Favori v{i}", 100.00m + i, new Dictionary<string, string> { ["secenek"] = "s" });
            db.Products.Add(product);
            ids.Add(product.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    /// <summary>
    /// Favorileri HTTP'siz, doğrudan SQL ile yazar. <c>created_at</c> sabit tabandan dakika dakika artar: i'nci ürün (0'dan) <c>SeedBase + i dk</c>,
    /// yani listede EN SON verilen en üsttedir.
    /// </summary>
    public static async Task SeedFavoritesAsync(this InventoryApiFixture fixture, Guid userId, IEnumerable<Guid> productIds)
    {
        var rows = productIds
            .Select((id, i) => $"('{userId}', '{id}', '{SeedBase.AddMinutes(i):yyyy-MM-dd HH:mm:ss}+00')")
            .ToArray();
        if (rows.Length > 0)
        {
            await fixture.ExecuteAsync($"INSERT INTO customer_favorites (user_id, product_id, created_at) VALUES {string.Join(", ", rows)}");
        }
    }

    public static async Task<IReadOnlyList<Guid>> NewProductAsync(this InventoryApiFixture fixture) => await fixture.CreateProductsAsync(1);

    public static Task SetProductActiveAsync(this InventoryApiFixture fixture, Guid productId, bool active) =>
        fixture.ExecuteAsync($"UPDATE products SET is_active = {(active ? "true" : "false")} WHERE id = '{productId}'");

    public static Task SetCategoryActiveOfProductAsync(this InventoryApiFixture fixture, Guid productId, bool active) =>
        fixture.ExecuteAsync($"UPDATE categories SET is_active = {(active ? "true" : "false")} WHERE id = (SELECT category_id FROM products WHERE id = '{productId}')");

    public static Task SetAllVariantsActiveAsync(this InventoryApiFixture fixture, Guid productId, bool active) =>
        fixture.ExecuteAsync($"UPDATE product_variants SET is_active = {(active ? "true" : "false")} WHERE product_id = '{productId}'");
}

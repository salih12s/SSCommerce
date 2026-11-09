using System.Net;
using System.Text.Json;
using SSCommerce.Api.Tests.Auth;
using SSCommerce.Api.Tests.Inventory;

namespace SSCommerce.Api.Tests.Admin;

/// <summary>
/// Faz 8 yönetim testleri için ayrı koleksiyon: kendi izole veritabanı (kategori listesi gibi "tüm kayıtları" döndüren uçlar diğer testlerin verisiyle
/// karışmasın; sıra değişse sonuç değişmesin = isolation). Aynı <see cref="InventoryApiFixture"/> sınıfı, ayrı bir örnek.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AdminApiCollection : ICollectionFixture<InventoryApiFixture>
{
    public const string Name = "admin-api";
}

/// <summary>Yönetim testleri için küçük yardımcılar: HTTP ile veri kurucular ve tek satırlık SQL sayaçları (her test KENDİ kayıtlarını kurar).</summary>
internal static class AdminKit
{
    public const string Base = "/api/v1/admin";

    /// <summary>Küçük harf/rakam slug parçası.</summary>
    public static string Uniq() => Guid.NewGuid().ToString("N")[..12];

    public static string NewSku() => "T" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    public static async Task<JsonDocument> Body(this HttpResponseMessage response) => await response.JsonAsync();

    public static async Task<T> Expect<T>(this HttpResponseMessage response, HttpStatusCode status, Func<JsonElement, T> read)
    {
        using var json = await response.JsonAsync();
        Assert.Equal(status, response.StatusCode);
        return read(json.RootElement);
    }

    // ---------- HTTP ile veri kurucular ----------

    public static async Task<(Guid Id, int Version, string Slug)> NewCategoryAsync(
        this ApiClient admin,
        Guid? parentId = null,
        bool isActive = true,
        string? name = null)
    {
        var slug = $"kat-{Uniq()}";
        var response = await admin.PostAsync($"{Base}/categories", new { name = name ?? $"Kategori {slug}", slug, parentId, isActive });
        return await response.Expect(HttpStatusCode.Created, c => (c.GetProperty("id").GetGuid(), c.GetProperty("version").GetInt32(), slug));
    }

    public static async Task<(Guid Id, int Version, string Slug)> NewProductAsync(this ApiClient admin, Guid categoryId, bool isActive = false)
    {
        var slug = $"urun-{Uniq()}";
        var response = await admin.PostAsync($"{Base}/products", new { categoryId, name = $"Ürün {slug}", slug, isActive });
        return await response.Expect(HttpStatusCode.Created, p => (p.GetProperty("id").GetGuid(), p.GetProperty("version").GetInt32(), slug));
    }

    public static async Task<(Guid Id, string Sku, int Version)> NewVariantAsync(
        this ApiClient admin,
        Guid productId,
        long priceMinor = 10_000,
        IReadOnlyDictionary<string, string>? options = null,
        bool isActive = true)
    {
        var sku = NewSku();
        var response = await admin.PostAsync(
            $"{Base}/products/{productId}/variants",
            new { sku, displayName = $"Varyant {sku}", priceMinor, options = options ?? new Dictionary<string, string> { ["renk"] = $"r{Uniq()}" }, isActive });
        return await response.Expect(HttpStatusCode.Created, v => (v.GetProperty("id").GetGuid(), sku, v.GetProperty("version").GetInt32()));
    }

    // ---------- SQL sayaçları ----------

    public static Task<long> CountAsync(this InventoryApiFixture fixture, string sql) => fixture.ScalarAsync<long>(sql);

    public static Task<long> AuditCountAsync(this InventoryApiFixture fixture, string action, Guid targetId) =>
        fixture.ScalarAsync<long>($"SELECT count(*) FROM admin_audit_log WHERE action = '{action}' AND target_id = '{targetId}'");

    public static Task<long> AuditTotalAsync(this InventoryApiFixture fixture) => fixture.ScalarAsync<long>("SELECT count(*) FROM admin_audit_log");

    public static async Task<int> VersionOfAsync(this InventoryApiFixture fixture, string table, Guid id) =>
        (int)(await fixture.ScalarAsync<long>($"SELECT version::bigint FROM {table} WHERE id = '{id}'"));

    /// <summary>Tablonun tüm satırlarının özeti (md5): iki ölçüm eşitse hiçbir satır eklenmedi/değişmedi/silinmedi.</summary>
    public static async Task<string> Md5Async(this InventoryApiFixture fixture, string table, string orderBy, string where = "true") =>
        (await fixture.ScalarAsync<string>($"SELECT coalesce(md5(string_agg(t::text, '|' ORDER BY {orderBy})), 'empty') FROM {table} t WHERE {where}"))!;

    public static string Url(string path) => $"{Base}{path}";
}

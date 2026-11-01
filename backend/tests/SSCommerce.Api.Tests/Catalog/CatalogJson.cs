using System.Net.Http.Json;
using System.Text.Json;

namespace SSCommerce.Api.Tests.Catalog;

/// <summary>Test yardımcıları: ham JSON okuma (DTO'ya çevirmeden: sızan/eksik alanlar görünsün diye).</summary>
internal static class CatalogJson
{
    /// <summary>
    /// Yanıtta HİÇBİR yerde (iç içe dahil) bulunmaması gereken anahtarlar (küçük harf). Faz 5 ile (D-048) <c>inStock</c> ve
    /// <c>availableQuantity</c> BİLİNÇLİ olarak artık izinlidir; ham stok/depo alanları (miktar, depo kimliği/kodu/adı, sürüm) yasak kalır.
    /// </summary>
    public static readonly string[] ForbiddenKeys =
    [
        "stock", "stockquantity", "quantity", "onhand", "onhandquantity", "version",
        "warehouse", "warehouseid", "warehousecode", "warehousename", "warehouses", "balances", "isactive", "optionsignature", "creditkey",
        "createdat", "updatedat", "categoryid", "productid", "storagekey", "parentid",
    ];

    public static async Task<JsonDocument> ReadJsonAsync(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(body);
    }

    public static async Task<(HttpResponseMessage Response, JsonDocument Json)> GetJsonAsync(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        return (response, await response.ReadJsonAsync());
    }

    public static string[] Slugs(this JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("slug").GetString()!).ToArray();

    public static string[] Names(this JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray();

    public static Guid[] Ids(this JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();

    /// <summary>Tüm iç içe nesnelerdeki özellik adları.</summary>
    public static IEnumerable<string> AllPropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var nested in AllPropertyNames(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in AllPropertyNames(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    public static void AssertNoForbiddenKeys(JsonElement element)
    {
        var found = AllPropertyNames(element)
            .Where(name => ForbiddenKeys.Contains(name.ToLowerInvariant().Replace("_", string.Empty)))
            .ToArray();
        Assert.Empty(found);
    }

    /// <summary>uuid'ler PostgreSQL'de bayt sırasıyla (= küçük harfli kanonik metnin ordinal sırası) karşılaştırılır; Guid.CompareTo farklı sıralar.</summary>
    public static int CompareLikePostgres(Guid a, Guid b) =>
        string.CompareOrdinal(a.ToString("D"), b.ToString("D"));

    public static void AssertProblem(HttpResponseMessage response, JsonElement body, int status, string code)
    {
        Assert.Equal((System.Net.HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(status, body.GetProperty("status").GetInt32());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }
}

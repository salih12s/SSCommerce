using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SSCommerce.Application.Catalog;

namespace SSCommerce.Api.Tests.Catalog;

/// <summary>
/// Global hata modeli (RFC 9457 + code + traceId). DB gerektirmez: bağlantı dizesi YOK iken katalog ucu istisna fırlatır
/// (DbContext çözümlenirken). Yanıt hiçbir ortamda istisna iletisini, yapılandırma anahtarını ya da yığın izini taşımamalı.
/// </summary>
public class CatalogErrorModelTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Unhandled_exception_in_catalog_endpoint_returns_500_internal_error_without_leaking_details(string environment)
    {
        await using var factory = ApiFactory.Create(environment, postgresConnectionString: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/categories", TestContext.Current.CancellationToken);
        var json = await response.ReadJsonAsync();
        using var _ = json;
        var raw = json.RootElement.GetRawText();

        CatalogJson.AssertProblem(response, json.RootElement, 500, "internal_error");
        Assert.DoesNotContain("ConnectionStrings", raw);
        Assert.DoesNotContain("   at ", raw);
        Assert.DoesNotContain("InvalidOperationException", raw);
        Assert.DoesNotContain("Password", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("yapılandırılmamış", raw);
        Assert.False(json.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == System.Text.Json.JsonValueKind.String);
    }

    // "Veritabanına inmeden": sorgu nesnesi çağrılırsa PATLAYAN sahte ICatalogQueries. Geçersiz girdi ona hiç ulaşmamalı.
    [Theory]
    [InlineData("/api/v1/products?pageSize=-1", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/products?sort=bogus", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/products?q=a%00b", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/products?category=B%C3%BCy%C3%BCk-Harf", HttpStatusCode.NotFound)]
    [InlineData("/api/v1/products?category=..%2Fetc", HttpStatusCode.NotFound)]
    [InlineData("/api/v1/products/B%C3%BCy%C3%BCk-Harf", HttpStatusCode.NotFound)]
    [InlineData("/api/v1/products/abc%0A", HttpStatusCode.NotFound)]
    public async Task Invalid_input_never_reaches_the_query_layer(string url, HttpStatusCode expected)
    {
        await using var factory = ApiFactory.Create(
            "Production",
            postgresConnectionString: null,
            services =>
            {
                services.RemoveAll<ICatalogQueries>();
                services.AddScoped<ICatalogQueries, ExplodingCatalogQueries>();
            });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task The_exploding_fake_really_is_wired_in_a_valid_request_reaches_it()
    {
        // Kontrol testi: yukarıdaki testin anlamlı olması için geçerli istek sahteye ULAŞMALI (500 internal_error).
        await using var factory = ApiFactory.Create(
            "Production",
            postgresConnectionString: null,
            services =>
            {
                services.RemoveAll<ICatalogQueries>();
                services.AddScoped<ICatalogQueries, ExplodingCatalogQueries>();
            });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/products?pageSize=5", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private sealed class ExplodingCatalogQueries : ICatalogQueries
    {
        public Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("sorgu katmanına ulaşılmamalıydı");

        public Task<ProductPageDto?> GetProductsAsync(ProductListQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("sorgu katmanına ulaşılmamalıydı");

        public Task<ProductDetailDto?> GetProductBySlugAsync(string slug, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("sorgu katmanına ulaşılmamalıydı");
    }
}

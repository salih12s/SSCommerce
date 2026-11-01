using System.Net;
using System.Text.Json;

namespace SSCommerce.Api.Tests.Catalog;

/// <summary>
/// Negative + sözleşme testleri: girdi doğrulaması (400), yazma yöntemlerinin kapalılığı, OpenAPI belgesi.
/// Hepsi gerçek HTTP hattından geçer. Development'ta minimal API bağlama hatalarını İSTİSNA olarak fırlatır
/// (ThrowOnBadRequest); sorgu parametreleri string bağlandığı için bu yol hiç devreye girmemeli: aynı vakalar Production'da da sınanır.
/// </summary>
[Collection(CatalogApiCollection.Name)]
public class CatalogContractTests(CatalogApiFixture fixture)
{
    // ---------- 400 doğrulama ----------

    public static TheoryData<string, string> InvalidQueries => new()
    {
        { "pageSize=-1", "pageSize" },
        { "pageSize=0", "pageSize" },
        { "pageSize=51", "pageSize" },
        { "pageSize=abc", "pageSize" },
        { "page=0", "page" },
        { "page=-3", "page" },
        { "page=10001", "page" },
        { "page=abc", "page" },
        { "page=1.5", "page" },
        { "page=99999999999999999999", "page" },
        { "page=1&page=2", "page" },
        { "sort=bogus", "sort" },
        { "sort=PRICE_ASC", "sort" },
        { "sort=price_asc;DROP%20TABLE%20products", "sort" },
        { "sort=created_at", "sort" },
        { "minPriceMinor=500&maxPriceMinor=100", "minPriceMinor" },
        { "minPriceMinor=-1", "minPriceMinor" },
        { "minPriceMinor=abc", "minPriceMinor" },
        { "minPriceMinor=12.5", "minPriceMinor" },
        { "maxPriceMinor=-5", "maxPriceMinor" },
        { "maxPriceMinor=9223372036854775807", "maxPriceMinor" },
        { $"q={new string('a', 101)}", "q" },
        { "q=a%00b", "q" },
        { "q=a%0Ab", "q" },
    };

    [Theory]
    [MemberData(nameof(InvalidQueries))]
    public async Task Invalid_query_returns_400_validation_failed_problem_json_in_Development(string query, string field) =>
        await AssertValidationFailure(fixture.Development, query, field);

    [Theory]
    [MemberData(nameof(InvalidQueries))]
    public async Task Invalid_query_returns_400_validation_failed_problem_json_in_Production(string query, string field) =>
        await AssertValidationFailure(fixture.Production, query, field);

    private static async Task AssertValidationFailure(HttpClient client, string query, string field)
    {
        var (response, json) = await client.GetJsonAsync($"/api/v1/products?{query}");
        using var _ = json;
        var root = json.RootElement;

        CatalogJson.AssertProblem(response, root, 400, "validation_failed");
        var errors = root.GetProperty("errors");
        Assert.True(errors.TryGetProperty(field, out var messages), $"errors içinde '{field}' bekleniyordu: {errors}");
        Assert.NotEmpty(messages.EnumerateArray());
        Assert.All(messages.EnumerateArray(), m => Assert.False(string.IsNullOrWhiteSpace(m.GetString())));

        // Sızıntı yok: yığın izi / istisna adı / SQL.
        var raw = root.GetRawText();
        Assert.DoesNotContain("   at ", raw);
        Assert.DoesNotContain("Exception", raw);
        Assert.DoesNotContain("SELECT", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Multiple_invalid_parameters_are_all_reported_in_one_response()
    {
        var (response, json) = await fixture.Development.GetJsonAsync("/api/v1/products?page=0&pageSize=99&sort=x&minPriceMinor=-1");
        using var _ = json;

        CatalogJson.AssertProblem(response, json.RootElement, 400, "validation_failed");
        Assert.Equal(
            ["minPriceMinor", "page", "pageSize", "sort"],
            json.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    [Theory]
    [InlineData("pageSize=50")]
    [InlineData("pageSize=1")]
    [InlineData("page=10000")]
    [InlineData("q=a")]
    public async Task Boundary_values_are_accepted(string query)
    {
        var response = await fixture.Development.GetAsync($"/api/v1/products?{query}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Exactly_100_character_search_is_accepted()
    {
        var response = await fixture.Development.GetAsync($"/api/v1/products?q={new string('a', 100)}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_query_parameters_are_ignored()
    {
        var response = await fixture.Development.GetAsync("/api/v1/products?foo=bar&isActive=false&includeInactive=true", TestContext.Current.CancellationToken);
        var json = await response.ReadJsonAsync();
        using var _ = json;

        // "isActive=false" gibi bir parametre pasif ürünleri AÇMAZ.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CatalogApiFixture.VisibleProductCount, json.RootElement.GetProperty("totalCount").GetInt64());
    }

    // ---------- Yazma yöntemleri yok ----------

    [Theory]
    [InlineData("POST", "/api/v1/products")]
    [InlineData("PUT", "/api/v1/products")]
    [InlineData("PATCH", "/api/v1/products")]
    [InlineData("DELETE", "/api/v1/products")]
    [InlineData("POST", "/api/v1/categories")]
    [InlineData("DELETE", "/api/v1/categories")]
    [InlineData("POST", "/api/v1/products/fx-telefon-a")]
    [InlineData("PUT", "/api/v1/products/fx-telefon-a")]
    [InlineData("PATCH", "/api/v1/products/fx-telefon-a")]
    [InlineData("DELETE", "/api/v1/products/fx-telefon-a")]
    public async Task Write_methods_are_not_allowed_and_return_problem_json(string method, string url)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = new StringContent("""{"name":"x"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        var response = await fixture.Development.SendAsync(request, TestContext.Current.CancellationToken);
        var json = await response.ReadJsonAsync();
        using var _ = json;

        CatalogJson.AssertProblem(response, json.RootElement, 405, "method_not_allowed");
        Assert.Contains("GET", response.Content.Headers.Allow);
    }

    [Fact]
    public async Task Write_attempt_does_not_change_the_data()
    {
        var before = await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM products", TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/products")
        {
            Content = new StringContent("""{"name":"Sızma Denemesi","slug":"sizma"}""", System.Text.Encoding.UTF8, "application/json"),
        };

        await fixture.Production.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(before, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM products", TestContext.Current.CancellationToken));
    }

    // ---------- Hata modeli (global) ----------

    [Fact]
    public async Task Unknown_route_has_code_not_found_and_trace_id()
    {
        var (response, json) = await fixture.Production.GetJsonAsync("/api/v1/bu-yol-yok");
        using var _ = json;

        CatalogJson.AssertProblem(response, json.RootElement, 404, "not_found");
    }

    [Fact]
    public async Task Trace_id_differs_between_requests()
    {
        var (_, first) = await fixture.Production.GetJsonAsync("/api/v1/products/fx-yok-boyle-urun");
        var (_, second) = await fixture.Production.GetJsonAsync("/api/v1/products/fx-yok-boyle-urun");
        using var _a = first;
        using var _b = second;

        Assert.NotEqual(first.RootElement.GetProperty("traceId").GetString(), second.RootElement.GetProperty("traceId").GetString());
    }

    // ---------- OpenAPI ----------

    [Fact]
    public async Task OpenApi_document_describes_the_three_read_paths_with_get_only()
    {
        var (response, json) = await fixture.Development.GetJsonAsync("/openapi/v1.json");
        using var _ = json;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paths = json.RootElement.GetProperty("paths");
        foreach (var path in new[] { "/api/v1/categories", "/api/v1/products", "/api/v1/products/{slug}" })
        {
            Assert.True(paths.TryGetProperty(path, out var item), $"{path} OpenAPI'de yok");
            Assert.Equal(["get"], item.EnumerateObject().Select(p => p.Name).ToArray()); // yazma yöntemi yok
        }

        Assert.Equal("GetCategories", paths.GetProperty("/api/v1/categories").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("ListProducts", paths.GetProperty("/api/v1/products").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("GetProduct", paths.GetProperty("/api/v1/products/{slug}").GetProperty("get").GetProperty("operationId").GetString());
    }

    [Fact]
    public async Task OpenApi_list_operation_declares_query_parameters_with_correct_types_and_responses()
    {
        var (_, json) = await fixture.Development.GetJsonAsync("/openapi/v1.json");
        using var _ = json;
        var operation = json.RootElement.GetProperty("paths").GetProperty("/api/v1/products").GetProperty("get");

        var parameters = operation.GetProperty("parameters").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);
        Assert.Equal(
            ["category", "maxPriceMinor", "minPriceMinor", "page", "pageSize", "q", "sort"],
            parameters.Keys.Order().ToArray());
        Assert.All(parameters.Values, p => Assert.Equal("query", p.GetProperty("in").GetString()));

        // Tipler sözleşmeyi yansıtmalı (parametreler string bağlansa da belge integer demeli).
        foreach (var name in new[] { "page", "pageSize", "minPriceMinor", "maxPriceMinor" })
        {
            Assert.Equal("integer", SchemaType(parameters[name]));
        }

        foreach (var name in new[] { "category", "q", "sort" })
        {
            Assert.Equal("string", SchemaType(parameters[name]));
        }

        var responses = operation.GetProperty("responses");
        Assert.True(Has(responses, "200"));
        Assert.True(Has(responses, "400"));
        Assert.True(Has(responses, "404"));
    }

    [Fact]
    public async Task OpenApi_detail_operation_declares_slug_path_parameter_and_404()
    {
        var (_, json) = await fixture.Development.GetJsonAsync("/openapi/v1.json");
        using var _ = json;
        var operation = json.RootElement.GetProperty("paths").GetProperty("/api/v1/products/{slug}").GetProperty("get");

        var slug = operation.GetProperty("parameters").EnumerateArray().Single();
        Assert.Equal("slug", slug.GetProperty("name").GetString());
        Assert.Equal("path", slug.GetProperty("in").GetString());
        Assert.True(slug.GetProperty("required").GetBoolean());
        Assert.True(Has(operation.GetProperty("responses"), "200"));
        Assert.True(Has(operation.GetProperty("responses"), "404"));
    }

    [Fact]
    public async Task OpenApi_component_schemas_match_the_response_contract_and_hide_admin_fields()
    {
        var (_, json) = await fixture.Development.GetJsonAsync("/openapi/v1.json");
        using var _ = json;
        var schemas = json.RootElement.GetProperty("components").GetProperty("schemas");

        Assert.Equal(
            ["id", "name", "parentSlug", "slug", "sortOrder"],
            PropertyNames(schemas, "CategoryDto"));
        Assert.Equal(
            ["category", "currency", "id", "inStock", "isDemo", "name", "primaryImage", "slug", "startingPriceMinor"],
            PropertyNames(schemas, "ProductListItemDto"));
        Assert.Equal(
            ["items", "page", "pageSize", "totalCount", "totalPages"],
            PropertyNames(schemas, "ProductPageDto"));
        Assert.Equal(
            ["brand", "category", "currency", "description", "id", "images", "inStock", "isDemo", "name", "slug", "specifications", "startingPriceMinor", "variants"],
            PropertyNames(schemas, "ProductDetailDto"));
        Assert.Equal(["availableQuantity", "currency", "displayName", "id", "inStock", "options", "priceMinor", "sku"], PropertyNames(schemas, "ProductVariantDto"));
        Assert.Equal(["altText", "isPrimary", "sortOrder", "url"], PropertyNames(schemas, "ProductImageDto"));

        // Kuruş alanları tam sayı (long => integer/int64).
        Assert.Equal("integer", SchemaTypeOf(schemas.GetProperty("ProductListItemDto").GetProperty("properties").GetProperty("startingPriceMinor")));
        Assert.Equal("integer", SchemaTypeOf(schemas.GetProperty("ProductVariantDto").GetProperty("properties").GetProperty("priceMinor")));

        // Hiçbir PUBLIC katalog şemasında yasak alan yok. (Admin stok şemaları - WarehouseDto, InventoryBalanceDto... - aynı belgede
        // bulunur ve depo/miktar/sürüm alanlarını bilerek taşır; kapsam dışıdır.) inStock/availableQuantity Faz 5 ile izinlidir (D-048).
        string[] publicSchemas =
        [
            "CategoryDto", "CategoryRefDto", "PrimaryImageDto", "ProductListItemDto", "ProductPageDto",
            "ProductDetailDto", "ProductVariantDto", "ProductImageDto",
        ];
        var forbidden = publicSchemas
            .SelectMany(name => CatalogJson.AllPropertyNames(schemas.GetProperty(name)))
            .Where(n => CatalogJson.ForbiddenKeys.Contains(n.ToLowerInvariant()))
            .ToArray();
        Assert.Empty(forbidden);
    }

    [Fact]
    public async Task OpenApi_is_not_served_in_Production()
    {
        var response = await fixture.Production.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static bool Has(JsonElement element, string name) => element.TryGetProperty(name, out _);

    private static string[] PropertyNames(JsonElement schemas, string schema) =>
        schemas.GetProperty(schema).GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray();

    private static string? SchemaType(JsonElement parameter) => SchemaTypeOf(parameter.GetProperty("schema"));

    private static string? SchemaTypeOf(JsonElement schema)
    {
        var type = schema.GetProperty("type");
        // Microsoft.OpenApi 2.x nullable tipi ["null","integer"] dizisi olarak yazabilir.
        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(t => t.GetString()).First(t => t != "null")
            : type.GetString();
    }
}

using SSCommerce.Api.Errors;
using SSCommerce.Application.Catalog;

namespace SSCommerce.Api.Catalog;

/// <summary>
/// Public katalog OKUMA uçları (F3.2, D-028). Yalnızca GET: yazma uçları bilinçli olarak yok (Faz 8). Kimlik doğrulama gerektirmez.
/// İstek akışı: HTTP → bu sınıf (bağlama + HTTP eşlemesi) → <see cref="ProductListQueryValidator"/> (saf doğrulama)
/// → <see cref="ICatalogQueries"/> (EF Core, Infrastructure) → PostgreSQL → DTO → JSON (camelCase).
/// </summary>
public static class CatalogEndpoints
{
    /// <param name="app">Genellikle <c>/api/v1</c> grubu (Program.cs): grup düzeyindeki güvenlik filtreleri buraya da uygulanır.</param>
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(string.Empty).WithTags("Catalog");

        api.MapGet("/categories", GetCategories)
            .WithName("GetCategories")
            .WithSummary("Aktif kategorileri listeler.")
            .Produces<IReadOnlyList<CategoryDto>>(StatusCodes.Status200OK);

        api.MapGet("/products", GetProducts)
            .WithName("ListProducts")
            .WithSummary("Ürünleri filtreler, sıralar ve sayfalar.")
            .WithDescription(
                "Yalnız aktif ürün + aktif kategori + en az bir aktif varyant. sort: newest (varsayılan), price_asc, price_desc, name_asc. " +
                "page 1-10000, pageSize 1-50 (varsayılan 20). Fiyatlar kuruştur ve başlangıç fiyatı (en düşük aktif varyant) üzerinden süzülür.")
            .Produces<ProductPageDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/products/{slug}", GetProduct)
            .WithName("GetProduct")
            .WithSummary("Slug ile ürün detayı (aktif varyantlar ve görseller).")
            .Produces<ProductDetailDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> GetCategories(ICatalogQueries catalog, CancellationToken cancellationToken) =>
        Results.Ok(await catalog.GetCategoriesAsync(cancellationToken));

    // Sorgu parametreleri bilinçli olarak string: "page=abc" gibi tip hataları minimal API'nin varsayılan bağlama 400'üne
    // (gövdesiz / Development'ta istisna) düşmez; doğrulayıcı alan bazlı `errors` sözlüğü üretir. OpenAPI tipleri
    // OpenApiConfiguration içinde düzeltilir.
    private static async Task<IResult> GetProducts(
        ICatalogQueries catalog,
        CancellationToken cancellationToken,
        string? category = null,
        string? q = null,
        string? minPriceMinor = null,
        string? maxPriceMinor = null,
        string? sort = null,
        string? page = null,
        string? pageSize = null)
    {
        var validation = ProductListQueryValidator.Validate(
            new ProductListRequest(category, q, minPriceMinor, maxPriceMinor, sort, page, pageSize));
        if (!validation.IsValid)
        {
            return ValidationProblem(validation.Errors);
        }

        var query = validation.Query!;

        // Biçimi bozuk kategori slug'ı hiçbir kategoriyle eşleşemez: veritabanına gitmeden 404 (SEO 404, 400 değil).
        if (query.CategorySlug is not null && !CatalogSlug.IsWellFormed(query.CategorySlug, CatalogSlug.CategoryMaxLength))
        {
            return CategoryNotFound();
        }

        var result = await catalog.GetProductsAsync(query, cancellationToken);
        return result is null ? CategoryNotFound() : Results.Ok(result);
    }

    private static async Task<IResult> GetProduct(string slug, ICatalogQueries catalog, CancellationToken cancellationToken)
    {
        // Karar: geçersiz slug biçimi 400 değil 404 product_not_found. URL bir kaynağı adresler; "Foo Bar" adlı kaynak yoktur.
        // Böylece SEO/önbellek tarafında tek tip davranış olur ve geçerli-olmayan girdi veritabanına inmez.
        if (!CatalogSlug.IsWellFormed(slug, CatalogSlug.ProductMaxLength))
        {
            return ProductNotFound();
        }

        var product = await catalog.GetProductBySlugAsync(slug, cancellationToken);
        return product is null ? ProductNotFound() : Results.Ok(product);
    }

    private static IResult ValidationProblem(IReadOnlyDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors.ToDictionary(kv => kv.Key, kv => kv.Value),
            title: "Geçersiz istek parametreleri",
            extensions: new Dictionary<string, object?> { [ApiProblemDetails.CodeKey] = ApiErrorCodes.ValidationFailed });

    private static IResult CategoryNotFound() =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Kategori bulunamadı",
            extensions: new Dictionary<string, object?> { [ApiProblemDetails.CodeKey] = ApiErrorCodes.CategoryNotFound });

    private static IResult ProductNotFound() =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Ürün bulunamadı",
            extensions: new Dictionary<string, object?> { [ApiProblemDetails.CodeKey] = ApiErrorCodes.ProductNotFound });
}

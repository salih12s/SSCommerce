using System.Security.Claims;
using SSCommerce.Api.Errors;
using SSCommerce.Api.Http;
using SSCommerce.Api.Security;
using SSCommerce.Application.Catalog;
using SSCommerce.Application.Inventory;

namespace SSCommerce.Api.Admin;

/// <summary>
/// Admin katalog yönetimi (Faz 8, docs/CONTRACT_PHASE_8.md "Catalog"): kategoriler, ürünler, görseller, varyantlar. <c>/api/v1/admin</c> grubuna bağlanır
/// (AdminOnly, Origin + CSRF, <c>no-store</c> otomatik). DELETE yoktur: pasifleştirilir. Slug ve SKU değişmezdir.
/// İstek akışı: HTTP → rate limit → auth (AdminOnly) → Origin + CSRF → bu sınıf (gövde okuma, HTTP eşlemesi) → <see cref="AdminCatalogValidator"/> (saf doğrulama)
/// → <see cref="IAdminCatalogService"/> (tek transaction, kilit + sürüm karşılaştırma, denetim satırı) → PostgreSQL.
/// Aktör yalnız principal'dendir; gövdedeki <c>id</c>/<c>isDemo</c>/<c>slug</c> (PATCH) gibi alanlar yok sayılır.
/// </summary>
public static class AdminCatalogEndpoints
{
    private const string Tag = "Admin Catalog";

    public static IEndpointRouteBuilder MapAdminCatalogEndpoints(this IEndpointRouteBuilder admin)
    {
        admin.MapGet("/categories", GetCategories)
            .WithTags(Tag)
            .WithName("AdminListCategories")
            .WithSummary("Pasifler dahil tüm kategoriler (sortOrder, name, id; en çok 500). Düz dizi.")
            .Produces<IReadOnlyList<AdminCategoryDto>>(StatusCodes.Status200OK);

        admin.MapPost("/categories", CreateCategory)
            .WithTags(Tag)
            .WithName("AdminCreateCategory")
            .WithSummary("Kategori oluşturur: { name, slug, parentId?, sortOrder?, isActive? }.")
            .WithDescription("Hatalar: 404 category_not_found (üst kategori yok); 409 category_slug_taken, category_too_deep; 503 busy.")
            .Accepts<NewCategoryBody>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<AdminCategoryDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        admin.MapPatch("/categories/{id}", UpdateCategory)
            .WithTags(Tag)
            .WithName("AdminUpdateCategory")
            .WithSummary("Kategori günceller: { version, name?, parentId?, sortOrder?, isActive? }. Slug değişmez; parentId null kökü yapar.")
            .WithDescription(
                "version ZORUNLU (eksik ya da < 1: 400). Kilitli satırdaki sürümle eşleşmezse 409 version_conflict (+currentVersion). " +
                "Hatalar: 404 category_not_found; 409 category_cycle, category_too_deep; 503 busy. Hiçbir şeyi değiştirmeyen istek sürümü artırmaz.")
            .Accepts<CategoryPatchBody>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<AdminCategoryDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        admin.MapGet("/products", GetProducts)
            .WithTags(Tag)
            .WithName("AdminListProducts")
            .WithSummary("Ürün özetleri (q, categoryId, status=active|inactive|all, page, pageSize ≤ 50); updatedAt azalan.")
            .Produces<PagedDto<AdminProductSummaryDto>>(StatusCodes.Status200OK)
            .ProducesValidationProblem();

        admin.MapGet("/products/{id}", GetProduct)
            .WithTags(Tag)
            .WithName("AdminGetProduct")
            .WithSummary("Ürün ayrıntısı: pasif varyantlar ve görseller dahil, her kaydın version değeriyle.")
            .Produces<AdminProductDetailDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        admin.MapPost("/products", CreateProduct)
            .WithTags(Tag)
            .WithName("AdminCreateProduct")
            .WithSummary("Ürün oluşturur (varsayılan pasif; isDemo her zaman false). Gövde en çok 64 KB.")
            .WithDescription("Hatalar: 404 category_not_found; 409 product_slug_taken; 503 busy.")
            .Accepts<NewProductBody>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<AdminProductDetailDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        admin.MapPatch("/products/{id}", UpdateProduct)
            .WithTags(Tag)
            .WithName("AdminUpdateProduct")
            .WithSummary("Ürün günceller: { version, categoryId?, name?, brand?, description?, specifications?, isActive? }. Slug değişmez. Gövde en çok 64 KB.")
            .WithDescription("Hatalar: 404 product_not_found, category_not_found; 409 version_conflict (+currentVersion); 503 busy.")
            .Accepts<ProductPatchBody>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<AdminProductDetailDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        admin.MapPut("/products/{id}/images", ReplaceImages)
            .WithTags(Tag)
            .WithName("AdminReplaceProductImages")
            .WithSummary("Ürün görsel kümesini değiştirir: { version, images: [{ storageKey, altText, isPrimary }] } (≤ 8, dizi sırası, doluysa tam bir ana görsel).")
            .WithDescription("storageKey onaylı yerel varlık listesinde olmalı (409 image_not_approved). Ürün sürümünü artırır.")
            .Accepts<ImagesPutBody>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<AdminProductDetailDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        admin.MapGet("/catalog/image-assets", GetImageAssets)
            .WithTags(Tag)
            .WithName("AdminListImageAssets")
            .WithSummary("Seçilebilir onaylı yerel görseller: [{ storageKey, creditKey, author }]. Yükleme ya da dış adres yoktur.")
            .Produces<IReadOnlyList<ImageAssetDto>>(StatusCodes.Status200OK);

        admin.MapPost("/products/{id}/variants", CreateVariant)
            .WithTags(Tag)
            .WithName("AdminCreateVariant")
            .WithSummary("Varyant ekler: { sku, displayName, priceMinor (1..2^53-1), options?, isActive? }.")
            .WithDescription("Hatalar: 404 product_not_found; 409 sku_taken, variant_options_taken; 503 busy.")
            .Accepts<NewVariantBody>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<AdminVariantDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        admin.MapPatch("/variants/{id}", UpdateVariant)
            .WithTags(Tag)
            .WithName("AdminUpdateVariant")
            .WithSummary("Varyant günceller: { version, displayName?, priceMinor?, isActive? }. SKU ve seçenekler değişmez.")
            .WithDescription(
                "Fiyat değişimi sipariş kalemlerine, sepet fiyat ipuçlarına ve stok hareketlerine dokunmaz; yeni önizlemeyi etkiler, eski quote 409 order_quote_changed olur.")
            .Accepts<VariantPatchBody>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<AdminVariantDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        admin.MapGet("/variants", SearchVariants)
            .WithTags(Tag)
            .WithName("AdminSearchVariants")
            .WithSummary("Stok ekranları için SKU/ürün araması (q, page, pageSize ≤ 50); sku sırası.")
            .Produces<PagedDto<VariantHitDto>>(StatusCodes.Status200OK)
            .ProducesValidationProblem();

        return admin;
    }

    // OpenAPI için gövde şekilleri (gerçek ayrıştırma JsonElement üzerinden; bu kayıtlar yalnız belgeleme içindir).
    public sealed record NewCategoryBody(string? Name, string? Slug, Guid? ParentId, int? SortOrder, bool? IsActive);

    public sealed record CategoryPatchBody(int? Version, string? Name, Guid? ParentId, int? SortOrder, bool? IsActive);

    public sealed record NewProductBody(Guid? CategoryId, string? Name, string? Slug, string? Brand, string? Description, Dictionary<string, string>? Specifications, bool? IsActive);

    public sealed record ProductPatchBody(int? Version, Guid? CategoryId, string? Name, string? Brand, string? Description, Dictionary<string, string>? Specifications, bool? IsActive);

    public sealed record ImagePutItem(string? StorageKey, string? AltText, bool? IsPrimary);

    public sealed record ImagesPutBody(int? Version, List<ImagePutItem>? Images);

    public sealed record NewVariantBody(string? Sku, string? DisplayName, long? PriceMinor, Dictionary<string, string>? Options, bool? IsActive);

    public sealed record VariantPatchBody(int? Version, string? DisplayName, long? PriceMinor, bool? IsActive);

    // ---------- Okuma ----------

    private static async Task<IResult> GetCategories(IAdminCatalogQueries queries, CancellationToken cancellationToken) =>
        Results.Ok(await queries.GetCategoriesAsync(cancellationToken));

    private static async Task<IResult> GetProducts(
        IAdminCatalogQueries queries,
        CancellationToken cancellationToken,
        string? q = null,
        string? categoryId = null,
        string? status = null,
        string? page = null,
        string? pageSize = null)
    {
        var validation = AdminCatalogValidator.ValidateProductList(q, categoryId, status, page, pageSize);
        return validation.IsValid
            ? Results.Ok(await queries.GetProductsAsync(validation.Value!, cancellationToken))
            : ApiResults.ValidationFailed(validation.Errors);
    }

    private static async Task<IResult> GetProduct(string id, IAdminCatalogQueries queries, CancellationToken cancellationToken)
    {
        var (productId, errors) = AdminCatalogValidator.ValidateId(id);
        if (productId is not { } valid)
        {
            return ApiResults.ValidationFailed(errors!);
        }

        var product = await queries.GetProductAsync(valid, cancellationToken);
        return product is null ? NotFound(CatalogWriteStatus.ProductNotFound) : Results.Ok(product);
    }

    private static IResult GetImageAssets() =>
        Results.Ok(ApprovedProductImages.All.Select(a => new ImageAssetDto(a.StorageKey, a.CreditKey, a.Author)).ToList());

    private static async Task<IResult> SearchVariants(
        IAdminCatalogQueries queries,
        CancellationToken cancellationToken,
        string? q = null,
        string? page = null,
        string? pageSize = null)
    {
        var validation = AdminCatalogValidator.ValidateVariantSearch(q, page, pageSize);
        return validation.IsValid
            ? Results.Ok(await queries.SearchVariantsAsync(validation.Value!, cancellationToken))
            : ApiResults.ValidationFailed(validation.Errors);
    }

    // ---------- Yazma ----------

    private static async Task<IResult> CreateCategory(HttpContext http, ClaimsPrincipal user, IAdminCatalogService service, CancellationToken cancellationToken)
    {
        var (body, bodyError) = await JsonRequestBody.ReadElementAsync(http.Request, JsonRequestBody.DefaultMaxBytes, cancellationToken);
        if (bodyError is not null)
        {
            return ApiResults.ValidationFailed(bodyError);
        }

        var validation = AdminCatalogValidator.ValidateNewCategory(body);
        return validation.IsValid
            ? ToResult(await service.CreateCategoryAsync(user.GetUserId(), validation.Value!, cancellationToken), StatusCodes.Status201Created)
            : ApiResults.ValidationFailed(validation.Errors);
    }

    private static async Task<IResult> UpdateCategory(string id, HttpContext http, ClaimsPrincipal user, IAdminCatalogService service, CancellationToken cancellationToken)
    {
        var (categoryId, idErrors) = AdminCatalogValidator.ValidateId(id);
        if (categoryId is not { } valid)
        {
            return ApiResults.ValidationFailed(idErrors!);
        }

        var (body, bodyError) = await JsonRequestBody.ReadElementAsync(http.Request, JsonRequestBody.DefaultMaxBytes, cancellationToken);
        if (bodyError is not null)
        {
            return ApiResults.ValidationFailed(bodyError);
        }

        var validation = AdminCatalogValidator.ValidateCategoryChange(body);
        return validation.IsValid
            ? ToResult(await service.UpdateCategoryAsync(user.GetUserId(), valid, validation.Value!, cancellationToken), StatusCodes.Status200OK)
            : ApiResults.ValidationFailed(validation.Errors);
    }

    private static async Task<IResult> CreateProduct(HttpContext http, ClaimsPrincipal user, IAdminCatalogService service, CancellationToken cancellationToken)
    {
        var (body, bodyError) = await JsonRequestBody.ReadElementAsync(http.Request, JsonRequestBody.ProductMaxBytes, cancellationToken);
        if (bodyError is not null)
        {
            return ApiResults.ValidationFailed(bodyError);
        }

        var validation = AdminCatalogValidator.ValidateNewProduct(body);
        return validation.IsValid
            ? ToResult(await service.CreateProductAsync(user.GetUserId(), validation.Value!, cancellationToken), StatusCodes.Status201Created)
            : ApiResults.ValidationFailed(validation.Errors);
    }

    private static async Task<IResult> UpdateProduct(string id, HttpContext http, ClaimsPrincipal user, IAdminCatalogService service, CancellationToken cancellationToken)
    {
        var (productId, idErrors) = AdminCatalogValidator.ValidateId(id);
        if (productId is not { } valid)
        {
            return ApiResults.ValidationFailed(idErrors!);
        }

        var (body, bodyError) = await JsonRequestBody.ReadElementAsync(http.Request, JsonRequestBody.ProductMaxBytes, cancellationToken);
        if (bodyError is not null)
        {
            return ApiResults.ValidationFailed(bodyError);
        }

        var validation = AdminCatalogValidator.ValidateProductChange(body);
        return validation.IsValid
            ? ToResult(await service.UpdateProductAsync(user.GetUserId(), valid, validation.Value!, cancellationToken), StatusCodes.Status200OK)
            : ApiResults.ValidationFailed(validation.Errors);
    }

    private static async Task<IResult> ReplaceImages(string id, HttpContext http, ClaimsPrincipal user, IAdminCatalogService service, CancellationToken cancellationToken)
    {
        var (productId, idErrors) = AdminCatalogValidator.ValidateId(id);
        if (productId is not { } valid)
        {
            return ApiResults.ValidationFailed(idErrors!);
        }

        var (body, bodyError) = await JsonRequestBody.ReadElementAsync(http.Request, JsonRequestBody.DefaultMaxBytes, cancellationToken);
        if (bodyError is not null)
        {
            return ApiResults.ValidationFailed(bodyError);
        }

        var validation = AdminCatalogValidator.ValidateImageReplacement(body);
        return validation.IsValid
            ? ToResult(await service.ReplaceImagesAsync(user.GetUserId(), valid, validation.Value!, cancellationToken), StatusCodes.Status200OK)
            : ApiResults.ValidationFailed(validation.Errors);
    }

    private static async Task<IResult> CreateVariant(string id, HttpContext http, ClaimsPrincipal user, IAdminCatalogService service, CancellationToken cancellationToken)
    {
        var (productId, idErrors) = AdminCatalogValidator.ValidateId(id);
        if (productId is not { } valid)
        {
            return ApiResults.ValidationFailed(idErrors!);
        }

        var (body, bodyError) = await JsonRequestBody.ReadElementAsync(http.Request, JsonRequestBody.DefaultMaxBytes, cancellationToken);
        if (bodyError is not null)
        {
            return ApiResults.ValidationFailed(bodyError);
        }

        var validation = AdminCatalogValidator.ValidateNewVariant(body);
        return validation.IsValid
            ? ToResult(await service.CreateVariantAsync(user.GetUserId(), valid, validation.Value!, cancellationToken), StatusCodes.Status201Created)
            : ApiResults.ValidationFailed(validation.Errors);
    }

    private static async Task<IResult> UpdateVariant(string id, HttpContext http, ClaimsPrincipal user, IAdminCatalogService service, CancellationToken cancellationToken)
    {
        var (variantId, idErrors) = AdminCatalogValidator.ValidateId(id);
        if (variantId is not { } valid)
        {
            return ApiResults.ValidationFailed(idErrors!);
        }

        var (body, bodyError) = await JsonRequestBody.ReadElementAsync(http.Request, JsonRequestBody.DefaultMaxBytes, cancellationToken);
        if (bodyError is not null)
        {
            return ApiResults.ValidationFailed(bodyError);
        }

        var validation = AdminCatalogValidator.ValidateVariantChange(body);
        return validation.IsValid
            ? ToResult(await service.UpdateVariantAsync(user.GetUserId(), valid, validation.Value!, cancellationToken), StatusCodes.Status200OK)
            : ApiResults.ValidationFailed(validation.Errors);
    }

    // ---------- Eşleme ----------

    private static IResult ToResult<T>(CatalogWriteResult<T> result, int successStatus) where T : class => result.Status switch
    {
        CatalogWriteStatus.Ok => successStatus == StatusCodes.Status200OK ? Results.Ok(result.Value) : Results.Json(result.Value, statusCode: successStatus),
        CatalogWriteStatus.VersionConflict => ApiResults.Problem(
            StatusCodes.Status409Conflict,
            "Kayıt başka biri tarafından değiştirilmiş; sayfayı yenileyip tekrar deneyin",
            ApiErrorCodes.VersionConflict,
            new Dictionary<string, object?> { ["currentVersion"] = result.CurrentVersion }),
        CatalogWriteStatus.CategorySlugTaken => Conflict("Bu kategori slug değeri kullanılıyor", ApiErrorCodes.CategorySlugTaken),
        CatalogWriteStatus.ProductSlugTaken => Conflict("Bu ürün slug değeri kullanılıyor", ApiErrorCodes.ProductSlugTaken),
        CatalogWriteStatus.SkuTaken => Conflict("Bu SKU kullanılıyor", ApiErrorCodes.SkuTaken),
        CatalogWriteStatus.VariantOptionsTaken => Conflict("Bu üründe aynı seçeneklerle başka bir varyant var", ApiErrorCodes.VariantOptionsTaken),
        CatalogWriteStatus.CategoryCycle => Conflict("Kategori kendi alt dalına bağlanamaz (döngü)", ApiErrorCodes.CategoryCycle),
        CatalogWriteStatus.CategoryTooDeep => Conflict("Kategori ağacı en çok 5 düzey olabilir", ApiErrorCodes.CategoryTooDeep),
        CatalogWriteStatus.ImageNotApproved => Conflict("Görsel onaylı yerel görseller arasında değil", ApiErrorCodes.ImageNotApproved),
        CatalogWriteStatus.Busy => ApiResults.Problem(
            StatusCodes.Status503ServiceUnavailable, "Sistem meşgul, lütfen tekrar deneyin", ApiErrorCodes.Busy),
        _ => NotFound(result.Status),
    };

    private static IResult Conflict(string title, string code) => ApiResults.Problem(StatusCodes.Status409Conflict, title, code);

    private static IResult NotFound(CatalogWriteStatus status) => status switch
    {
        CatalogWriteStatus.CategoryNotFound => ApiResults.Problem(StatusCodes.Status404NotFound, "Kategori bulunamadı", ApiErrorCodes.CategoryNotFound),
        CatalogWriteStatus.ProductNotFound => ApiResults.Problem(StatusCodes.Status404NotFound, "Ürün bulunamadı", ApiErrorCodes.ProductNotFound),
        CatalogWriteStatus.VariantNotFound => ApiResults.Problem(StatusCodes.Status404NotFound, "Varyant bulunamadı", ApiErrorCodes.VariantNotFound),
        _ => throw new InvalidOperationException("Bilinmeyen katalog yazma durumu."),
    };
}

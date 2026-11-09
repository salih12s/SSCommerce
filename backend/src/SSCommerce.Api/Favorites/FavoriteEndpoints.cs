using System.Security.Claims;
using SSCommerce.Api.Errors;
using SSCommerce.Api.Security;
using SSCommerce.Application.Favorites;

namespace SSCommerce.Api.Favorites;

/// <summary>
/// Favori uçları (MD-1, D-088; docs/PLAN_CUSTOMER_EXPERIENCE.md). <c>/api/v1/favorites</c> grubu <c>RequireAuthorization()</c> ister; kullanıcı kimliği
/// YALNIZ cookie'den (<c>ClaimsPrincipal.GetUserId()</c>) gelir, URL ve gövdede yoktur: başkasının favorisi hiçbir uçtan görünmez. Origin + CSRF
/// <c>MapApiV1</c> grubundan gelir; yazmalar <c>mutation</c> hız sınırındadır; yanıtlar <c>no-store</c>'dur (kişisel veri).
/// PUT ve DELETE idempotenttir: sonuç aynı olur, tekrar çağrı hata vermez.
/// </summary>
public static class FavoriteEndpoints
{
    public static IEndpointRouteBuilder MapFavoriteEndpoints(this IEndpointRouteBuilder api)
    {
        var favorites = api.MapGroup("/favorites").WithTags("Favorites").RequireAuthorization();

        favorites.MapGet(string.Empty, List)
            .WithName("ListFavorites")
            .WithSummary("Kullanıcının favorileri, eklenme anına göre azalan (en çok 100).")
            .WithDescription(
                "Öğeler ürün liste öğesiyle aynı alanlar/birimlerdir (startingPriceMinor kuruş) + addedAt (UTC) + available. " +
                "Satıştan kalkan ürün listede kalır: available:false, inStock:false.")
            .Produces<FavoriteListDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        favorites.MapGet("/ids", ListIds)
            .WithName("ListFavoriteIds")
            .WithSummary("Favori ürün kimlikleri (kalp durumu ve sayaç için hafif uç).")
            .Produces<FavoriteIdsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        favorites.MapPut("/{productId}", Add)
            .WithName("AddFavorite")
            .WithSummary("Ürünü favoriye ekler (idempotent: zaten favoriyse yine 204).")
            .WithDescription("Hatalar: 404 product_not_found (yok/pasif/kategori pasif/geçersiz kimlik); 409 favorite_limit (100'e ulaşmışken YENİ ürün); 503 busy.")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        favorites.MapDelete("/{productId}", Remove)
            .WithName("RemoveFavorite")
            .WithSummary("Ürünü favoriden çıkarır (idempotent: favoride değilse de 204).")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return api;
    }

    private static async Task<IResult> List(ClaimsPrincipal user, IFavoriteService favorites, CancellationToken cancellationToken) =>
        Results.Ok(await favorites.ListAsync(user.GetUserId(), cancellationToken));

    private static async Task<IResult> ListIds(ClaimsPrincipal user, IFavoriteService favorites, CancellationToken cancellationToken) =>
        Results.Ok(await favorites.ListIdsAsync(user.GetUserId(), cancellationToken));

    private static async Task<IResult> Add(string productId, ClaimsPrincipal user, IFavoriteService favorites, CancellationToken cancellationToken)
    {
        // Geçersiz kimlik biçimi 400 değil 404: URL bir kaynağı adresler, "abc" adlı ürün yoktur (katalogdaki slug kararıyla aynı).
        // Yalnız kanonik "D" biçimi (tireli, parantezsiz); {..}, (..) ve tiresiz biçimler aynı kaynağı farklı URL'lerle adreslemesin.
        if (!Guid.TryParseExact(productId, "D", out var id))
        {
            return ProductNotFound();
        }

        return ToResult(await favorites.AddAsync(user.GetUserId(), id, cancellationToken));
    }

    private static async Task<IResult> Remove(string productId, ClaimsPrincipal user, IFavoriteService favorites, CancellationToken cancellationToken)
    {
        // Geçersiz biçim zaten favoride olamaz: DELETE idempotent olduğundan yine 204 (veritabanına inilmez).
        if (!Guid.TryParseExact(productId, "D", out var id))
        {
            return Results.NoContent();
        }

        return ToResult(await favorites.RemoveAsync(user.GetUserId(), id, cancellationToken));
    }

    private static IResult ToResult(FavoriteWriteStatus status) => status switch
    {
        FavoriteWriteStatus.Ok => Results.NoContent(),
        FavoriteWriteStatus.ProductNotFound => ProductNotFound(),
        FavoriteWriteStatus.LimitReached => ApiResults.Problem(
            StatusCodes.Status409Conflict, "En çok 100 ürün favoriye eklenebilir", ApiErrorCodes.FavoriteLimit),
        FavoriteWriteStatus.Busy => ApiResults.Problem(
            StatusCodes.Status503ServiceUnavailable, "Sistem meşgul, lütfen tekrar deneyin", ApiErrorCodes.Busy),
        _ => throw new InvalidOperationException("Bilinmeyen favori işlem durumu."),
    };

    private static IResult ProductNotFound() =>
        ApiResults.Problem(StatusCodes.Status404NotFound, "Ürün bulunamadı", ApiErrorCodes.ProductNotFound);
}

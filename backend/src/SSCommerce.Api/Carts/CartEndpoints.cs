using System.Security.Claims;
using SSCommerce.Api.Errors;
using SSCommerce.Api.Http;
using SSCommerce.Api.Security;
using SSCommerce.Application.Carts;

namespace SSCommerce.Api.Carts;

/// <summary>
/// Sepet uçları (F6.1/F6.2, D-049..D-051). <c>/api/v1/cart</c> grubu <c>RequireAuthorization()</c> ister (Customer/Warehouse/Admin kendi sepetini kullanır);
/// Origin + CSRF <c>MapApiV1</c> grubundan gelir; yazmalar <c>mutation</c> rate limit politikasındadır; yanıtlar <c>no-store</c>'dur.
/// Kullanıcı kimliği YALNIZ principal'dendir (<see cref="ClaimsPrincipalExtensions.GetUserId"/>): gövde/rotadaki <c>userId</c> yok sayılır.
/// Tutar sunucuda güncel katalog fiyatından hesaplanır; gövdedeki <c>priceMinor</c>/<c>subtotalMinor</c>/<c>discount</c>/<c>unitPrice</c> yok sayılır.
/// </summary>
public static class CartEndpoints
{
    public static IEndpointRouteBuilder MapCartEndpoints(this IEndpointRouteBuilder api)
    {
        var cart = api.MapGroup("/cart").WithTags("Cart").RequireAuthorization();

        cart.MapGet(string.Empty, GetCart)
            .WithName("GetCart")
            .WithSummary("Oturumdaki kullanıcının sepeti. Sepet yoksa boş sepet döner ve hiçbir şey oluşturulmaz.")
            .Produces<CartDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict);

        cart.MapPost("/items", AddItem)
            .WithName("AddCartItem")
            .WithSummary("Sepete varyant ekler; varyant zaten varsa adedi ARTIRIR. Stok ayrılmaz.")
            .WithDescription(
                "Hatalar: 404 variant_not_found; 409 variant_inactive (varyant/ürün/kategori pasif), insufficient_stock (availableQuantity ile), " +
                "cart_quantity_limit (satır adedi > 20), cart_line_limit (50 farklı satır), cart_total_too_large; 503 busy.")
            .Accepts<AddCartItemRequest>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<CartDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        cart.MapPatch("/items/{itemId}", UpdateItem)
            .WithName("UpdateCartItem")
            .WithSummary("Satır adedini MUTLAK değere ayarlar (1..20). 0 ve negatif 400; silmek için DELETE. Yalnız artış stok/aktiflik denetler.")
            .Accepts<UpdateCartItemRequest>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<CartDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        cart.MapDelete("/items/{itemId}", RemoveItem)
            .WithName("RemoveCartItem")
            .WithSummary("Satırı siler ve güncel sepeti döner. Başkasının ya da olmayan satır 404 cart_item_not_found.")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<CartDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return api;
    }

    private static async Task<IResult> GetCart(ClaimsPrincipal user, ICartService carts, CancellationToken cancellationToken) =>
        ToResult(await carts.GetAsync(user.GetUserId(), cancellationToken));

    private static async Task<IResult> AddItem(HttpContext http, ClaimsPrincipal user, ICartService carts, CancellationToken cancellationToken)
    {
        var (body, bodyError) = await JsonRequestBody.ReadAsync<AddCartItemRequest>(http.Request, JsonRequestBody.DefaultMaxBytes, cancellationToken);
        if (body is null)
        {
            return ApiResults.ValidationFailed(bodyError!);
        }

        var validation = CartRequestValidator.ValidateAdd(body);
        return validation.IsValid
            ? ToResult(await carts.AddItemAsync(user.GetUserId(), validation.Value!, cancellationToken))
            : ApiResults.ValidationFailed(validation.Errors);
    }

    private static async Task<IResult> UpdateItem(
        string itemId,
        HttpContext http,
        ClaimsPrincipal user,
        ICartService carts,
        CancellationToken cancellationToken)
    {
        var (body, bodyError) = await JsonRequestBody.ReadAsync<UpdateCartItemRequest>(http.Request, JsonRequestBody.DefaultMaxBytes, cancellationToken);
        if (body is null)
        {
            return ApiResults.ValidationFailed(bodyError!);
        }

        var validation = CartRequestValidator.ValidateQuantityOnly(body);
        if (!validation.IsValid)
        {
            return ApiResults.ValidationFailed(validation.Errors);
        }

        // Bozuk kimlik hiçbir satırla eşleşemez: veritabanına gitmeden 404 (olmayan satırla aynı yanıt).
        return Guid.TryParse(itemId, out var id)
            ? ToResult(await carts.SetQuantityAsync(user.GetUserId(), id, validation.Value!.Quantity, cancellationToken))
            : ItemNotFound();
    }

    private static async Task<IResult> RemoveItem(string itemId, ClaimsPrincipal user, ICartService carts, CancellationToken cancellationToken) =>
        Guid.TryParse(itemId, out var id)
            ? ToResult(await carts.RemoveItemAsync(user.GetUserId(), id, cancellationToken))
            : ItemNotFound();

    private static IResult ToResult(CartOperationResult result) => result.Status switch
    {
        CartOperationStatus.Ok => Results.Ok(result.Cart),
        CartOperationStatus.VariantNotFound => ApiResults.Problem(
            StatusCodes.Status404NotFound, "Varyant bulunamadı", ApiErrorCodes.VariantNotFound),
        CartOperationStatus.VariantInactive => ApiResults.Problem(
            StatusCodes.Status409Conflict, "Ürün şu an satışta değil", ApiErrorCodes.VariantInactive),
        CartOperationStatus.InsufficientStock => ApiResults.Problem(
            StatusCodes.Status409Conflict,
            "Yeterli stok yok",
            ApiErrorCodes.InsufficientStock,
            new Dictionary<string, object?> { ["availableQuantity"] = result.AvailableQuantity ?? 0 }),
        CartOperationStatus.QuantityLimit => ApiResults.Problem(
            StatusCodes.Status409Conflict, "Bir satırda en çok 20 adet olabilir", ApiErrorCodes.CartQuantityLimit),
        CartOperationStatus.LineLimit => ApiResults.Problem(
            StatusCodes.Status409Conflict, "Sepette en çok 50 farklı ürün olabilir", ApiErrorCodes.CartLineLimit),
        CartOperationStatus.ItemNotFound => ItemNotFound(),
        CartOperationStatus.TotalTooLarge => ApiResults.Problem(
            StatusCodes.Status409Conflict, "Sepet toplamı izin verilen üst sınırı aşıyor", ApiErrorCodes.CartTotalTooLarge),
        CartOperationStatus.Busy => ApiResults.Problem(
            StatusCodes.Status503ServiceUnavailable, "Sistem meşgul, lütfen tekrar deneyin", ApiErrorCodes.Busy),
        _ => throw new InvalidOperationException("Bilinmeyen sepet işlem durumu."),
    };

    private static IResult ItemNotFound() =>
        ApiResults.Problem(StatusCodes.Status404NotFound, "Sepet satırı bulunamadı", ApiErrorCodes.CartItemNotFound);
}

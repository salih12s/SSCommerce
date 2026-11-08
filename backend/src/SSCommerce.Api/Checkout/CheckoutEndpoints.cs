using System.Security.Claims;
using Microsoft.Extensions.Options;
using SSCommerce.Api.Errors;
using SSCommerce.Api.Http;
using SSCommerce.Api.Security;
using SSCommerce.Application.Checkout;
using SSCommerce.Application.Orders;

namespace SSCommerce.Api.Checkout;

/// <summary>
/// Checkout preview (F6.4, D-054; Faz 7: quote, D-060). SALT OKUNUR: sipariş, ödeme, rezervasyon YARATMAZ; stok ve sepet değişmez.
/// Sipariş <c>POST /api/v1/orders</c> ile verilir. POST olmasının nedeni
/// gövdede adres kimliği taşımak ve adres kimliğini URL'ye/loglara koymamaktır. Auth + CSRF + <c>mutation</c> rate limit uygulanır.
/// </summary>
public static class CheckoutEndpoints
{
    public static IEndpointRouteBuilder MapCheckoutEndpoints(this IEndpointRouteBuilder api)
    {
        var checkout = api.MapGroup("/checkout").WithTags("Checkout").RequireAuthorization();

        checkout.MapPost("/preview", Preview)
            .WithName("CheckoutPreview")
            .WithSummary("Sepet + adres için sipariş öncesi özet. Kargo hesaplanmadığı için shippingMinor ve grandTotalMinor null; orderingAvailable true; canProceed ise quote döner.")
            .WithDescription(
                "Satır sorunları hata değildir: 200 + canProceed:false (quote null). canProceed:true iken quote, POST /orders gövdesinde aynen geri gönderilen fiyat onayıdır; " +
                "sipariş fiyatı ASLA ondan gelmez, sunucu kilit altında yeniden hesaplar ve farkta 409 order_quote_changed verir. " +
                "Hatalar: 409 cart_empty, 409 cart_total_too_large, 404 address_not_found (yok ya da başkasının).")
            .Accepts<CheckoutPreviewRequest>("application/json")
            .RequireRateLimiting(RateLimitPolicies.Mutation)
            .Produces<CheckoutPreviewDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return api;
    }

    private static async Task<IResult> Preview(
        HttpContext http,
        ClaimsPrincipal user,
        ICheckoutPreviewService preview,
        IOptionsMonitor<OrderLimitOptions> ordering,
        CancellationToken cancellationToken)
    {
        var (body, bodyError) = await JsonRequestBody.ReadAsync<CheckoutPreviewRequest>(http.Request, JsonRequestBody.DefaultMaxBytes, cancellationToken);
        if (body is null)
        {
            return ApiResults.ValidationFailed(bodyError!);
        }

        if (body.AddressId is not { } addressId || addressId == Guid.Empty)
        {
            return ApiResults.ValidationFailed(new Dictionary<string, string[]> { ["addressId"] = ["addressId zorunludur."] });
        }

        var result = await preview.PreviewAsync(user.GetUserId(), addressId, cancellationToken);
        return result.Status switch
        {
            // orderingAvailable sipariş anahtarını (Ordering:Enabled) yansıtır; şema aynıdır.
            CheckoutPreviewStatus.Ok => Results.Ok(result.Preview! with { OrderingAvailable = ordering.CurrentValue.Enabled }),
            CheckoutPreviewStatus.CartEmpty => ApiResults.Problem(StatusCodes.Status409Conflict, "Sepet boş", ApiErrorCodes.CartEmpty),
            CheckoutPreviewStatus.AddressNotFound => ApiResults.Problem(StatusCodes.Status404NotFound, "Adres bulunamadı", ApiErrorCodes.AddressNotFound),
            CheckoutPreviewStatus.TotalTooLarge => ApiResults.Problem(
                StatusCodes.Status409Conflict, "Sepet toplamı izin verilen üst sınırı aşıyor", ApiErrorCodes.CartTotalTooLarge),
            _ => throw new InvalidOperationException("Bilinmeyen preview durumu."),
        };
    }
}

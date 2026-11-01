using SSCommerce.Application.Addresses;
using SSCommerce.Application.Carts;

namespace SSCommerce.Application.Checkout;

// Checkout PREVIEW sözleşmesi (docs/CONTRACT_PHASES_5_6.md, D-054). Sipariş, ödeme, rezervasyon YARATMAZ; stok ve sepet değişmez.
// Kargo/vergi yapılandırılmadığı için ödenecek kesin tutar UYDURULMAZ: shippingMinor ve grandTotalMinor null.

public static class ShippingStatuses
{
    public const string NotCalculated = "shippingNotCalculated";
}

/// <param name="ItemId">Sorun belirli bir satırla ilgiliyse o satırın kimliği.</param>
public sealed record CheckoutIssueDto(string Code, Guid? ItemId);

public sealed record CheckoutPreviewDto(
    IReadOnlyList<CartItemDto> Lines,
    AddressDto Address,
    int ItemCount,
    long SubtotalMinor,
    long? ShippingMinor,
    string ShippingStatus,
    long? GrandTotalMinor,
    string Currency,
    bool CanProceed,
    IReadOnlyList<CheckoutIssueDto> Issues,
    bool OrderingAvailable,
    DateTimeOffset CalculatedAt);

public sealed record CheckoutPreviewRequest(Guid? AddressId);

public enum CheckoutPreviewStatus
{
    Ok,
    CartEmpty,
    AddressNotFound,
    TotalTooLarge,
}

public sealed record CheckoutPreviewResult(CheckoutPreviewStatus Status, CheckoutPreviewDto? Preview = null);

/// <summary>Salt okunur: yalnız AsNoTracking okumaları, SaveChanges ve yazan transaction YOKTUR.</summary>
public interface ICheckoutPreviewService
{
    Task<CheckoutPreviewResult> PreviewAsync(Guid userId, Guid addressId, CancellationToken cancellationToken);
}

using SSCommerce.Application.Addresses;
using SSCommerce.Application.Carts;

namespace SSCommerce.Application.Checkout;

// Checkout PREVIEW sözleşmesi (docs/CONTRACT_PHASES_5_6.md, D-054; Faz 7: docs/CONTRACT_PHASE_7.md "Quote"). Sipariş, ödeme, rezervasyon YARATMAZ;
// stok ve sepet değişmez. Sipariş POST /orders ile verilir; önizleme yalnız onay için quote üretir.
// Kargo/vergi yapılandırılmadığı için ödenecek kesin tutar UYDURULMAZ: shippingMinor ve grandTotalMinor null.

public static class ShippingStatuses
{
    public const string NotCalculated = "shippingNotCalculated";
}

/// <param name="ItemId">Sorun belirli bir satırla ilgiliyse o satırın kimliği.</param>
public sealed record CheckoutIssueDto(string Code, Guid? ItemId);

/// <param name="Quote">
/// Fiyat onayı (Faz 7, D-060): sunucunun bu sepet için hesapladığı özet; <c>POST /orders</c> gövdesinde aynen geri gönderilir. Yalnız
/// <see cref="CanProceed"/> true iken dolu, aksi halde null. Önizleme salt okunur kalır: quote kaydedilmez, saf bir hesaptır.
/// </param>
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
    string? Quote,
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

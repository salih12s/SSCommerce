namespace SSCommerce.Application.Carts;

// Sepet yanıt sözleşmesi (docs/CONTRACT_PHASES_5_6.md "Faz 6 API", D-049..D-051). Para kuruştur (long, "...Minor").
// Fiyatlar HER okumada güncel katalog fiyatından hesaplanır; tarayıcıdan gelen hiçbir tutar kullanılmaz.

/// <summary>Satır durumları (<see cref="CartItemDto.Status"/>) ve preview <c>issues[].code</c> değerleri.</summary>
public static class CartLineStatus
{
    public const string Available = "available";

    /// <summary>Stok > 0 ama sepetteki adet stoktan fazla.</summary>
    public const string InsufficientStock = "insufficient_stock";

    public const string OutOfStock = "out_of_stock";

    /// <summary>Varyant, ürün ya da kategori pasif. Fiyat alanları null, ara toplama girmez.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>Yalnız preview <c>issues[]</c> içinde: satırın fiyatı eklendiğinden beri değişti (bilgilendirme, engellemez).</summary>
    public const string PriceChanged = "price_changed";
}

/// <param name="AvailableQuantity">Aktif depolardaki toplam stok, en çok 20 (D-048). <c>unavailable</c> satırda 0.</param>
/// <param name="PreviousUnitPriceMinor">Yalnız fiyat eklendiğinden/adet değiştiğinden beri farklıysa dolu.</param>
public sealed record CartItemDto(
    Guid Id,
    Guid VariantId,
    string Sku,
    string ProductSlug,
    string ProductName,
    string VariantName,
    string? ImageUrl,
    string? ImageAlt,
    int Quantity,
    long? UnitPriceMinor,
    long? LineSubtotalMinor,
    long? PreviousUnitPriceMinor,
    bool PriceChanged,
    int AvailableQuantity,
    string Status);

/// <param name="ItemCount">Satır adetlerinin toplamı (pasif satırlar dahil).</param>
/// <param name="SubtotalMinor"><c>unavailable</c> olmayan satırların güncel fiyatla toplamı.</param>
/// <param name="HasIssues">Herhangi bir satırın durumu <c>available</c> değilse true.</param>
public sealed record CartDto(IReadOnlyList<CartItemDto> Items, int ItemCount, long SubtotalMinor, string Currency, bool HasIssues);

/// <summary>Veritabanından okunan ham satır; hesap <see cref="CartCalculator"/>'da (saf).</summary>
/// <param name="HintUnitPrice"><c>unit_price_at_add</c>: yalnız "fiyat değişti" ipucu, tutara girmez.</param>
/// <param name="IsPurchasable">Varyant AND ürün AND kategori aktif.</param>
/// <param name="AvailableOnHand">Aktif depoların toplamı, CAP'SİZ ham değer (cap hesapta uygulanır).</param>
public sealed record CartLineData(
    Guid ItemId,
    Guid VariantId,
    string Sku,
    string ProductSlug,
    string ProductName,
    string VariantName,
    string? ImageUrl,
    string? ImageAlt,
    int Quantity,
    decimal HintUnitPrice,
    decimal CurrentUnitPrice,
    bool IsPurchasable,
    long AvailableOnHand);

// HAM istek gövdeleri (bilinmeyen alanlar - userId, priceMinor, subtotalMinor, discount, unitPrice - kayıtta yoktur, yok sayılır).
public sealed record AddCartItemRequest(Guid? VariantId, long? Quantity);

public sealed record UpdateCartItemRequest(long? Quantity);

public sealed record AddCartItemCommand(Guid VariantId, int Quantity);

public enum CartOperationStatus
{
    Ok,
    VariantNotFound,
    VariantInactive,
    InsufficientStock,
    QuantityLimit,
    LineLimit,
    ItemNotFound,
    TotalTooLarge,

    /// <summary>Kilit zaman aşımı: hiçbir şey yazılmadı (503).</summary>
    Busy,
}

/// <param name="AvailableQuantity">Yalnız <see cref="CartOperationStatus.InsufficientStock"/> durumunda dolu (cap'li).</param>
public sealed record CartOperationResult(CartOperationStatus Status, CartDto? Cart, int? AvailableQuantity = null);

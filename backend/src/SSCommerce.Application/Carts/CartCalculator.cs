using SSCommerce.Application.Catalog;
using SSCommerce.Domain.Catalog;

namespace SSCommerce.Application.Carts;

/// <summary>Hesap sonucu: <see cref="TooLarge"/> true ise (ara toplam <see cref="Money.MaxAmount"/>'ı aşıyor) <see cref="Cart"/> null'dır.</summary>
public sealed record CartCalculation(CartDto? Cart, bool TooLarge);

/// <summary>
/// Sepet hesabı: SAF fonksiyon (veritabanı yok). Fiyatlar güncel katalog fiyatıdır (<see cref="CartLineData.CurrentUnitPrice"/>);
/// <see cref="CartLineData.HintUnitPrice"/> yalnız "fiyat değişti" bilgisini üretir. Toplam <c>decimal</c> ile toplanır, kuruşa SON adımda çevrilir.
/// Tek bir satır ya da toplam <see cref="Money.MaxAmount"/>'ı aşarsa taşma yerine <see cref="CartCalculation.TooLarge"/> döner (409, 500 değil).
/// </summary>
public static class CartCalculator
{
    public static CartCalculation Calculate(IReadOnlyList<CartLineData> lines)
    {
        var items = new List<CartItemDto>(lines.Count);
        var subtotal = 0m;
        var itemCount = 0;
        var hasIssues = false;

        foreach (var line in lines)
        {
            var available = AvailableQuantity(line);
            var status = StatusOf(line, available);
            itemCount += line.Quantity;
            hasIssues |= status != CartLineStatus.Available;

            if (status == CartLineStatus.Unavailable)
            {
                items.Add(ToDto(line, available, status, null, null, null, priceChanged: false));
                continue;
            }

            var lineSubtotal = line.CurrentUnitPrice * line.Quantity;
            subtotal += lineSubtotal;
            if (lineSubtotal > Money.MaxAmount || subtotal > Money.MaxAmount)
            {
                return new CartCalculation(null, TooLarge: true);
            }

            var priceChanged = line.HintUnitPrice != line.CurrentUnitPrice;
            items.Add(ToDto(
                line,
                available,
                status,
                Money.ToMinor(line.CurrentUnitPrice),
                Money.ToMinor(lineSubtotal),
                priceChanged ? Money.ToMinor(line.HintUnitPrice) : null,
                priceChanged));
        }

        return new CartCalculation(new CartDto(items, itemCount, Money.ToMinor(subtotal), Money.Currency, hasIssues), TooLarge: false);
    }

    /// <summary>Public stok tanımının tek cap'i (D-048): min(toplam, 20).</summary>
    public static int Cap(long availableOnHand) => PublicAvailability.Cap(availableOnHand);

    private static int AvailableQuantity(CartLineData line) => line.IsPurchasable ? Cap(line.AvailableOnHand) : 0;

    private static string StatusOf(CartLineData line, int available)
    {
        if (!line.IsPurchasable)
        {
            return CartLineStatus.Unavailable;
        }

        if (available == 0)
        {
            return CartLineStatus.OutOfStock;
        }

        return line.Quantity > available ? CartLineStatus.InsufficientStock : CartLineStatus.Available;
    }

    private static CartItemDto ToDto(
        CartLineData line,
        int available,
        string status,
        long? unitPriceMinor,
        long? lineSubtotalMinor,
        long? previousUnitPriceMinor,
        bool priceChanged) =>
        new(
            line.ItemId,
            line.VariantId,
            line.Sku,
            line.ProductSlug,
            line.ProductName,
            line.VariantName,
            line.ImageUrl,
            line.ImageAlt,
            line.Quantity,
            unitPriceMinor,
            lineSubtotalMinor,
            previousUnitPriceMinor,
            priceChanged,
            available,
            status);
}

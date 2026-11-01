namespace SSCommerce.Domain.Cart;

/// <summary>
/// Sepet kuralları (D-049, D-050, docs/CONTRACT_PHASES_5_6.md). Satır adedi sınırı veritabanında CHECK olarak da vardır
/// (<c>ck_cart_items_quantity_range</c>); bir sabiti değiştirirsen yeni migration gerekir. <see cref="MaxDistinctLines"/> yalnız uygulamada denetlenir.
/// </summary>
public static class CartRules
{
    /// <summary>Bir satırdaki en büyük adet (public stok üst sınırıyla aynı: Inventory PublicAvailabilityCap).</summary>
    public const int MaxLineQuantity = 20;

    /// <summary>Bir sepetteki en çok farklı satır (varyant) sayısı. DB'de karşılığı yok; sepet yazması kilit altında sayar.</summary>
    public const int MaxDistinctLines = 50;

    public static int RequireQuantity(int quantity)
    {
        if (quantity is < 1 or > MaxLineQuantity)
        {
            throw new CartRuleException($"Adet 1 ile {MaxLineQuantity} arasında olmalı.");
        }

        return quantity;
    }

    /// <summary>Katalog fiyatı kopyası (<c>unit_price_at_add</c>): 0 ≤ tutar ≤ Money.MaxAmount, en çok 2 ondalık basamak.</summary>
    public static decimal RequireUnitPrice(decimal price)
    {
        try
        {
            return Catalog.Money.EnsureValid(price);
        }
        catch (Catalog.CatalogRuleException exception)
        {
            throw new CartRuleException(exception.Message);
        }
    }
}

using SSCommerce.Domain.Catalog;

namespace SSCommerce.Domain.Cart;

/// <summary>
/// Sepet satırı: bir varyant ve adet. <see cref="UnitPriceAtAdd"/> sunucunun satır eklendiği/adedi değiştirildiği andaki katalog
/// fiyatıdır ve YALNIZCA "fiyat değişti" bilgisini vermek içindir. Hiçbir ara toplam, toplam ya da ödeme hesabında KULLANILMAZ
/// (tutar her zaman güncel katalog fiyatından hesaplanır, D-049) ve tarayıcıdan gelen bir değerle doldurulmaz.
/// </summary>
public sealed class CartItem : AuditedEntity
{
    private CartItem()
    {
    }

    public CartItem(Guid cartId, Guid productVariantId, int quantity, decimal currentCatalogUnitPrice)
    {
        if (cartId == Guid.Empty || productVariantId == Guid.Empty)
        {
            throw new CartRuleException("Sepet ve varyant kimliği zorunlu.");
        }

        CartId = cartId;
        ProductVariantId = productVariantId;
        Quantity = CartRules.RequireQuantity(quantity);
        UnitPriceAtAdd = CartRules.RequireUnitPrice(currentCatalogUnitPrice);
    }

    public Guid CartId { get; private set; }

    public Guid ProductVariantId { get; private set; }

    public int Quantity { get; private set; }

    /// <summary>Yalnız "fiyat değişti" ipucu. Tutar hesabına GİRMEZ. TL, ölçek 2.</summary>
    public decimal UnitPriceAtAdd { get; private set; }

    /// <summary>Mutlak adet; satır adedi her değiştiğinde ipucu fiyatı da güncel katalog fiyatıyla yenilenir.</summary>
    public void ChangeQuantity(int quantity, decimal currentCatalogUnitPrice)
    {
        var validQuantity = CartRules.RequireQuantity(quantity);
        UnitPriceAtAdd = CartRules.RequireUnitPrice(currentCatalogUnitPrice);
        Quantity = validQuantity;
    }
}

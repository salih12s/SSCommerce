using SSCommerce.Domain.Catalog;

namespace SSCommerce.Domain.Ordering;

/// <summary>
/// Sipariş kalemi: sipariş anındaki ürün/fiyat KOPYASI (snapshot). DEĞİŞMEZ: veritabanı trigger'ı UPDATE/DELETE/TRUNCATE'i reddeder (D-064),
/// bu yüzden tüm alanlar yalnız yapıcıda atanır ve <c>AuditedEntity</c> kullanılmaz. Katalog sonradan değişse de kalem aynı kalır.
/// <see cref="LineSubtotal"/> yapıcıda <c>birim fiyat × adet</c> olarak hesaplanır (DB CHECK'i de aynı eşitliği ister).
/// </summary>
public sealed class OrderItem
{
    private OrderItem()
    {
        Sku = string.Empty;
        ProductName = string.Empty;
        VariantName = string.Empty;
        ProductSlug = string.Empty;
        Currency = OrderRules.Currency;
    }

    public OrderItem(
        Guid orderId,
        Guid productVariantId,
        string sku,
        string productName,
        string variantName,
        string productSlug,
        string? imageUrl,
        string? imageAlt,
        decimal unitPrice,
        int quantity)
        : this()
    {
        if (orderId == Guid.Empty || productVariantId == Guid.Empty)
        {
            throw new OrderRuleException("Sipariş ve varyant kimliği zorunlu.");
        }

        if (imageUrl is null && imageAlt is not null)
        {
            throw new OrderRuleException("Görsel alt metni görsel yolu olmadan verilemez.");
        }

        Id = Guid.CreateVersion7();
        OrderId = orderId;
        ProductVariantId = productVariantId;
        Sku = OrderRules.RequireSku(sku);
        ProductName = OrderRules.RequireCatalogText(productName, CatalogRules.ProductNameMaxLength, "Ürün adı");
        VariantName = OrderRules.RequireCatalogText(variantName, CatalogRules.DisplayNameMaxLength, "Varyant adı");
        ProductSlug = OrderRules.RequireSlug(productSlug, CatalogRules.ProductSlugMaxLength, "Ürün slug'ı");
        ImageUrl = imageUrl is null ? null : OrderRules.RequireImagePath(imageUrl, CatalogRules.StorageKeyMaxLength, "Görsel yolu");
        ImageAlt = imageAlt is null ? null : OrderRules.RequireCatalogText(imageAlt, CatalogRules.AltTextMaxLength, "Alt metin");
        UnitPrice = OrderRules.RequireUnitPrice(unitPrice);
        Quantity = OrderRules.RequireQuantity(quantity);
        LineSubtotal = OrderRules.ComputeLineSubtotal(UnitPrice, Quantity);
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid ProductVariantId { get; private set; }

    public string Sku { get; private set; }

    public string ProductName { get; private set; }

    public string VariantName { get; private set; }

    public string ProductSlug { get; private set; }

    public string? ImageUrl { get; private set; }

    public string? ImageAlt { get; private set; }

    /// <summary>Sipariş anındaki katalog birim fiyatı (TL, ölçek 2).</summary>
    public decimal UnitPrice { get; private set; }

    public int Quantity { get; private set; }

    /// <summary><see cref="UnitPrice"/> × <see cref="Quantity"/>.</summary>
    public decimal LineSubtotal { get; private set; }

    /// <summary>Her zaman <c>TRY</c>.</summary>
    public string Currency { get; private set; }
}

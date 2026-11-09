namespace SSCommerce.Domain.Catalog;

/// <summary>
/// Ürün görseli. <c>StorageKey</c> web uygulamasının public yoludur (<c>/images/...</c>); dosya yüklemesi bu fazda yok.
/// <c>CreditKey</c> web'deki <c>image-credits.ts</c> kaydına bağdır (kaydın <c>file</c> alanı).
/// </summary>
public sealed class ProductImage : AuditedEntity
{
    private ProductImage()
    {
        StorageKey = string.Empty;
        AltText = string.Empty;
    }

    internal ProductImage(Guid productId, string storageKey, string altText, int sortOrder, bool isPrimary, string? creditKey)
    {
        ProductId = productId;
        StorageKey = CatalogRules.RequireImagePath(storageKey, CatalogRules.StorageKeyMaxLength, "Görsel yolu");
        AltText = CatalogRules.RequireText(altText, CatalogRules.AltTextMaxLength, "Alt metin");
        SortOrder = sortOrder < 0 ? throw new CatalogRuleException("Görsel sırası negatif olamaz.") : sortOrder;
        IsPrimary = isPrimary;
        CreditKey = creditKey is null
            ? null
            : CatalogRules.RequireImagePath(creditKey, CatalogRules.CreditKeyMaxLength, "Kaynak anahtarı");
    }

    public Guid ProductId { get; private set; }

    public string StorageKey { get; private set; }

    public string AltText { get; private set; }

    public int SortOrder { get; private set; }

    public bool IsPrimary { get; private set; }

    public string? CreditKey { get; private set; }
}

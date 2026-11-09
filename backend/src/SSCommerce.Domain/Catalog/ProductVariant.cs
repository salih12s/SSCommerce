namespace SSCommerce.Domain.Catalog;

/// <summary>
/// Satılabilir SKU. <see cref="Product"/> vitrin kaydıdır, varyant ise fiyatı ve seçenekleri taşıyan satış birimidir.
/// Stok bu varlıkta TUTULMAZ: bakiye <c>inventory_balances</c>, hareketler <c>stock_movements</c> tablosundadır (Faz 5, Domain/Inventory).
/// </summary>
public sealed class ProductVariant : AuditedEntity
{
    private ProductVariant()
    {
        Sku = string.Empty;
        DisplayName = string.Empty;
        Options = new Dictionary<string, string>();
        OptionSignature = VariantOptions.DefaultSignature;
        Currency = Money.Currency;
    }

    internal ProductVariant(Guid productId, string sku, string displayName, VariantOptions options, decimal price)
    {
        ProductId = productId;
        Sku = CatalogRules.RequireSku(sku);
        DisplayName = CatalogRules.RequireText(displayName, CatalogRules.DisplayNameMaxLength, "Varyant adı");
        Options = options.Values;
        OptionSignature = options.Signature;
        Price = Money.EnsureValid(price);
        Currency = Money.Currency;
        IsActive = true;
    }

    public Guid ProductId { get; private set; }

    public string Sku { get; private set; }

    public string DisplayName { get; private set; }

    public IReadOnlyDictionary<string, string> Options { get; private set; }

    public string OptionSignature { get; private set; }

    /// <summary>TL cinsinden, ölçek 2. API'ye <see cref="Money.ToMinor"/> ile kuruş olarak çıkar.</summary>
    public decimal Price { get; private set; }

    public string Currency { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>
    /// Yazma sürümü (Faz 8, D-068). Yeni satır 1'den başlar; her gerçek değişiklikte tam 1 artar. İyimser eşzamanlılık belirteci DEĞİLDİR:
    /// kontrol, kilitli satırdan okunan sürümle "kilitle + karşılaştır" biçiminde yapılır (docs/CONTRACT_PHASE_8.md).
    /// </summary>
    public int Version { get; private set; } = 1;

    /// <summary>Sürümü tam 1 artırır. Hiçbir şeyi değiştirmeyen yazma çağırmaz.</summary>
    public void BumpVersion() => Version = checked(Version + 1);

    public void SetActive(bool isActive) => IsActive = isActive;

    public void ChangePrice(decimal price) => Price = Money.EnsureValid(price);

    /// <summary>Görünen adı değiştirir (SKU ve seçenekler değişmez). Kurallar kurucuyla aynıdır.</summary>
    public void Rename(string displayName) =>
        DisplayName = CatalogRules.RequireText(displayName, CatalogRules.DisplayNameMaxLength, "Varyant adı");
}

namespace SSCommerce.Domain.Catalog;

public sealed class Product : AuditedEntity
{
    private readonly List<ProductVariant> _variants = [];
    private readonly List<ProductImage> _images = [];

    private Product()
    {
        Name = string.Empty;
        Slug = string.Empty;
    }

    public Product(
        Guid categoryId,
        string name,
        string slug,
        string? brand = null,
        string? description = null,
        IReadOnlyDictionary<string, string>? specifications = null,
        bool isDemo = false)
    {
        CategoryId = categoryId;
        Name = CatalogRules.RequireText(name, CatalogRules.ProductNameMaxLength, "Ürün adı");
        Slug = CatalogRules.RequireSlug(slug, CatalogRules.ProductSlugMaxLength, "Ürün slug");
        Brand = CatalogRules.OptionalText(brand, CatalogRules.BrandMaxLength, "Marka");
        Description = CatalogRules.OptionalText(description, CatalogRules.DescriptionMaxLength, "Açıklama");
        Specifications = NormalizeSpecifications(specifications);
        IsDemo = isDemo;
        IsActive = true;
    }

    public Guid CategoryId { get; private set; }

    public Category? Category { get; private set; }

    public string Name { get; private set; }

    public string Slug { get; private set; }

    public string? Brand { get; private set; }

    public string? Description { get; private set; }

    /// <summary>Teknik özellik tablosu (anahtar → değer); null olabilir. jsonb olarak saklanır.</summary>
    public IReadOnlyDictionary<string, string>? Specifications { get; private set; }

    /// <summary>Demo/test verisi mi? Gerçek ürünlerde false. Vitrin "Satış yakında" gösterimi bununla beslenir (D-027).</summary>
    public bool IsDemo { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>
    /// Yazma sürümü (Faz 8, D-068). Yeni satır 1'den başlar; her gerçek değişiklikte tam 1 artar. İyimser eşzamanlılık belirteci DEĞİLDİR:
    /// kontrol, kilitli satırdan okunan sürümle "kilitle + karşılaştır" biçiminde yapılır (docs/CONTRACT_PHASE_8.md).
    /// </summary>
    public int Version { get; private set; } = 1;

    /// <summary>Sürümü tam 1 artırır. Hiçbir şeyi değiştirmeyen yazma çağırmaz.</summary>
    public void BumpVersion() => Version = checked(Version + 1);

    public IReadOnlyCollection<ProductVariant> Variants => _variants;

    public IReadOnlyCollection<ProductImage> Images => _images;

    public ProductVariant AddVariant(string sku, string displayName, decimal price, IEnumerable<KeyValuePair<string, string>>? options = null)
    {
        var variantOptions = VariantOptions.Create(options);
        if (_variants.Any(v => v.OptionSignature == variantOptions.Signature))
        {
            throw new CatalogRuleException($"Bu üründe aynı seçeneklerle başka bir varyant var: {variantOptions.Signature}.");
        }

        var variant = new ProductVariant(Id, sku, displayName, variantOptions, price);
        _variants.Add(variant);
        return variant;
    }

    public ProductImage AddImage(string storageKey, string altText, int sortOrder = 0, bool isPrimary = false, string? creditKey = null)
    {
        if (isPrimary && _images.Any(i => i.IsPrimary))
        {
            throw new CatalogRuleException("Üründe zaten bir ana görsel var.");
        }

        var image = new ProductImage(Id, storageKey, altText, sortOrder, isPrimary, creditKey);
        _images.Add(image);
        return image;
    }

    public void SetActive(bool isActive) => IsActive = isActive;

    /// <summary>Adı değiştirir (slug değişmez: siparişler ve bağlantılar ona dayanır). Kurallar kurucuyla aynıdır.</summary>
    public void Rename(string name) => Name = CatalogRules.RequireText(name, CatalogRules.ProductNameMaxLength, "Ürün adı");

    public void SetBrand(string? brand) => Brand = CatalogRules.OptionalText(brand, CatalogRules.BrandMaxLength, "Marka");

    public void SetDescription(string? description) =>
        Description = CatalogRules.OptionalText(description, CatalogRules.DescriptionMaxLength, "Açıklama");

    public void SetSpecifications(IReadOnlyDictionary<string, string>? specifications) =>
        Specifications = NormalizeSpecifications(specifications);

    /// <summary>Başka kategoriye taşır. Kategorinin varlığını veritabanı FK'si ve servis denetler.</summary>
    public void MoveToCategory(Guid categoryId)
    {
        if (categoryId == Guid.Empty)
        {
            throw new CatalogRuleException("Kategori zorunlu.");
        }

        CategoryId = categoryId;
    }

    /// <summary>Teknik özellik tablosunu doğrular ve kararlı (ordinal sıralı) biçime çevirir; boş tablo null olur.</summary>
    public static SortedDictionary<string, string>? NormalizeSpecifications(IReadOnlyDictionary<string, string>? specifications)
    {
        if (specifications is null || specifications.Count == 0)
        {
            return null;
        }

        if (specifications.Count > CatalogRules.MaxSpecificationCount)
        {
            throw new CatalogRuleException($"En çok {CatalogRules.MaxSpecificationCount} teknik özellik olabilir.");
        }

        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawKey, rawValue) in specifications)
        {
            var key = CatalogRules.RequireText(rawKey, CatalogRules.SpecificationKeyMaxLength, "Özellik adı");
            var value = CatalogRules.RequireText(rawValue, CatalogRules.SpecificationValueMaxLength, $"Özellik değeri ({key})");
            if (!result.TryAdd(key, value))
            {
                throw new CatalogRuleException($"Özellik adı tekrar ediyor: {key}.");
            }
        }

        return result;
    }
}

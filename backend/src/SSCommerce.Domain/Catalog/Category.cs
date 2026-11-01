namespace SSCommerce.Domain.Catalog;

public sealed class Category : AuditedEntity
{
    private Category()
    {
        Name = string.Empty;
        Slug = string.Empty;
    }

    public Category(string name, string slug, Guid? parentId = null, int sortOrder = 0)
    {
        Name = CatalogRules.RequireText(name, CatalogRules.CategoryNameMaxLength, "Kategori adı");
        Slug = CatalogRules.RequireSlug(slug, CatalogRules.CategorySlugMaxLength, "Kategori slug");
        SortOrder = sortOrder;
        IsActive = true;
        SetParent(parentId);
    }

    public string Name { get; private set; }

    public string Slug { get; private set; }

    public Guid? ParentId { get; private set; }

    public int SortOrder { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Doğrudan kendine bağlanamaz (DB CHECK parent_id &lt;&gt; id). Döngü (A→B→A) denetimi gerekirse sonraki fazlarda eklenir.</summary>
    public void SetParent(Guid? parentId)
    {
        if (parentId == Id)
        {
            throw new CatalogRuleException("Kategori kendi üst kategorisi olamaz.");
        }

        ParentId = parentId;
    }

    public void SetActive(bool isActive) => IsActive = isActive;
}

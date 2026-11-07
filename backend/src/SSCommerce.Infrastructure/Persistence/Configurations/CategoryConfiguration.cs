using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SSCommerce.Domain.Catalog;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories", t =>
        {
            t.HasCheckConstraint("ck_categories_slug_format", $"slug ~ '{CatalogRules.SlugPattern}'");
            t.HasCheckConstraint("ck_categories_name_not_blank", "btrim(name) <> ''");
            t.HasCheckConstraint("ck_categories_parent_not_self", "parent_id IS NULL OR parent_id <> id");
            t.HasCheckConstraint("ck_categories_version_positive", "version >= 1");
        });

        builder.HasKey(c => c.Id).HasName("pk_categories");
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(CatalogRules.CategoryNameMaxLength).IsRequired();
        builder.Property(c => c.Slug).HasColumnName("slug").HasMaxLength(CatalogRules.CategorySlugMaxLength).IsRequired();
        builder.Property(c => c.ParentId).HasColumnName("parent_id");
        builder.Property(c => c.SortOrder).HasColumnName("sort_order");
        builder.Property(c => c.IsActive).HasColumnName("is_active");
        // Faz 8: yazma sürümü. Ham INSERT'ler sütunu vermez (DEFAULT 1). Eşzamanlılık belirteci değil: kilit + karşılaştırma (CONTRACT_PHASE_8).
        builder.Property(c => c.Version).HasColumnName("version").HasColumnType("integer").HasDefaultValue(1);
        builder.Property(c => c.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

        builder.HasIndex(c => c.Slug).IsUnique().HasDatabaseName("ux_categories_slug");

        // Alt kategori listesi ve parent_id FK'si için.
        builder.HasIndex(c => c.ParentId).HasDatabaseName("ix_categories_parent_id");

        // Üst kategori silinemez (geçmişi silme): vitrinden kaldırma is_active=false ile yapılır.
        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(c => c.ParentId)
            .HasConstraintName("fk_categories_parent")
            .OnDelete(DeleteBehavior.Restrict);
    }
}

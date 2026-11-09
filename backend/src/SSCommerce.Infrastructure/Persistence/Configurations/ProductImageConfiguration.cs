using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SSCommerce.Domain.Catalog;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

internal sealed class ProductImageConfiguration : IEntityTypeConfiguration<ProductImage>
{
    public void Configure(EntityTypeBuilder<ProductImage> builder)
    {
        builder.ToTable("product_images", t =>
        {
            t.HasCheckConstraint("ck_product_images_storage_key_format", $"storage_key ~ '{CatalogRules.ImagePathPattern}'");
            t.HasCheckConstraint(
                "ck_product_images_credit_key_format",
                $"credit_key IS NULL OR credit_key ~ '{CatalogRules.ImagePathPattern}'");
            t.HasCheckConstraint("ck_product_images_alt_text_not_blank", "btrim(alt_text) <> ''");
            t.HasCheckConstraint("ck_product_images_sort_order_non_negative", "sort_order >= 0");
        });

        builder.HasKey(i => i.Id).HasName("pk_product_images");
        builder.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(i => i.ProductId).HasColumnName("product_id");
        builder.Property(i => i.StorageKey).HasColumnName("storage_key").HasMaxLength(CatalogRules.StorageKeyMaxLength).IsRequired();
        builder.Property(i => i.AltText).HasColumnName("alt_text").HasMaxLength(CatalogRules.AltTextMaxLength).IsRequired();
        builder.Property(i => i.SortOrder).HasColumnName("sort_order");
        builder.Property(i => i.IsPrimary).HasColumnName("is_primary");
        builder.Property(i => i.CreditKey).HasColumnName("credit_key").HasMaxLength(CatalogRules.CreditKeyMaxLength);
        builder.Property(i => i.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(i => i.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

        // Galeri sırası + product_id FK'si için.
        builder.HasIndex(i => new { i.ProductId, i.SortOrder }).HasDatabaseName("ix_product_images_product_id_sort_order");
        // Ürün başına en fazla bir ana görsel (kısmi benzersiz indeks).
        builder.HasIndex(i => i.ProductId)
            .IsUnique()
            .HasFilter("is_primary")
            .HasDatabaseName("ux_product_images_product_id_primary");
    }
}

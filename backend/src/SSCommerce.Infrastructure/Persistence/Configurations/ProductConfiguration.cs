using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SSCommerce.Domain.Catalog;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products", t =>
        {
            t.HasCheckConstraint("ck_products_slug_format", $"slug ~ '{CatalogRules.SlugPattern}'");
            t.HasCheckConstraint("ck_products_name_not_blank", "btrim(name) <> ''");
            t.HasCheckConstraint("ck_products_brand_not_blank", "brand IS NULL OR btrim(brand) <> ''");
            t.HasCheckConstraint(
                "ck_products_description_length",
                $"description IS NULL OR char_length(description) <= {CatalogRules.DescriptionMaxLength}");
            t.HasCheckConstraint(
                "ck_products_specifications_object",
                $"specifications IS NULL OR (jsonb_typeof(specifications) = 'object' AND pg_column_size(specifications) <= {CatalogRules.SpecificationsMaxBytes})");
        });

        builder.HasKey(p => p.Id).HasName("pk_products");
        builder.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(p => p.CategoryId).HasColumnName("category_id");
        builder.Property(p => p.Name).HasColumnName("name").HasMaxLength(CatalogRules.ProductNameMaxLength).IsRequired();
        builder.Property(p => p.Slug).HasColumnName("slug").HasMaxLength(CatalogRules.ProductSlugMaxLength).IsRequired();
        builder.Property(p => p.Brand).HasColumnName("brand").HasMaxLength(CatalogRules.BrandMaxLength);
        builder.Property(p => p.Description).HasColumnName("description").HasColumnType("text");
        builder.Property(p => p.Specifications).HasColumnName("specifications").AsJsonbDictionary();
        builder.Property(p => p.IsDemo).HasColumnName("is_demo").HasDefaultValue(false);
        builder.Property(p => p.IsActive).HasColumnName("is_active");
        builder.Property(p => p.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

        builder.HasIndex(p => p.Slug).IsUnique().HasDatabaseName("ux_products_slug");
        // Kategori listesi (aktif ürünler) sorgusunu ve category_id FK'sini birlikte karşılar.
        builder.HasIndex(p => new { p.CategoryId, p.IsActive }).HasDatabaseName("ix_products_category_id_is_active");

        builder.HasOne(p => p.Category)
            .WithMany()
            .HasForeignKey(p => p.CategoryId)
            .HasConstraintName("fk_products_category")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(p => p.Variants)
            .WithOne()
            .HasForeignKey(v => v.ProductId)
            .HasConstraintName("fk_product_variants_product")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(p => p.Variants).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(p => p.Images)
            .WithOne()
            .HasForeignKey(i => i.ProductId)
            .HasConstraintName("fk_product_images_product")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(p => p.Images).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

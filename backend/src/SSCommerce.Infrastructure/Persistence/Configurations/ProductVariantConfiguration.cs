using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SSCommerce.Domain.Catalog;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

internal sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        builder.ToTable("product_variants", t =>
        {
            t.HasCheckConstraint(
                "ck_product_variants_sku_format",
                $"sku ~ '{CatalogRules.SkuPattern}' AND char_length(sku) BETWEEN {CatalogRules.SkuMinLength} AND {CatalogRules.SkuMaxLength}");
            t.HasCheckConstraint("ck_product_variants_display_name_not_blank", "btrim(display_name) <> ''");
            t.HasCheckConstraint("ck_product_variants_price_non_negative", "price >= 0");
            // MVP yalnız TRY (D-026); çoklu para birimi gelirse bu CHECK bilinçli olarak gevşetilir.
            t.HasCheckConstraint("ck_product_variants_currency_try", $"currency = '{Money.Currency}'");
            t.HasCheckConstraint(
                "ck_product_variants_options_object",
                $"jsonb_typeof(options) = 'object' AND pg_column_size(options) <= {CatalogRules.OptionsMaxBytes}");
            t.HasCheckConstraint("ck_product_variants_option_signature_not_blank", "btrim(option_signature) <> ''");
        });

        builder.HasKey(v => v.Id).HasName("pk_product_variants");
        builder.Property(v => v.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(v => v.ProductId).HasColumnName("product_id");
        builder.Property(v => v.Sku).HasColumnName("sku").HasMaxLength(CatalogRules.SkuMaxLength).IsRequired();
        builder.Property(v => v.DisplayName).HasColumnName("display_name").HasMaxLength(CatalogRules.DisplayNameMaxLength).IsRequired();
        builder.Property(v => v.Options).HasColumnName("options").AsJsonbDictionary();
        builder.Property(v => v.OptionSignature).HasColumnName("option_signature").HasMaxLength(CatalogRules.OptionSignatureMaxLength).IsRequired();
        builder.Property(v => v.Price).HasColumnName("price").HasColumnType("numeric(18,2)");
        builder.Property(v => v.Currency).HasColumnName("currency").HasColumnType("char(3)").IsRequired();
        builder.Property(v => v.IsActive).HasColumnName("is_active");
        builder.Property(v => v.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(v => v.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

        builder.HasIndex(v => v.Sku).IsUnique().HasDatabaseName("ux_product_variants_sku");
        // product_id önde olduğu için product_id FK'sini ayrıca indekslemeye gerek kalmaz.
        builder.HasIndex(v => new { v.ProductId, v.OptionSignature }).IsUnique().HasDatabaseName("ux_product_variants_product_id_option_signature");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Ordering;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>order_items</c>: sipariş anındaki ürün/fiyat kopyası. DEĞİŞMEZ (migration'daki trigger, D-064). Varyanta FK vardır ama katalog
/// değerleri kopyadır: katalog değişse de kalem değişmez.
/// </summary>
internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("order_items", t =>
        {
            t.HasCheckConstraint("ck_order_items_sku_format", $"sku ~ '{CatalogRules.SkuPattern}'");
            t.HasCheckConstraint("ck_order_items_product_name_not_blank", "btrim(product_name) <> ''");
            t.HasCheckConstraint("ck_order_items_variant_name_not_blank", "btrim(variant_name) <> ''");
            t.HasCheckConstraint("ck_order_items_product_slug_format", $"product_slug ~ '{CatalogRules.SlugPattern}'");
            t.HasCheckConstraint("ck_order_items_image_url_format", $"image_url IS NULL OR image_url ~ '{CatalogRules.ImagePathPattern}'");
            t.HasCheckConstraint("ck_order_items_unit_price_non_negative", "unit_price >= 0");
            t.HasCheckConstraint("ck_order_items_quantity_range", $"quantity BETWEEN 1 AND {OrderRules.MaxLineQuantity}");
            t.HasCheckConstraint("ck_order_items_line_subtotal", "line_subtotal = unit_price * quantity");
            t.HasCheckConstraint("ck_order_items_currency", $"currency = '{OrderRules.Currency}'");
        });

        builder.HasKey(i => i.Id).HasName("pk_order_items");
        builder.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(i => i.OrderId).HasColumnName("order_id");
        builder.Property(i => i.ProductVariantId).HasColumnName("product_variant_id");
        builder.Property(i => i.Sku).HasColumnName("sku").HasMaxLength(CatalogRules.SkuMaxLength).IsRequired();
        builder.Property(i => i.ProductName).HasColumnName("product_name").HasMaxLength(CatalogRules.ProductNameMaxLength).IsRequired();
        builder.Property(i => i.VariantName).HasColumnName("variant_name").HasMaxLength(CatalogRules.DisplayNameMaxLength).IsRequired();
        builder.Property(i => i.ProductSlug).HasColumnName("product_slug").HasMaxLength(CatalogRules.ProductSlugMaxLength).IsRequired();
        builder.Property(i => i.ImageUrl).HasColumnName("image_url").HasMaxLength(CatalogRules.StorageKeyMaxLength);
        builder.Property(i => i.ImageAlt).HasColumnName("image_alt").HasMaxLength(CatalogRules.AltTextMaxLength);
        builder.Property(i => i.UnitPrice).HasColumnName("unit_price").HasColumnType("numeric(18,2)");
        builder.Property(i => i.Quantity).HasColumnName("quantity");
        builder.Property(i => i.LineSubtotal).HasColumnName("line_subtotal").HasColumnType("numeric(18,2)");
        builder.Property(i => i.Currency).HasColumnName("currency").HasColumnType("char(3)").IsRequired();

        // order_id önde olduğu için order FK'si ayrıca indekslenmez.
        builder.HasIndex(i => new { i.OrderId, i.ProductVariantId }).IsUnique().HasDatabaseName("ux_order_items_order_id_product_variant_id");
        builder.HasIndex(i => i.ProductVariantId).HasDatabaseName("ix_order_items_product_variant_id");

        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(i => i.OrderId)
            .HasConstraintName("fk_order_items_order")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ProductVariant>()
            .WithMany()
            .HasForeignKey(i => i.ProductVariantId)
            .HasConstraintName("fk_order_items_product_variant")
            .OnDelete(DeleteBehavior.Restrict);
    }
}

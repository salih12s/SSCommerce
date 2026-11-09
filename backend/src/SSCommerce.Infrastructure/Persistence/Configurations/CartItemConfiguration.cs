using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SSCommerce.Domain.Cart;
using SSCommerce.Domain.Catalog;
using DomainCart = SSCommerce.Domain.Cart.Cart;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

internal sealed class CartItemConfiguration : IEntityTypeConfiguration<CartItem>
{
    public void Configure(EntityTypeBuilder<CartItem> builder)
    {
        builder.ToTable("cart_items", t =>
        {
            t.HasCheckConstraint("ck_cart_items_quantity_range", $"quantity BETWEEN 1 AND {CartRules.MaxLineQuantity}");
            t.HasCheckConstraint("ck_cart_items_unit_price_at_add_non_negative", "unit_price_at_add >= 0");
        });

        builder.HasKey(i => i.Id).HasName("pk_cart_items");
        builder.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(i => i.CartId).HasColumnName("cart_id");
        builder.Property(i => i.ProductVariantId).HasColumnName("product_variant_id");
        builder.Property(i => i.Quantity).HasColumnName("quantity");
        // Yalnız "fiyat değişti" ipucu: ara toplam/toplam hesabına HİÇBİR yerde girmez (D-049).
        builder.Property(i => i.UnitPriceAtAdd).HasColumnName("unit_price_at_add").HasColumnType("numeric(18,2)");
        builder.Property(i => i.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(i => i.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

        // cart_id önde olduğu için cart FK'sini ayrıca indekslemeye gerek kalmaz.
        builder.HasIndex(i => new { i.CartId, i.ProductVariantId }).IsUnique().HasDatabaseName("ux_cart_items_cart_id_product_variant_id");
        builder.HasIndex(i => i.ProductVariantId).HasDatabaseName("ix_cart_items_product_variant_id");

        builder.HasOne<DomainCart>()
            .WithMany()
            .HasForeignKey(i => i.CartId)
            .HasConstraintName("fk_cart_items_cart")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ProductVariant>()
            .WithMany()
            .HasForeignKey(i => i.ProductVariantId)
            .HasConstraintName("fk_cart_items_product_variant")
            .OnDelete(DeleteBehavior.Restrict);
    }
}

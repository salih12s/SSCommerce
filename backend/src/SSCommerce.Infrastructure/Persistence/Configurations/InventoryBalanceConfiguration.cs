using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Inventory;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

internal sealed class InventoryBalanceConfiguration : IEntityTypeConfiguration<InventoryBalance>
{
    public void Configure(EntityTypeBuilder<InventoryBalance> builder)
    {
        builder.ToTable("inventory_balances", t =>
        {
            t.HasCheckConstraint(
                "ck_inventory_balances_on_hand_range",
                $"on_hand_quantity BETWEEN 0 AND {InventoryRules.MaxOnHandQuantity}");
            t.HasCheckConstraint("ck_inventory_balances_version_positive", "version >= 1");
        });

        builder.HasKey(b => b.Id).HasName("pk_inventory_balances");
        builder.Property(b => b.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(b => b.WarehouseId).HasColumnName("warehouse_id");
        builder.Property(b => b.ProductVariantId).HasColumnName("product_variant_id");
        builder.Property(b => b.OnHandQuantity).HasColumnName("on_hand_quantity");
        // Düz sütun: EF eşzamanlılık belirteci DEĞİL. Eşzamanlılık atomik koşullu SQL ile sağlanır (D-046); sürüm yalnız tanı içindir.
        builder.Property(b => b.Version).HasColumnName("version").HasColumnType("bigint");
        builder.Property(b => b.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(b => b.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

        // warehouse_id önde olduğu için warehouse FK'sini ayrıca indekslemeye gerek kalmaz; ON CONFLICT hedefi de bu indekstir.
        builder.HasIndex(b => new { b.WarehouseId, b.ProductVariantId }).IsUnique()
            .HasDatabaseName("ux_inventory_balances_warehouse_id_product_variant_id");
        // Public toplam (SUM by variant) ve variant FK'si için.
        builder.HasIndex(b => b.ProductVariantId).HasDatabaseName("ix_inventory_balances_product_variant_id");

        builder.HasOne<Warehouse>()
            .WithMany()
            .HasForeignKey(b => b.WarehouseId)
            .HasConstraintName("fk_inventory_balances_warehouse")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ProductVariant>()
            .WithMany()
            .HasForeignKey(b => b.ProductVariantId)
            .HasConstraintName("fk_inventory_balances_product_variant")
            .OnDelete(DeleteBehavior.Restrict);
    }
}

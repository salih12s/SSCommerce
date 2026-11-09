using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Inventory;
using SSCommerce.Domain.Ordering;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>order_inventory_allocations</c>: bir kalemin hangi depodan kaç adet düşüldüğü (D-062). DEĞİŞMEZ (migration'daki trigger, D-064).
/// İptal iadesi bu satırlara göre özgün depoya yapılır. <c>placement_movement_id</c> düşümü kanıtlayan stok hareketine bağlar.
/// </summary>
internal sealed class OrderInventoryAllocationConfiguration : IEntityTypeConfiguration<OrderInventoryAllocation>
{
    public void Configure(EntityTypeBuilder<OrderInventoryAllocation> builder)
    {
        builder.ToTable("order_inventory_allocations", t =>
        {
            t.HasCheckConstraint("ck_order_inventory_allocations_quantity_positive", "quantity > 0");
        });

        builder.HasKey(a => a.Id).HasName("pk_order_inventory_allocations");
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(a => a.OrderItemId).HasColumnName("order_item_id");
        builder.Property(a => a.WarehouseId).HasColumnName("warehouse_id");
        builder.Property(a => a.ProductVariantId).HasColumnName("product_variant_id");
        builder.Property(a => a.Quantity).HasColumnName("quantity");
        builder.Property(a => a.PlacementMovementId).HasColumnName("placement_movement_id");

        // order_item_id önde olduğu için kalem FK'si ayrıca indekslenmez; iptal "kalem başına parça" sorgusu da bunu kullanır.
        builder.HasIndex(a => new { a.OrderItemId, a.WarehouseId }).IsUnique().HasDatabaseName("ux_order_inventory_allocations_order_item_id_warehouse_id");
        // Bir stok hareketi en çok bir dağıtım parçasını kanıtlar (operation_key zaten dağıtım kimliğine bağlıdır); aynı zamanda FK indeksi.
        builder.HasIndex(a => a.PlacementMovementId).IsUnique().HasDatabaseName("ux_order_inventory_allocations_placement_movement_id");
        builder.HasIndex(a => a.WarehouseId).HasDatabaseName("ix_order_inventory_allocations_warehouse_id");
        builder.HasIndex(a => a.ProductVariantId).HasDatabaseName("ix_order_inventory_allocations_product_variant_id");

        builder.HasOne<OrderItem>()
            .WithMany()
            .HasForeignKey(a => a.OrderItemId)
            .HasConstraintName("fk_order_inventory_allocations_order_item")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Warehouse>()
            .WithMany()
            .HasForeignKey(a => a.WarehouseId)
            .HasConstraintName("fk_order_inventory_allocations_warehouse")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ProductVariant>()
            .WithMany()
            .HasForeignKey(a => a.ProductVariantId)
            .HasConstraintName("fk_order_inventory_allocations_product_variant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<StockMovement>()
            .WithMany()
            .HasForeignKey(a => a.PlacementMovementId)
            .HasConstraintName("fk_order_inventory_allocations_placement_movement")
            .OnDelete(DeleteBehavior.Restrict);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Inventory;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        var operationTypes = string.Join(", ", StockOperationTypes.All.Select(t => $"'{t}'"));

        builder.ToTable("stock_movements", t =>
        {
            t.HasCheckConstraint("ck_stock_movements_quantity_delta_nonzero", "quantity_delta <> 0");
            t.HasCheckConstraint(
                "ck_stock_movements_quantity_delta_range",
                $"quantity_delta BETWEEN -{InventoryRules.MaxAbsoluteDelta} AND {InventoryRules.MaxAbsoluteDelta}");
            t.HasCheckConstraint("ck_stock_movements_operation_type", $"operation_type IN ({operationTypes})");
            t.HasCheckConstraint(
                "ck_stock_movements_on_hand_after_range",
                $"on_hand_after BETWEEN 0 AND {InventoryRules.MaxOnHandQuantity}");
            t.HasCheckConstraint("ck_stock_movements_sku_format", $"sku ~ '{CatalogRules.SkuPattern}'");
            t.HasCheckConstraint("ck_stock_movements_reason_not_blank", "btrim(reason) <> ''");
            t.HasCheckConstraint("ck_stock_movements_reference_not_blank", "reference IS NULL OR btrim(reference) <> ''");
            t.HasCheckConstraint("ck_stock_movements_operation_key_format", $"operation_key ~ '{InventoryRules.OperationKeyPattern}'");
            t.HasCheckConstraint(
                "ck_stock_movements_request_fingerprint_format",
                $"request_fingerprint ~ '{InventoryRules.RequestFingerprintPattern}'");
        });

        builder.HasKey(m => m.Id).HasName("pk_stock_movements");
        builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(m => m.WarehouseId).HasColumnName("warehouse_id");
        builder.Property(m => m.ProductVariantId).HasColumnName("product_variant_id");
        builder.Property(m => m.Sku).HasColumnName("sku").HasMaxLength(CatalogRules.SkuMaxLength).IsRequired();
        builder.Property(m => m.QuantityDelta).HasColumnName("quantity_delta");
        builder.Property(m => m.OperationType)
            .HasColumnName("operation_type")
            .HasMaxLength(32)
            .HasConversion(new ValueConverter<StockOperationType, string>(
                type => StockOperationTypes.ToWire(type),
                wire => StockOperationTypes.FromWire(wire)))
            .IsRequired();
        builder.Property(m => m.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(m => m.Reason).HasColumnName("reason").HasMaxLength(InventoryRules.ReasonMaxLength).IsRequired();
        builder.Property(m => m.Reference).HasColumnName("reference").HasMaxLength(InventoryRules.ReferenceMaxLength);
        builder.Property(m => m.OperationKey).HasColumnName("operation_key").HasMaxLength(InventoryRules.OperationKeyMaxLength).IsRequired();
        builder.Property(m => m.RequestFingerprint)
            .HasColumnName("request_fingerprint")
            .HasColumnType($"char({InventoryRules.RequestFingerprintLength})")
            .IsRequired();
        builder.Property(m => m.OnHandAfter).HasColumnName("on_hand_after");
        builder.Property(m => m.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");

        builder.HasIndex(m => m.OperationKey).IsUnique().HasDatabaseName("ux_stock_movements_operation_key");
        // Önde warehouse_id / product_variant_id olduğu için bu FK'ler ayrıca indekslenmez.
        builder.HasIndex(m => new { m.WarehouseId, m.OccurredAt }).HasDatabaseName("ix_stock_movements_warehouse_id_occurred_at");
        builder.HasIndex(m => new { m.ProductVariantId, m.OccurredAt }).HasDatabaseName("ix_stock_movements_product_variant_id_occurred_at");
        // actor_user_id FK'si: kullanıcı silme/denetim sorgusu bu sütunu tarar; EF'in otomatik "IX_" adı yerine açık ad.
        builder.HasIndex(m => m.ActorUserId).HasDatabaseName("ix_stock_movements_actor_user_id");

        builder.HasOne<Warehouse>()
            .WithMany()
            .HasForeignKey(m => m.WarehouseId)
            .HasConstraintName("fk_stock_movements_warehouse")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ProductVariant>()
            .WithMany()
            .HasForeignKey(m => m.ProductVariantId)
            .HasConstraintName("fk_stock_movements_product_variant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(m => m.ActorUserId)
            .HasConstraintName("fk_stock_movements_actor_user")
            .OnDelete(DeleteBehavior.Restrict);
    }
}

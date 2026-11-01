using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SSCommerce.Domain.Inventory;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

internal sealed class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> builder)
    {
        builder.ToTable("warehouses", t =>
        {
            t.HasCheckConstraint("ck_warehouses_code_format", $"code ~ '{InventoryRules.WarehouseCodePattern}'");
            t.HasCheckConstraint("ck_warehouses_name_not_blank", "btrim(name) <> ''");
        });

        builder.HasKey(w => w.Id).HasName("pk_warehouses");
        builder.Property(w => w.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(w => w.Code).HasColumnName("code").HasMaxLength(InventoryRules.WarehouseCodeMaxLength).IsRequired();
        builder.Property(w => w.Name).HasColumnName("name").HasMaxLength(InventoryRules.WarehouseNameMaxLength).IsRequired();
        builder.Property(w => w.IsActive).HasColumnName("is_active");
        builder.Property(w => w.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(w => w.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

        builder.HasIndex(w => w.Code).IsUnique().HasDatabaseName("ux_warehouses_code");
    }
}

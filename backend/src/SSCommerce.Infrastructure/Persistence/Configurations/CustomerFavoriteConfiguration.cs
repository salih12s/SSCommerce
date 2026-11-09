using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Customers;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

internal sealed class CustomerFavoriteConfiguration : IEntityTypeConfiguration<CustomerFavorite>
{
    public void Configure(EntityTypeBuilder<CustomerFavorite> builder)
    {
        builder.ToTable("customer_favorites");

        // Bileşik anahtar: aynı ürün kullanıcı başına bir kez (idempotent ekleme benzersizlik ihlalini başarı sayar, MD-1).
        // user_id önde olduğu için kullanıcı FK'si ayrıca indekslenmez; product_id araması gerekirse ayrı indeks eklenir.
        builder.HasKey(f => new { f.UserId, f.ProductId }).HasName("pk_customer_favorites");
        builder.Property(f => f.UserId).HasColumnName("user_id");
        builder.Property(f => f.ProductId).HasColumnName("product_id");
        builder.Property(f => f.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");

        // Liste: kullanıcının favorileri en yeniden eskiye.
        builder.HasIndex(f => new { f.UserId, f.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_customer_favorites_user_id_created_at");

        // Ürün FK'sı için (Restrict kontrolü ve ürüne göre arama); adı projedeki ix_ kalıbına uyar.
        builder.HasIndex(f => f.ProductId).HasDatabaseName("ix_customer_favorites_product_id");

        // Kullanıcı silinirse favorileri de gider (kişisel veri; sipariş gibi saklama yükümlülüğü yok).
        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(f => f.UserId)
            .HasConstraintName("fk_customer_favorites_user")
            .OnDelete(DeleteBehavior.Cascade);

        // Ürünler silinmez (pasifleştirilir); katalogdaki tüm ürün/varyant FK'ları gibi Restrict: yanlışlıkla silme favorileri sessizce yok etmesin.
        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(f => f.ProductId)
            .HasConstraintName("fk_customer_favorites_product")
            .OnDelete(DeleteBehavior.Restrict);
    }
}

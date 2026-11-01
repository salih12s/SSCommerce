using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SSCommerce.Infrastructure.Identity;
using DomainCart = SSCommerce.Domain.Cart.Cart;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

// "DomainCart" takma adı bilinçlidir: SSCommerce.Domain.Cart ad alanı ile Cart tipi, SSCommerce.* altındaki ad alanlarında karışabilir.
internal sealed class CartConfiguration : IEntityTypeConfiguration<DomainCart>
{
    public void Configure(EntityTypeBuilder<DomainCart> builder)
    {
        builder.ToTable("carts");

        builder.HasKey(c => c.Id).HasName("pk_carts");
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.UserId).HasColumnName("user_id");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

        // Kullanıcı başına tek sepet (D-049); aynı zamanda user_id FK'sinin indeksi ve INSERT … ON CONFLICT (user_id) hedefi.
        builder.HasIndex(c => c.UserId).IsUnique().HasDatabaseName("ux_carts_user_id");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .HasConstraintName("fk_carts_user")
            .OnDelete(DeleteBehavior.Cascade);
    }
}

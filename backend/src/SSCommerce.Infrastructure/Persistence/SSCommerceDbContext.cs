using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SSCommerce.Domain.Cart;
using SSCommerce.Domain.Catalog;
using SSCommerce.Domain.Customers;
using SSCommerce.Domain.Inventory;
using SSCommerce.Infrastructure.Identity;
using SSCommerce.Infrastructure.Persistence.Configurations;
using DomainCart = SSCommerce.Domain.Cart.Cart;

namespace SSCommerce.Infrastructure.Persistence;

/// <summary>
/// Tek DbContext (modüler monolit). Model yapılandırması <c>Configurations/</c> altındadır; şema yalnızca migration'larla yönetilir
/// (EnsureCreated/EnsureDeleted kullanılmaz). created_at/updated_at SaveChanges sırasında <see cref="TimeProvider"/> ile UTC damgalanır.
/// </summary>
public sealed class SSCommerceDbContext(DbContextOptions<SSCommerceDbContext> options, TimeProvider timeProvider)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();

    public DbSet<ProductImage> ProductImages => Set<ProductImage>();

    public DbSet<Warehouse> Warehouses => Set<Warehouse>();

    public DbSet<InventoryBalance> InventoryBalances => Set<InventoryBalance>();

    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    public DbSet<DomainCart> Carts => Set<DomainCart>();

    public DbSet<CartItem> CartItems => Set<CartItem>();

    public DbSet<CustomerAddress> CustomerAddresses => Set<CustomerAddress>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Önce Identity'nin varsayılan AspNet* modeli, sonra Catalog yapılandırmaları, en son Identity'ye eklenen DB kuralları.
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SSCommerceDbContext).Assembly);
        modelBuilder.ConfigureIdentity();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampTimestamps()
    {
        var utcNow = timeProvider.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries<AppUser>())
        {
            if (entry.State == EntityState.Added && entry.Entity.CreatedAt == default)
            {
                entry.Entity.CreatedAt = utcNow;
            }
        }

        foreach (var entry in ChangeTracker.Entries<AuditedEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.MarkCreated(utcNow);
                    break;
                case EntityState.Modified:
                    entry.Entity.MarkUpdated(utcNow);
                    break;
            }
        }
    }
}

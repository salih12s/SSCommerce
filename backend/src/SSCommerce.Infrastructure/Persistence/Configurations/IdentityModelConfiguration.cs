using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

/// <summary>
/// Identity tablolarının (AspNet*) varsayılan şemasına eklenen DB düzeyi kurallar (D-032). <c>base.OnModelCreating</c>'den SONRA çağrılmalıdır.
/// Tablo/kolon adları Identity varsayılanıdır (PascalCase); Catalog'un snake_case adlandırması bu tablolara uygulanmaz.
/// </summary>
internal static class IdentityModelConfiguration
{
    public static void ConfigureIdentity(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(user =>
        {
            user.Property(u => u.CreatedAt).HasColumnType("timestamptz");

            // Identity varsayılanı UNIQUE değildir (benzersizlik yalnız UserManager'da, uygulama düzeyinde). Eşzamanlı iki kayıt
            // aynı e-postayı geçirmesin diye veritabanında da benzersiz: aynı indeks (EmailIndex), yalnız UNIQUE eklenir.
            user.HasIndex(u => u.NormalizedEmail).HasDatabaseName("EmailIndex").IsUnique();
        });

        // Yalnız rol ADLARI (kullanıcı/parola yok). Id/NormalizedName/ConcurrencyStamp sabit: migration deterministik kalır.
        modelBuilder.Entity<IdentityRole<Guid>>().HasData(
            Role(AppRoles.CustomerId, AppRoles.Customer, AppRoles.CustomerConcurrencyStamp),
            Role(AppRoles.WarehouseId, AppRoles.Warehouse, AppRoles.WarehouseConcurrencyStamp),
            Role(AppRoles.AdminId, AppRoles.Admin, AppRoles.AdminConcurrencyStamp));
    }

    private static IdentityRole<Guid> Role(Guid id, string name, string concurrencyStamp) => new()
    {
        Id = id,
        Name = name,
        NormalizedName = name.ToUpperInvariant(),
        ConcurrencyStamp = concurrencyStamp,
    };
}

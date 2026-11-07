using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SSCommerce.Application.Addresses;
using SSCommerce.Application.Administration;
using SSCommerce.Application.Carts;
using SSCommerce.Application.Catalog;
using SSCommerce.Application.Checkout;
using SSCommerce.Application.Inventory;
using SSCommerce.Application.Orders;
using SSCommerce.Infrastructure.Addresses;
using SSCommerce.Infrastructure.Administration;
using SSCommerce.Infrastructure.Carts;
using SSCommerce.Infrastructure.Catalog;
using SSCommerce.Infrastructure.Checkout;
using SSCommerce.Infrastructure.Health;
using SSCommerce.Infrastructure.Identity;
using SSCommerce.Infrastructure.Inventory;
using SSCommerce.Infrastructure.Orders;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Bağlantı dizesi anahtarı: <c>ConnectionStrings:Postgres</c> (ortam değişkeni <c>ConnectionStrings__Postgres</c>).</summary>
    public const string ConnectionStringName = "Postgres";

    /// <summary>Tek komut zaman aşımı (sn). Kilit bekleyen/takılan sorgu havuz bağlantısını sonsuza dek tutmasın.</summary>
    public const int CommandTimeoutSeconds = 15;

    /// <summary>Bağlantı dizesi havuz boyutunu açıkça vermediyse uygulanan üst sınır (Npgsql varsayılanı 100, PostgreSQL varsayılanı toplam 100 bağlantı).</summary>
    public const int DefaultMaxPoolSize = 30;

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Zaman damgaları için saat kaynağı; testler kendi TimeProvider'ını kaydedebilir.
        services.TryAddSingleton(TimeProvider.System);

        // Seçenekler DbContext ilk çözüldüğünde okunur: bağlantı dizesi olmadan uygulama açılır,
        // yalnızca veritabanına ihtiyaç duyan istekler başarısız olur.
        services.AddDbContext<SSCommerceDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"ConnectionStrings:{ConnectionStringName} yapılandırılmamış (ortam değişkeni ConnectionStrings__{ConnectionStringName}).");
            }

            options.UseNpgsql(WithPoolLimit(connectionString), npgsql => npgsql.CommandTimeout(CommandTimeoutSeconds));
        });

        // Public katalog okuma sorguları (F3.2); DbContext ile aynı ömür (istek başına).
        services.AddScoped<ICatalogQueries, CatalogQueries>();

        // Stok (F5): yazma servisi atomik/idempotent, okuma sorguları admin içindir.
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<IInventoryQueries, InventoryQueries>();

        // Sepet, adres ve checkout preview (F6). Hepsi istek başına; preview salt okunurdur.
        services.AddScoped<CartLineReader>();
        services.AddScoped<ICartService, CartService>();
        services.AddScoped<IAddressService, AddressService>();
        services.AddScoped<ICheckoutPreviewService, CheckoutPreviewService>();

        // Siparişler (F7). Hepsi istek başına: verme ve durum geçişi kendi transaction'ını yönetir, sorgular salt okunurdur.
        services.AddScoped<OrderReader>();
        services.AddScoped<IOrderPlacementService, OrderPlacementService>();
        services.AddScoped<IOrderStatusService, OrderStatusService>();
        services.AddScoped<IOrderQueries, OrderQueries>();
        services.AddScoped<IDeliveryViewService, DeliveryViewService>();

        // Ödenmemiş sipariş sınırları (Faz 8, F8.5): açılışta 1..1000 doğrulanır, geçersizse uygulama AÇILMAZ.
        services.AddOptions<OrderLimitOptions>()
            .BindConfiguration(OrderLimitOptions.SectionName)
            .Validate(options => options.Validate() is null, "Ordering:MaxOpenOrdersPerUser ve Ordering:MaxOrdersPerRollingDay 1 ile 1000 arasında olmalıdır.")
            .ValidateOnStart();

        // Yönetim (Faz 8): katalog yazma/okuma, pano ve raporlar, personel rolleri. Hepsi istek başına.
        services.AddScoped<AdminCatalogQueries>();
        services.AddScoped<IAdminCatalogQueries>(provider => provider.GetRequiredService<AdminCatalogQueries>());
        services.AddScoped<IAdminCatalogService, AdminCatalogService>();
        services.AddScoped<IDashboardQueries, DashboardQueries>();
        services.AddScoped<IStaffQueries, StaffQueries>();
        services.AddScoped<IStaffRoleService, StaffRoleService>();

        // Identity çekirdeği (D-032): kullanıcı/rol yönetimi. Cookie şeması ve politikalar Api'de.
        services.AddIdentityServices();

        // Soğuk başlangıç ısınması (D-065): dinlemeye başlamadan ÖNCE çalışır, hatası yutulur. Test türüyle okuyabilsin diye ayrıca tekil kayıtlı.
        services.AddSingleton<ReadinessWarmUp>();
        services.AddHostedService(provider => provider.GetRequiredService<ReadinessWarmUp>());

        services.AddSingleton<MigrationReadinessState>();
        services.AddHealthChecks()
            .AddCheck<PostgresReadinessHealthCheck>(PostgresReadinessHealthCheck.Name, tags: ["ready"])
            .AddCheck<PendingMigrationsHealthCheck>(PendingMigrationsHealthCheck.Name, tags: ["ready"]);

        return services;
    }

    /// <summary>
    /// Bağlantı dizesi <c>Maximum Pool Size</c>'ı (ya da eş anlamlılarını) vermediyse <see cref="DefaultMaxPoolSize"/> ekler.
    /// Dize ayrıştırılamıyorsa olduğu gibi döner: hatayı Npgsql bağlanırken kendi iletisiyle verir. Bağlantı dizesi ASLA loglanmaz.
    /// </summary>
    internal static string WithPoolLimit(string connectionString)
    {
        DbConnectionStringBuilder builder;
        try
        {
            builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        }
        catch (ArgumentException)
        {
            return connectionString;
        }

        string[] aliases = ["Maximum Pool Size", "MaxPoolSize", "Max Pool Size", "Maximum_Pool_Size"];
        if (aliases.Any(builder.ContainsKey))
        {
            return connectionString;
        }

        builder["Maximum Pool Size"] = DefaultMaxPoolSize;
        return builder.ConnectionString;
    }
}

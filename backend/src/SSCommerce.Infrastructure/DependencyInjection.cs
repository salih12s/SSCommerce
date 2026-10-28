using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SSCommerce.Infrastructure.Health;
using SSCommerce.Infrastructure.Persistence;

namespace SSCommerce.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Bağlantı dizesi anahtarı: <c>ConnectionStrings:Postgres</c> (ortam değişkeni <c>ConnectionStrings__Postgres</c>).</summary>
    public const string ConnectionStringName = "Postgres";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
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

            options.UseNpgsql(connectionString);
        });

        services.AddHealthChecks()
            .AddCheck<PostgresReadinessHealthCheck>(PostgresReadinessHealthCheck.Name, tags: ["ready"]);

        return services;
    }
}

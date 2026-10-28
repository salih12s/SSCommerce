using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SSCommerce.Api.Tests;

/// <summary>
/// Gerçek Kestrel yerine bellek içi sunucu, ama tam HTTP hattı (middleware, routing, health check).
/// Her test kendi ortamını ve bağlantı dizesini verir; paylaşılan durum yok (isolation).
/// </summary>
internal static class ApiFactory
{
    public static WebApplicationFactory<Program> Create(
        string environment,
        string? postgresConnectionString,
        Action<IServiceCollection>? configureTestServices = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                // Makinedeki ConnectionStrings__Postgres değişkeni "bağlantı yok" testlerini bozmasın:
                // boş değer ortam değişkenini ezer.
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = postgresConnectionString ?? string.Empty,
                });
            });

            if (configureTestServices is not null)
            {
                builder.ConfigureTestServices(configureTestServices);
            }
        });
    }
}

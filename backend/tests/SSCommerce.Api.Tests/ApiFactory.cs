using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SSCommerce.Api.Security;

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
        Action<IServiceCollection>? configureTestServices = null,
        IReadOnlyDictionary<string, string?>? extraConfiguration = null,
        bool enforceProductionConfiguration = false)
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

                // Testin kendi ayarları (ör. rate limit, izinli origin) sonra eklenir ve öncekilerin üzerine yazar.
                if (extraConfiguration is not null)
                {
                    config.AddInMemoryCollection(extraConfiguration);
                }
            });

            builder.ConfigureTestServices(services =>
            {
                // Faz 9 (B5): Production/Staging fail-fast denetimi (ProductionConfigurationValidator) bu yardımcıyla kurulan testlerde KAPALIDIR;
                // çoğu test "bağlantı dizesi yok", "izinli origin yok" gibi durumları bilerek Production adıyla sınar. Denetimi sınayan testler
                // enforceProductionConfiguration: true verir.
                if (!enforceProductionConfiguration)
                {
                    services.RemoveAll<IValidateOptions<ProductionStartupCheck>>();
                }

                configureTestServices?.Invoke(services);
            });
        });
    }
}

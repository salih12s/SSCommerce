using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace SSCommerce.Api.Security;

/// <summary>
/// DataProtection anahtarlarının kalıcılığı. Auth cookie'si ve antiforgery token'ları bu anahtarlarla korunur; anahtarlar
/// kalıcı değilse uygulama her yeniden başlatıldığında (ya da birden çok örnekte) TÜM oturumlar ve token'lar düşer.
/// <c>DataProtection:KeysPath</c> (ortam değişkeni <c>DataProtection__KeysPath</c>) bir dizin verir; dizin REPO DIŞINDA, yalnız uygulama
/// hesabının okuyup yazabildiği bir yerde olmalıdır (anahtarlar şifresiz XML'dir; dizine erişen herkes cookie üretebilir).
/// Boşsa: Development'ta varsayılan davranış (makineye özgü), diğer ortamlarda açılışta UYARI loglanır, uygulama düşmez.
/// </summary>
public static class DataProtectionSetup
{
    public const string ConfigurationKey = "DataProtection:KeysPath";
    public const string ApplicationName = "SSCommerce";

    public static IServiceCollection AddKeyPersistence(this IServiceCollection services)
    {
        // Uygulama adı: aynı anahtar dizinini paylaşan örnekler birbirinin cookie'sini çözebilsin (yol/ad farkına bağlı olmasın).
        services.AddDataProtection(options => options.ApplicationDiscriminator = ApplicationName);

        // Yapılandırma ÇÖZÜMLEME ANINDA okunur (son yapılandırma, ortam değişkenleri ve test geçersiz kılmaları dahil).
        services.AddOptions<KeyManagementOptions>().Configure<IConfiguration, ILoggerFactory>((options, configuration, loggerFactory) =>
        {
            var path = configuration[ConfigurationKey];
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var directory = Directory.CreateDirectory(Path.GetFullPath(path));
            options.XmlRepository = new FileSystemXmlRepository(directory, loggerFactory);
        });

        services.AddHostedService<DataProtectionConfigurationCheck>();
        return services;
    }
}

/// <summary>Açılışta, Development dışında anahtar dizini yoksa uyarır (uygulamayı DÜŞÜRMEZ).</summary>
internal sealed class DataProtectionConfigurationCheck(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<DataProtectionConfigurationCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment() && string.IsNullOrWhiteSpace(configuration[DataProtectionSetup.ConfigurationKey]))
        {
            logger.LogWarning(
                "{Key} yapılandırılmamış: DataProtection anahtarları kalıcı değil. Yeniden başlatmada tüm oturumlar ve CSRF token'ları geçersiz olur; " +
                "birden çok örnekte cookie'ler çözülemez. Repo dışında, yalnız uygulama hesabına açık bir dizin verin.",
                DataProtectionSetup.ConfigurationKey);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

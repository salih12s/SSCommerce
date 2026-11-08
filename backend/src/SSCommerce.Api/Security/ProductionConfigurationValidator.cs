using Microsoft.Extensions.Options;
using SSCommerce.Infrastructure;

namespace SSCommerce.Api.Security;

/// <summary>
/// Üretim fail-fast (Faz 9, B5). Ortam adı <c>Production</c> ya da <c>Staging</c> ise açılışta (<c>ValidateOnStart</c>) şunlar ZORUNLUDUR; biri eksikse
/// uygulama AÇILMAZ:
/// <list type="bullet">
/// <item><c>ConnectionStrings:Postgres</c> dolu.</item>
/// <item><c>Security:AllowedOrigins</c> dolu ve her öğe MUTLAK https origin (yol, sorgu, parça, kullanıcı bilgisi ve sondaki <c>/</c> YOK).
/// Development dışında olduğundan <c>http://localhost</c> da KABUL EDİLMEZ.</item>
/// <item><c>DataProtection:KeysPath</c> dolu (Development dışı ortamlarda eskiden yalnız uyarıydı).</item>
/// <item><c>Ordering:Enabled</c> AÇIKÇA <c>true</c>/<c>false</c> (sipariş verme sahip iş-riski kararıdır; sessiz varsayılan yok).</item>
/// </list>
/// Hata iletileri DEĞER içermez, yalnız yapılandırma anahtarı adı (bağlantı dizesi/origin loglara sızmaz). Development ve diğer ortam adları
/// (Testing vb.) etkilenmez. Bu kayıt <c>IValidateOptions&lt;<see cref="ProductionStartupCheck"/>&gt;</c> olarak yapılır; testler gerektiğinde onu çıkarabilir.
/// </summary>
public sealed class ProductionStartupCheck;

public sealed class ProductionConfigurationValidator(IConfiguration configuration, IHostEnvironment environment) : IValidateOptions<ProductionStartupCheck>
{
    public static bool AppliesTo(IHostEnvironment environment) => environment.IsProduction() || environment.IsStaging();

    public ValidateOptionsResult Validate(string? name, ProductionStartupCheck options)
    {
        if (!AppliesTo(environment))
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(DependencyInjection.ConnectionStringName)))
        {
            failures.Add($"{environment.EnvironmentName}: ConnectionStrings:{DependencyInjection.ConnectionStringName} zorunludur.");
        }

        var origins = configuration.GetSection($"{SecurityOptions.SectionName}:AllowedOrigins").GetChildren().Select(c => c.Value).ToList();
        if (origins.Count == 0 || origins.All(string.IsNullOrWhiteSpace))
        {
            failures.Add($"{environment.EnvironmentName}: {SecurityOptions.SectionName}:AllowedOrigins zorunludur (en az bir https origin).");
        }
        else
        {
            for (var i = 0; i < origins.Count; i++)
            {
                if (!IsHttpsOrigin(origins[i]))
                {
                    failures.Add($"{environment.EnvironmentName}: {SecurityOptions.SectionName}:AllowedOrigins:{i} mutlak bir https origin olmalıdır (yol, sorgu ve sondaki '/' olmadan).");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(configuration[DataProtectionSetup.ConfigurationKey]))
        {
            failures.Add($"{environment.EnvironmentName}: {DataProtectionSetup.ConfigurationKey} zorunludur.");
        }

        // Güvenlik denetimi B2: sipariş verme kod varsayılanı açıktır (testler/E2E). Herkese açık ortamda bu bir İŞ-RİSKİ kararıdır (D-061/D-071/D-072); sessiz varsayılana bırakılmaz,
        // operatör true/false'u AÇIKÇA yazmak zorundadır (fail-closed açılış: yazılmamışsa uygulama açılmaz).
        if (!bool.TryParse(configuration["Ordering:Enabled"], out _))
        {
            failures.Add($"{environment.EnvironmentName}: Ordering:Enabled açıkça true ya da false olarak verilmelidir (herkese açık sipariş sahip kararıdır).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Şema+host(+port) dışında hiçbir şey olmayan mutlak https adresi. Tarayıcının <c>Origin</c> başlığı da tam bu biçimdedir.</summary>
    public static bool IsHttpsOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0
            && string.Equals(value, uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }
}

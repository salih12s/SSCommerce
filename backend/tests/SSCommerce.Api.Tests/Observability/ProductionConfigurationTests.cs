using System.Net;
using Microsoft.Extensions.Options;
using SSCommerce.Api.Security;

namespace SSCommerce.Api.Tests.Observability;

/// <summary>
/// Faz 9 B5: Production/Staging'de güvensiz yapılandırma ile uygulama AÇILMAZ ("fail fast": hatayı ilk istekte değil açılışta, dağıtım sırasında yakala).
/// Her eksik ayrı sınanır; hata iletisi yalnız anahtar adını söyler, değeri (bağlantı dizesi, origin) sızdırmaz.
/// Bu testler <c>enforceProductionConfiguration: true</c> ile kurulur; <see cref="ApiFactory"/> varsayılanı denetimi kapatır (diğer testler bilerek eksik yapılandırma kullanır).
/// </summary>
public sealed class ProductionConfigurationTests : IDisposable
{
    private const string Connection = "Host=127.0.0.1;Port=1;Timeout=2;Database=x;Username=x;Password=hunter2-gizli";
    private const string Origin = "https://magaza.example";

    private readonly string _keys = Path.Combine(Path.GetTempPath(), "ssc-keys-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_keys))
        {
            Directory.Delete(_keys, recursive: true);
        }
    }

    private Dictionary<string, string?> Valid() => new()
    {
        ["Security:AllowedOrigins:0"] = Origin,
        ["DataProtection:KeysPath"] = _keys,
        ["Ordering:Enabled"] = "false",
    };

    private static Exception Startup(string environment, string? connection, Dictionary<string, string?> config)
    {
        using var factory = ApiFactory.Create(environment, connection, extraConfiguration: config, enforceProductionConfiguration: true);
        return Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    /// <summary>İstisna zincirindeki tüm iletiler (OptionsValidationException'ın iletisi hatalarını taşır).</summary>
    private static string Chain(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join('\n', parts);
    }

    private static void AssertRefused(Exception exception, string expectedKey)
    {
        var text = Chain(exception);
        Assert.Contains(nameof(OptionsValidationException), text);
        Assert.Contains(expectedKey, text);
        // Değer sızıntısı yok.
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("Password", text);
        Assert.DoesNotContain("127.0.0.1", text);
        Assert.DoesNotContain("magaza.example", text);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task A_complete_configuration_starts_the_app(string environment)
    {
        await using var factory = ApiFactory.Create(environment, Connection, extraConfiguration: Valid(), enforceProductionConfiguration: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void A_missing_connection_string_is_refused(string environment) =>
        AssertRefused(Startup(environment, connection: null, Valid()), "ConnectionStrings:Postgres");

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void A_whitespace_connection_string_is_refused(string environment) =>
        AssertRefused(Startup(environment, connection: "   ", Valid()), "ConnectionStrings:Postgres");

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Missing_allowed_origins_are_refused(string environment)
    {
        var config = Valid();
        config.Remove("Security:AllowedOrigins:0");

        AssertRefused(Startup(environment, Connection, config), "Security:AllowedOrigins");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void A_missing_ordering_switch_is_refused(string environment)
    {
        var config = Valid();
        config.Remove("Ordering:Enabled");

        AssertRefused(Startup(environment, Connection, config), "Ordering:Enabled");
    }

    [Theory]
    [InlineData("")]
    [InlineData("evet")]
    [InlineData("1")]
    public void A_non_boolean_ordering_switch_is_refused(string value)
    {
        var config = Valid();
        config["Ordering:Enabled"] = value;

        // Bool olmayan değerde yapılandırma bağlayıcısı OptionsValidationException yerine InvalidOperationException ("Failed to convert configuration value")
        // fırlatabilir; ikisi de AÇILMAYI reddeder. Sözleşme: uygulama açılmaz, ileti anahtar adını söyler, bağlantı dizesi sırrı sızmaz.
        var text = Chain(Startup("Production", Connection, config));

        Assert.Contains("Ordering:Enabled", text);
        // Bağlayıcının iletisi geçersiz bool değerini yazabilir; bu sır değildir. Sızmaması gereken şey bağlantı dizesi parolasıdır.
        Assert.DoesNotContain("hunter2", text);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task An_explicit_ordering_decision_either_way_starts_the_app(string value)
    {
        var config = Valid();
        config["Ordering:Enabled"] = value;
        await using var factory = ApiFactory.Create("Production", Connection, extraConfiguration: config, enforceProductionConfiguration: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("http://magaza.example")]            // https değil
    [InlineData("http://localhost:3000")]            // Development dışında localhost http kabul edilmez
    [InlineData("https://magaza.example/")]          // sondaki '/' (Origin başlığı böyle gelmez)
    [InlineData("https://magaza.example/giris")]     // yol
    [InlineData("https://magaza.example?x=1")]       // sorgu
    [InlineData("https://magaza.example#parca")]     // parça
    [InlineData("https://kullanici@magaza.example")] // kullanıcı bilgisi
    [InlineData("magaza.example")]                   // mutlak değil
    [InlineData("//magaza.example")]
    [InlineData("ftp://magaza.example")]
    [InlineData("  ")]
    public void An_origin_that_is_not_an_absolute_https_origin_is_refused(string origin)
    {
        var config = Valid();
        config["Security:AllowedOrigins:0"] = origin;

        var text = Chain(Startup("Production", Connection, config));

        Assert.Contains(nameof(OptionsValidationException), text);
        Assert.Contains("Security:AllowedOrigins", text);
        Assert.DoesNotContain("hunter2", text);
        if (!string.IsNullOrWhiteSpace(origin))
        {
            Assert.DoesNotContain(origin, text); // yapılandırma değeri iletide YOK
        }
    }

    [Fact]
    public void One_bad_origin_among_good_ones_is_refused_and_named_by_index_only()
    {
        var config = Valid();
        config["Security:AllowedOrigins:1"] = "http://yanlis.example";

        var text = Chain(Startup("Production", Connection, config));

        Assert.Contains("Security:AllowedOrigins:1", text);
        Assert.DoesNotContain("yanlis.example", text);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void A_missing_data_protection_keys_path_is_refused(string environment)
    {
        var config = Valid();
        config.Remove("DataProtection:KeysPath");

        AssertRefused(Startup(environment, Connection, config), "DataProtection:KeysPath");
    }

    [Fact]
    public void Every_problem_is_reported_together_in_one_failure()
    {
        var text = Chain(Startup("Production", connection: null, new Dictionary<string, string?>()));

        Assert.Contains("ConnectionStrings:Postgres", text);
        Assert.Contains("Security:AllowedOrigins", text);
        Assert.Contains("DataProtection:KeysPath", text);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Qa")]
    public async Task Other_environments_are_not_affected_even_with_nothing_configured(string environment)
    {
        await using var factory = ApiFactory.Create(environment, postgresConnectionString: null, extraConfiguration: new Dictionary<string, string?>(), enforceProductionConfiguration: true);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("https://magaza.example", true)]
    [InlineData("https://magaza.example:8443", true)]
    [InlineData("HTTPS://Magaza.Example", true)]
    [InlineData("https://magaza.example:443", false)] // varsayılan port yazılırsa tarayıcı Origin'i ile eşleşmez
    [InlineData("http://magaza.example", false)]
    [InlineData("https://magaza.example/", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" https://magaza.example", false)]
    public void Origin_shape_rules(string? origin, bool valid) => Assert.Equal(valid, ProductionConfigurationValidator.IsHttpsOrigin(origin));
}

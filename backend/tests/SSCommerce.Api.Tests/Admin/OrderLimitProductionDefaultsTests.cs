using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SSCommerce.Api.Tests.Inventory;
using SSCommerce.Application.Orders;

namespace SSCommerce.Api.Tests.Admin;

/// <summary>
/// INTEGRATION / yapılandırma testleri: sipariş sınırlarının ÜRÜN varsayılanları (günlük 10, açık 3 — docs/CONTRACT_PHASE_8.md "Unpaid-order limits (F8.5)")
/// üretim API'sinde değişmedi mi? Playwright'ın <c>E2E_MAX_*</c> yükseltmesi yalnız E2eHost alt sürecine ait bir ortam değişkenidir (bkz. tools/SSCommerce.E2eHost/Program.cs);
/// üretim API'si o değişken adlarını hiç bilmez. Neden test: sınırı bir "geçici ayar" sessizce gevşetirse kimse fark etmez; bu test kırmızı olur.
/// </summary>
[Collection(AdminApiCollection.Name)]
public class OrderLimitProductionDefaultsTests(InventoryApiFixture fixture)
{
    private static string BackendRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SSCommerce.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new FileNotFoundException("backend/SSCommerce.slnx bulunamadı.");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task The_running_api_resolves_the_contract_defaults_10_per_day_and_3_open_when_nothing_overrides_them(string environment)
    {
        await using var factory = ApiFactory.Create(
            environment,
            fixture.Database.ConnectionString,
            extraConfiguration: new Dictionary<string, string?>(InventoryApiFixture.HighLimits) { ["Security:AllowedOrigins:0"] = InventoryApiFixture.ProductionOrigin });
        using var client = factory.CreateClient(); // ana bilgisayarı başlatır (ValidateOnStart dahil)

        var options = factory.Services.GetRequiredService<IOptions<OrderLimitOptions>>().Value;

        Assert.Equal(10, options.MaxOrdersPerRollingDay);
        Assert.Equal(3, options.MaxOpenOrdersPerUser);
        Assert.Equal(OrderLimitOptions.DefaultMaxOrdersPerRollingDay, options.MaxOrdersPerRollingDay);
        Assert.Equal(OrderLimitOptions.DefaultMaxOpenOrdersPerUser, options.MaxOpenOrdersPerUser);
        Assert.Null(options.Validate());
    }

    [Fact]
    public void No_appsettings_file_of_the_api_overrides_the_ordering_section()
    {
        var apiDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
        var files = Directory.GetFiles(apiDirectory, "appsettings*.json");

        Assert.NotEmpty(files); // dosyalar gerçekten bulundu
        foreach (var file in files)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            Assert.False(json.RootElement.TryGetProperty(OrderLimitOptions.SectionName, out _), $"{Path.GetFileName(file)} 'Ordering' bölümü içermemeli.");
        }
    }

    [Fact]
    public void The_production_source_does_not_know_the_e2e_override_variable_names()
    {
        var root = BackendRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("E2E_MAX_", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        Assert.Empty(offenders);
        // Yükseltme gerçekten yalnız E2eHost'ta yaşar (aksi hâlde bu test yanlış yeri tarıyor olurdu).
        Assert.Contains("E2E_MAX_ORDERS_PER_DAY", File.ReadAllText(Path.Combine(root, "tools", "SSCommerce.E2eHost", "Program.cs")), StringComparison.Ordinal);
    }
}

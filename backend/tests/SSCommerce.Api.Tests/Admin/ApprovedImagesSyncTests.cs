using System.Text.RegularExpressions;
using SSCommerce.Application.Catalog;

namespace SSCommerce.Api.Tests.Admin;

/// <summary>
/// Senkron (CONTRACT) testi: sunucudaki onaylı görsel listesi == web'deki <c>image-credits.ts</c> ürün kayıtları == diskteki <c>public/images/urun</c> dosyaları.
/// Neden: admin yalnız bu listedeki yerel dosyaları seçebilir; liste, yazar atfı ya da dosya biri değişip öbürleri unutulursa kırık görsel ya da
/// atıfsız görsel yayınlanırdı. Bu test o üçlünün birlikte değişmesini zorlar (unit düzeyi: dosya okur, veritabanı gerekmez).
/// </summary>
public partial class ApprovedImagesSyncTests
{
    [GeneratedRegex("""credit\(\s*"(/images/urun/[a-z0-9-]+\.webp)",\s*"[^"]*",\s*"[^"]*",\s*"([^"]*)",""", RegexOptions.Singleline)]
    private static partial Regex CreditRegex();

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend", "SSCommerce.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
    }

    [Fact]
    public void The_server_allowlist_equals_the_web_credit_entries_and_the_files_on_disk()
    {
        var root = RepositoryRoot();
        var credits = CreditRegex().Matches(File.ReadAllText(Path.Combine(root, "apps", "web", "src", "content", "image-credits.ts")))
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
        var disk = Directory.EnumerateFiles(Path.Combine(root, "apps", "web", "public", "images", "urun"), "*.webp")
            .Select(path => "/images/urun/" + Path.GetFileName(path))
            .ToHashSet(StringComparer.Ordinal);
        var allowlist = ApprovedProductImages.All.Select(a => a.StorageKey).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(allowlist);
        Assert.Equal(12, ApprovedProductImages.All.Count);
        Assert.Equal(allowlist.Order(StringComparer.Ordinal), credits.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(allowlist.Order(StringComparer.Ordinal), disk.Order(StringComparer.Ordinal));
        Assert.All(ApprovedProductImages.All, asset =>
        {
            Assert.Equal(asset.StorageKey, asset.CreditKey);
            Assert.Equal(credits[asset.StorageKey], asset.Author);
        });
    }

    [Theory]
    [InlineData("/images/urun/klavye.webp", true)]
    [InlineData("/images/urun/KLAVYE.webp", false)]
    [InlineData("/images/urun/klavye.webp ", false)]
    [InlineData(" /images/urun/klavye.webp", false)]
    [InlineData("/images/urun/klavye.png", false)]
    [InlineData("/images/hero/masa-ustu-teknoloji.webp", false)]
    [InlineData("https://images.example/klavye.webp", false)]
    [InlineData("/images/urun/../hero/masa-ustu-teknoloji.webp", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_exact_matches_of_the_allowlist_are_approved(string? key, bool approved)
    {
        Assert.Equal(approved, ApprovedProductImages.TryGet(key, out _));
    }
}

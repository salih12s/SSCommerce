using System.Text.RegularExpressions;

namespace SSCommerce.Domain.Catalog;

/// <summary>
/// Katalog doğrulama kuralları. Aynı kurallar veritabanında CHECK olarak da vardır (savunma derinliği);
/// bir kuralı değiştirirsen EF konfigürasyonundaki CHECK'i de değiştir.
/// </summary>
public static partial class CatalogRules
{
    public const int CategoryNameMaxLength = 80;
    public const int CategorySlugMaxLength = 120;
    public const int ProductNameMaxLength = 160;
    public const int ProductSlugMaxLength = 160;
    public const int BrandMaxLength = 80;
    public const int DescriptionMaxLength = 4000;
    public const int SkuMinLength = 3;
    public const int SkuMaxLength = 40;
    public const int DisplayNameMaxLength = 160;
    public const int OptionSignatureMaxLength = 400;
    public const int StorageKeyMaxLength = 300;
    public const int AltTextMaxLength = 200;
    public const int CreditKeyMaxLength = 80;

    public const int MaxOptionCount = 10;
    public const int OptionKeyMaxLength = 20;
    public const int OptionValueMaxLength = 30;

    public const int MaxSpecificationCount = 50;
    public const int SpecificationKeyMaxLength = 60;
    public const int SpecificationValueMaxLength = 300;

    /// <summary>Veritabanındaki jsonb boyut sınırları (pg_column_size, bayt).</summary>
    public const int OptionsMaxBytes = 2048;
    public const int SpecificationsMaxBytes = 16384;

    public const string SlugPattern = "^[a-z0-9]+(-[a-z0-9]+)*$";
    public const string SkuPattern = "^[A-Z0-9]+(-[A-Z0-9]+)*$";

    // Yalnızca /images/ altında, küçük harf/rakam/_/- segmentleri ve zorunlu uzantı. ".." ve "//" yapısal olarak imkânsız.
    public const string ImagePathPattern = "^/images/[a-z0-9_-]+(/[a-z0-9_-]+)*\\.[a-z0-9]{2,5}$";

    // .NET'te "$" sondaki satır sonu karakterinden önce de eşleşir ("abc" + LF kabul edilirdi); "\z" yalnız metnin gerçek sonudur.
    // Veritabanı CHECK'leri (PostgreSQL "~") "$" ile zaten tam sonu ister: *Pattern sabitleri DEĞİŞMEZ (migration'daki SQL ile aynı kalmalı),
    // yalnız .NET regex'i sıkılaştırılır.
    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\z")]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"^[A-Z0-9]+(-[A-Z0-9]+)*\z")]
    private static partial Regex SkuRegex();

    [GeneratedRegex(@"^/images/[a-z0-9_-]+(/[a-z0-9_-]+)*\.[a-z0-9]{2,5}\z")]
    private static partial Regex ImagePathRegex();

    public static string RequireSlug(string? value, int maxLength, string what)
    {
        if (value is null || value.Length == 0 || value.Length > maxLength || !SlugRegex().IsMatch(value))
        {
            throw new CatalogRuleException($"{what}: geçersiz slug (küçük harf, rakam ve tire; en çok {maxLength} karakter).");
        }

        return value;
    }

    public static string RequireSku(string? value)
    {
        if (value is null || value.Length < SkuMinLength || value.Length > SkuMaxLength || !SkuRegex().IsMatch(value))
        {
            throw new CatalogRuleException($"SKU geçersiz (büyük harf, rakam ve tire; {SkuMinLength}-{SkuMaxLength} karakter).");
        }

        return value;
    }

    public static string RequireImagePath(string? value, int maxLength, string what)
    {
        if (value is null || value.Length > maxLength || !ImagePathRegex().IsMatch(value))
        {
            throw new CatalogRuleException($"{what}: geçersiz görsel yolu (/images/... küçük harfli güvenli yol, uzantılı).");
        }

        return value;
    }

    public static string RequireText(string? value, int maxLength, string what)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > maxLength)
        {
            throw new CatalogRuleException($"{what}: boş olamaz, en çok {maxLength} karakter.");
        }

        return trimmed;
    }

    public static string? OptionalText(string? value, int maxLength, string what)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            throw new CatalogRuleException($"{what}: en çok {maxLength} karakter.");
        }

        return trimmed;
    }
}

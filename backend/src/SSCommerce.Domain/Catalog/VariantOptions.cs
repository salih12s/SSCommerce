using System.Text.RegularExpressions;

namespace SSCommerce.Domain.Catalog;

/// <summary>
/// Varyant seçenekleri (renk, kapasite vb.) ve onlardan türeyen deterministik imza.
/// Anahtar ve değerler normalize edilir (kırp + küçük harf) ve ASCII slug biçiminde olmalıdır; Türkçe gösterim
/// <c>display_name</c>'in işidir. İmza: anahtarlar sıralı <c>hafiza=256gb;renk=siyah</c>; seçeneksiz varyant için <c>default</c>.
/// Aynı üründe aynı imza iki kez bulunamaz (DB UNIQUE).
/// </summary>
public sealed partial class VariantOptions
{
    public const string DefaultSignature = "default";

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\z")]
    private static partial Regex TokenRegex();

    private VariantOptions(SortedDictionary<string, string> values, string signature)
    {
        Values = values;
        Signature = signature;
    }

    public IReadOnlyDictionary<string, string> Values { get; }

    public string Signature { get; }

    public static VariantOptions None { get; } = new(new SortedDictionary<string, string>(StringComparer.Ordinal), DefaultSignature);

    public static VariantOptions Create(IEnumerable<KeyValuePair<string, string>>? options)
    {
        if (options is null)
        {
            return None;
        }

        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawKey, rawValue) in options)
        {
            var key = Normalize(rawKey, CatalogRules.OptionKeyMaxLength, "Seçenek anahtarı");
            var value = Normalize(rawValue, CatalogRules.OptionValueMaxLength, $"Seçenek değeri ({key})");
            if (!sorted.TryAdd(key, value))
            {
                throw new CatalogRuleException($"Seçenek anahtarı tekrar ediyor: {key}.");
            }

            if (sorted.Count > CatalogRules.MaxOptionCount)
            {
                throw new CatalogRuleException($"En çok {CatalogRules.MaxOptionCount} seçenek olabilir.");
            }
        }

        if (sorted.Count == 0)
        {
            return None;
        }

        var signature = string.Join(';', sorted.Select(kv => $"{kv.Key}={kv.Value}"));
        if (signature.Length > CatalogRules.OptionSignatureMaxLength)
        {
            throw new CatalogRuleException("Seçenek imzası çok uzun.");
        }

        return new VariantOptions(sorted, signature);
    }

    private static string Normalize(string? raw, int maxLength, string what)
    {
        var token = raw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(token) || token.Length > maxLength || !TokenRegex().IsMatch(token))
        {
            throw new CatalogRuleException(
                $"{what} geçersiz (küçük harf ASCII, rakam ve tire; en çok {maxLength} karakter).");
        }

        return token;
    }
}

using System.Text.RegularExpressions;

namespace SSCommerce.Domain.Customers;

/// <summary>
/// Teslimat adresi kuralları (D-052): Türkiye'ye özel, yalnız teslimat için gereken alanlar. Telefon ve posta kodu biçimi
/// veritabanında CHECK olarak da vardır (savunma derinliği). İl listesi yalnız uygulamada denetlenir (DB CHECK yok).
/// </summary>
public static partial class AddressRules
{
    public const int MaxAddressesPerUser = 10;

    public const int LabelMaxLength = 50;
    public const int RecipientNameMaxLength = 100;
    public const int ProvinceMaxLength = 32;
    public const int DistrictMaxLength = 64;
    public const int StreetAddressMaxLength = 300;
    public const int PhoneLength = 10;
    public const int PostalCodeLength = 5;

    /// <summary>Normalize telefon: ülke ve trunk öneki olmadan 10 hane, cep numarası (5 ile başlar).</summary>
    public const string PhonePattern = "^5[0-9]{9}$";

    /// <summary>İlk iki hane plaka kodu (01-81), ardından üç hane.</summary>
    public const string PostalCodePattern = "^(0[1-9]|[1-7][0-9]|8[01])[0-9]{3}$";

    /// <summary>81 il, resmî Türkçe yazımıyla (yaklaşık alfabetik; sıra anlamsızdır). Karşılaştırma ordinal ve büyük/küçük harfe duyarlıdır.</summary>
    public static readonly IReadOnlyList<string> Provinces =
    [
        "Adana", "Adıyaman", "Afyonkarahisar", "Ağrı", "Aksaray", "Amasya", "Ankara", "Antalya", "Ardahan", "Artvin",
        "Aydın", "Balıkesir", "Bartın", "Batman", "Bayburt", "Bilecik", "Bingöl", "Bitlis", "Bolu", "Burdur",
        "Bursa", "Çanakkale", "Çankırı", "Çorum", "Denizli", "Diyarbakır", "Düzce", "Edirne", "Elazığ", "Erzincan",
        "Erzurum", "Eskişehir", "Gaziantep", "Giresun", "Gümüşhane", "Hakkâri", "Hatay", "Iğdır", "Isparta", "İstanbul",
        "İzmir", "Kahramanmaraş", "Karabük", "Karaman", "Kars", "Kastamonu", "Kayseri", "Kilis", "Kırıkkale", "Kırklareli",
        "Kırşehir", "Kocaeli", "Konya", "Kütahya", "Malatya", "Manisa", "Mardin", "Mersin", "Muğla", "Muş",
        "Nevşehir", "Niğde", "Ordu", "Osmaniye", "Rize", "Sakarya", "Samsun", "Şanlıurfa", "Siirt", "Sinop",
        "Sivas", "Şırnak", "Tekirdağ", "Tokat", "Trabzon", "Tunceli", "Uşak", "Van", "Yalova", "Yozgat",
        "Zonguldak",
    ];

    private static readonly HashSet<string> ProvinceSet = new(Provinces, StringComparer.Ordinal);

    // .NET'te "$" sondaki satır sonundan önce de eşleşir; "\z" yalnız metnin gerçek sonudur. DB CHECK'i (PostgreSQL "~") "$" ile tam sonu ister:
    // *Pattern sabitleri DEĞİŞMEZ, yalnız .NET regex'i sıkılaştırılır.
    [GeneratedRegex(@"^5[0-9]{9}\z")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"^(0[1-9]|[1-7][0-9]|8[01])[0-9]{3}\z")]
    private static partial Regex PostalCodeRegex();

    /// <summary>
    /// Kullanıcı girdisini <c>5XXXXXXXXX</c> biçimine getirir: boşluklar atılır, başta <c>+90</c> ya da <c>0</c> öneki kaldırılır.
    /// Kabul: <c>05321234567</c>, <c>+905321234567</c>, <c>0 532 123 45 67</c>, <c>5321234567</c>. Başka her şey reddedilir.
    /// </summary>
    public static string NormalizePhone(string? value)
    {
        var compact = value is null ? string.Empty : value.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (compact.StartsWith("+90", StringComparison.Ordinal))
        {
            compact = compact[3..];
        }
        else if (compact.StartsWith('0'))
        {
            compact = compact[1..];
        }

        if (!PhoneRegex().IsMatch(compact))
        {
            throw new AddressRuleException("Telefon numarası geçersiz (5XXXXXXXXX biçiminde cep telefonu).");
        }

        return compact;
    }

    public static string RequireProvince(string? value)
    {
        var trimmed = value?.Trim();
        if (trimmed is null || !ProvinceSet.Contains(trimmed))
        {
            throw new AddressRuleException("İl geçersiz (81 ilden biri olmalı).");
        }

        return trimmed;
    }

    /// <summary>Boş ya da <c>null</c> → <c>null</c> (posta kodu isteğe bağlı). Doluysa 5 hane ve ilk iki hane 01-81.</summary>
    public static string? OptionalPostalCode(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (!PostalCodeRegex().IsMatch(trimmed))
        {
            throw new AddressRuleException("Posta kodu geçersiz (5 hane, ilk iki hane 01-81).");
        }

        return trimmed;
    }

    public static string RequireText(string? value, int maxLength, string what)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > maxLength)
        {
            throw new AddressRuleException($"{what}: boş olamaz, en çok {maxLength} karakter.");
        }

        // HAM değer denetlenir: Trim() U+2028/U+2029 gibi ayırıcıları kenardan sessizce atardı.
        if (ContainsUnsafeCharacter(value))
        {
            throw new AddressRuleException($"{what}: kontrol, biçim ya da satır ayırıcı karakter içeremez.");
        }

        return trimmed;
    }

    /// <summary>
    /// Serbest metinde yasak karakter var mı? Unicode kategorileri: Cc (kontrol, NUL dahil), Cf (biçim: U+202E, U+200B, U+FEFF...),
    /// Zl (U+2028), Zp (U+2029). Sıradan harf/rakam/noktalama, bitişik işaretler, NBSP ve emoji yasak DEĞİLDİR.
    /// Application'daki <c>TextRules</c> bunu çağırır; iki tarafın aynı kararı verdiği testle sabitlidir.
    /// </summary>
    public static bool ContainsUnsafeCharacter(string? value)
    {
        if (value is null)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator)
            {
                return true;
            }
        }

        return false;
    }
}

namespace SSCommerce.Application.Catalog;

/// <summary>
/// Slug biçim denetimi (<c>^[a-z0-9]+(-[a-z0-9]+)*$</c>, D-026). Regex yerine elle yazıldı: .NET'te <c>$</c> sondaki "\n"
/// karakterini de kabul eder, burada kabul edilmez. Biçimi bozuk değer veritabanına hiç gitmez (NUL gibi karakterler Npgsql'de hata verir).
/// </summary>
public static class CatalogSlug
{
    public const int CategoryMaxLength = 120;
    public const int ProductMaxLength = 160;

    public static bool IsWellFormed(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
        {
            return false;
        }

        var previousWasHyphen = true; // başta tire olamaz
        foreach (var ch in value)
        {
            if (ch == '-')
            {
                if (previousWasHyphen)
                {
                    return false; // başta ya da ardışık tire
                }

                previousWasHyphen = true;
            }
            else if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                previousWasHyphen = false;
            }
            else
            {
                return false;
            }
        }

        return !previousWasHyphen; // sonda tire olamaz
    }
}

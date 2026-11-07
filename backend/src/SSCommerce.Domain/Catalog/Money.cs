namespace SSCommerce.Domain.Catalog;

/// <summary>
/// Para dönüşümü (D-004): domain <c>decimal</c> (TL), API <c>long</c> kuruş (<c>...Minor</c>), DB <c>numeric(18,2)</c>.
/// Saf yardımcıdır: yuvarlama YAPMAZ. Kuruştan küçük kesir, negatif tutar ya da sütuna sığmayan değer hata verir.
/// </summary>
public static class Money
{
    public const string Currency = "TRY";

    /// <summary>
    /// En büyük tutar: kuruş karşılığı 2^53 - 1 = 9007199254740991 (JavaScript'in güvenli tam sayı sınırı, GOAL §3), yani
    /// 90.071.992.547.409,91 TL. API'deki <c>...Minor</c> alanları TypeScript'te <c>number</c> olarak kayıpsız okunabilsin diye
    /// sınır DB sütununun (numeric(18,2), ~10^16 TL) altındadır; DB CHECK/sütun bilinçli olarak değişmedi (daha geniş = güvenli yedek).
    /// </summary>
    public const decimal MaxAmount = 90_071_992_547_409.91m;

    /// <summary>Admin yazmalarında kabul edilen en büyük kuruş tutarı: 2^53 - 1 (JavaScript güvenli tam sayı sınırı).</summary>
    public const long MaxMinor = 9_007_199_254_740_991L;

    /// <summary>
    /// Admin fiyat yazması (Faz 8): <c>priceMinor</c> 1..<see cref="MaxMinor"/>. Sıfır ve negatif reddedilir (var olan sıfır fiyatlar okunabilir kalır,
    /// ama yeni yazma sıfır fiyat üretmez). Dönen değer TL (ölçek 2).
    /// </summary>
    public static decimal FromAdminPriceMinor(long minor)
    {
        if (minor < 1 || minor > MaxMinor)
        {
            throw new CatalogRuleException($"Fiyat (kuruş) 1 ile {MaxMinor} arasında olmalıdır.");
        }

        return FromMinor(minor);
    }

    /// <summary>TL → kuruş. Örn. 42999.00m → 4299900. 1.234m (kuruştan küçük kesir) → hata. 1.500m kabul edilir (değer 2 basamağa sığıyor).</summary>
    public static long ToMinor(decimal amount)
    {
        EnsureValid(amount);
        return decimal.ToInt64(amount * 100m);
    }

    /// <summary>Kuruş → TL, ölçek her zaman 2 (4299900 → 42999.00m).</summary>
    public static decimal FromMinor(long minor)
    {
        if (minor < 0)
        {
            throw new CatalogRuleException("Tutar negatif olamaz.");
        }

        // long ölçeği 0, 0.01m ölçeği 2: çarpım tam ve ölçek 2 kalır.
        var amount = minor * 0.01m;
        EnsureValid(amount);
        return amount;
    }

    /// <summary>Fiyat kuralı: 0 ≤ tutar ≤ <see cref="MaxAmount"/>, en çok 2 ondalık basamak.</summary>
    public static decimal EnsureValid(decimal amount)
    {
        if (amount < 0)
        {
            throw new CatalogRuleException("Tutar negatif olamaz.");
        }

        if (amount > MaxAmount)
        {
            throw new CatalogRuleException("Tutar izin verilen üst sınırı aşıyor.");
        }

        if (decimal.Round(amount, 2) != amount)
        {
            throw new CatalogRuleException("Tutar en çok 2 ondalık basamak (kuruş) içerebilir.");
        }

        return amount;
    }
}

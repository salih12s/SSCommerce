namespace SSCommerce.Application.Catalog;

/// <summary>
/// Admin'in ürün görseli olarak seçebileceği ONAYLI yerel varlıklar (Faz 8, D-069). Yükleme ve dış adres yoktur: yalnız bu liste.
/// Liste web uygulamasındaki <c>apps/web/src/content/image-credits.ts</c> kaydı (yazar/lisans) ve <c>public/images/urun/</c> dosyalarıyla
/// BİRLİKTE değişir; bir test üçünün aynı kümeyi gösterdiğini doğrular. <c>CreditKey</c> kaydın <c>file</c> alanıdır (storage key ile aynı yol).
/// </summary>
public static class ApprovedProductImages
{
    public sealed record Asset(string StorageKey, string CreditKey, string Author);

    public static readonly IReadOnlyList<Asset> All =
    [
        Of("/images/urun/dizustu.webp", "Kari Shea"),
        Of("/images/urun/monitor.webp", "Mediamodifier"),
        Of("/images/urun/telefon.webp", "2H Media"),
        Of("/images/urun/kulaklik.webp", "Jason Leung"),
        Of("/images/urun/hoparlor.webp", "Jonas Jacobsson"),
        Of("/images/urun/kulakici.webp", "Yasin Hasan"),
        Of("/images/urun/akilli-ampul.webp", "Ian Talmacs"),
        Of("/images/urun/ic-mekan-kamera.webp", "Jakub Żerdzicki"),
        Of("/images/urun/klavye.webp", "Martin Garrido"),
        Of("/images/urun/kablosuz-sarj.webp", "Matteo Grobberio"),
        Of("/images/urun/sarj-adaptoru.webp", "Markus Winkler"),
        Of("/images/urun/sirt-cantasi.webp", "Marina Zaharkina"),
    ];

    private static readonly Dictionary<string, Asset> ByKey = All.ToDictionary(a => a.StorageKey, StringComparer.Ordinal);

    /// <summary>Tam eşleşme (büyük/küçük harf, boşluk ve kodlanmış biçimler eşleşmez).</summary>
    public static bool TryGet(string? storageKey, out Asset asset)
    {
        if (storageKey is not null && ByKey.TryGetValue(storageKey, out var found))
        {
            asset = found;
            return true;
        }

        asset = null!;
        return false;
    }

    private static Asset Of(string file, string author) => new(file, file, author);
}

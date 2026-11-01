namespace SSCommerce.Infrastructure.Persistence.Seed;

/// <summary>
/// Demo katalog içeriği (F3.3). TÜM ürünler kurgusaldır ve <c>is_demo=true</c> olarak yazılır; marka alanı boştur.
/// Gerçek marka/model, indirim, stok ya da garanti vaadi içermez.
/// Kategori slug/adları, fiyatlar ve görsel yolları <c>apps/web/src/content/demo-catalog.ts</c> ile hizalıdır
/// (ürün <c>code</c> değeri SKU ön eki olur: SSC-3001 → SSC-3001-STD). Görsel kaynakları <c>image-credits.ts</c> içindedir;
/// <c>CreditKey</c> o kaydın <c>file</c> alanıdır.
/// Görsel kuralı: yalnızca fotoğrafın GERÇEKTEN temsil ettiği ürüne görsel bağlanır. Uygun fotoğrafı olmayan ürünler (fare, mouse pad)
/// görselsiz kalır; vitrin onlar için kategori görselini kullanabilir (karar frontend'e aittir).
/// </summary>
internal static class DemoCatalogData
{
    private const string DemoNote = "Katalog gösterimi için kurgusal örnek üründür; gerçek bir ürün değildir.";

    internal sealed record DemoImage(string Path, string AltText);

    internal sealed record DemoVariant(string Sku, string DisplayName, decimal Price, IReadOnlyDictionary<string, string>? Options = null);

    internal sealed record DemoProduct(
        string Slug,
        string Name,
        string Description,
        IReadOnlyDictionary<string, string> Specifications,
        IReadOnlyList<DemoVariant> Variants,
        DemoImage? Image = null);

    internal sealed record DemoCategory(string Slug, string Name, int SortOrder, IReadOnlyList<DemoProduct> Products);

    private static Dictionary<string, string> Opt(string key, string value) => new() { [key] = value };

    private static Dictionary<string, string> Spec(string type) => new() { ["Tür"] = type, ["Veri durumu"] = "Demo" };

    public static IReadOnlyList<DemoCategory> Categories { get; } =
    [
        new("ses-sistemleri", "Ses Sistemleri", 10,
        [
            new("ton-kulakustu-kulaklik", "Ton Kulaküstü Kulaklık",
                $"Kulak üstü tasarımlı kulaklık. {DemoNote}",
                Spec("Kulaküstü kulaklık"),
                [
                    new("SSC-3001-STD", "Ton Kulaküstü Kulaklık - Standart paket", 3890.00m, Opt("paket", "standart")),
                    new("SSC-3001-SEY", "Ton Kulaküstü Kulaklık - Seyahat kılıflı paket", 4190.00m, Opt("paket", "seyahat-kilifli")),
                ],
                new("/images/urun/kulaklik.webp", "Siyah zeminde duran siyah kulaküstü kulaklık")),
            new("cember-tasinabilir-hoparlor", "Çember Taşınabilir Hoparlör",
                $"Taşınabilir boyutta hoparlör. {DemoNote}",
                Spec("Taşınabilir hoparlör"),
                [
                    new("SSC-3002-S", "Çember Taşınabilir Hoparlör - Küçük boy", 2190.00m, Opt("boy", "kucuk")),
                    new("SSC-3002-B", "Çember Taşınabilir Hoparlör - Büyük boy", 2990.00m, Opt("boy", "buyuk")),
                ],
                new("/images/urun/hoparlor.webp", "Pencere önünde duran, gözenekli ızgaralı siyah ve gri bir hoparlör")),
            new("nokta-kablosuz-kulakici", "Nokta Kablosuz Kulakiçi",
                $"Şarj kutulu kulakiçi kulaklık. {DemoNote}",
                Spec("Kulakiçi kulaklık"),
                [new("SSC-3003", "Nokta Kablosuz Kulakiçi", 1499.00m)],
                new("/images/urun/kulakici.webp", "Açık yeşil zeminde, kapağı açık şarj kutusunda duran siyah kablosuz kulaklıklar")),
        ]),
        new("akilli-yasam", "Akıllı Yaşam", 20,
        [
            new("isik-akilli-ampul", "Işık Akıllı Ampul",
                $"Akıllı ev kurulumları için ampul. {DemoNote}",
                Spec("Akıllı ampul"),
                [
                    new("SSC-4001-1", "Işık Akıllı Ampul - Tekli paket", 349.00m, Opt("paket", "tekli")),
                    new("SSC-4001-3", "Işık Akıllı Ampul - Üçlü paket", 949.00m, Opt("paket", "uclu")),
                ],
                new("/images/urun/akilli-ampul.webp", "Beyaz zeminde yan yatan beyaz bir ampul")),
            new("goz-ev-ici-kamera", "Göz Ev İçi Kamera",
                $"Dönebilen başlıklı ev içi kamera. {DemoNote}",
                Spec("Ev içi kamera"),
                [new("SSC-4002", "Göz Ev İçi Kamera", 1299.00m)],
                new("/images/urun/ic-mekan-kamera.webp", "Beyaz zeminde duran, dönebilen başlıklı beyaz bir ev içi kamera")),
        ]),
        new("aksesuarlar", "Aksesuarlar", 30,
        [
            new("disk-kablosuz-sarj-pedi", "Disk Kablosuz Şarj Pedi",
                $"Yuvarlak kablosuz şarj pedi. {DemoNote}",
                Spec("Kablosuz şarj pedi"),
                [
                    new("SSC-6001-10", "Disk Kablosuz Şarj Pedi - 10 W", 799.00m, Opt("guc", "10w")),
                    new("SSC-6001-15", "Disk Kablosuz Şarj Pedi - 15 W", 999.00m, Opt("guc", "15w")),
                ],
                new("/images/urun/kablosuz-sarj.webp", "Ahşap yüzeyde duran, kablosu bağlı beyaz yuvarlak şarj pedi")),
            new("hizli-sarj-adaptoru", "Hızlı Şarj Adaptörü",
                $"Fişli şarj adaptörü. {DemoNote}",
                Spec("Şarj adaptörü"),
                [
                    new("SSC-6002-30", "Hızlı Şarj Adaptörü - 30 W", 549.00m, Opt("guc", "30w")),
                    new("SSC-6002-65", "Hızlı Şarj Adaptörü - 65 W", 899.00m, Opt("guc", "65w")),
                ],
                new("/images/urun/sarj-adaptoru.webp", "Turuncu zeminde, kablosu takılı beyaz bir fiş şarj adaptörü")),
        ]),
        new("oyun", "Oyun", 40,
        [
            new("tus-75-mekanik-klavye", "Tuş 75 Mekanik Klavye",
                $"Kompakt yerleşimli mekanik klavye. {DemoNote}",
                Spec("Mekanik klavye"),
                [
                    new("SSC-5001-TR", "Tuş 75 Mekanik Klavye - Türkçe Q yerleşim", 2899.00m, Opt("yerlesim", "tr-q")),
                    new("SSC-5001-US", "Tuş 75 Mekanik Klavye - İngilizce yerleşim", 2899.00m, Opt("yerlesim", "us")),
                ],
                new("/images/urun/klavye.webp", "Açık renk masada çapraz duran, kırmızı bir tuşlu siyah mekanik klavye")),
            // Aşağıdaki iki ürün için fotoğraf yok: yanlış eşleşme olmasın diye görselsiz.
            new("halka-oyun-faresi", "Halka Oyun Faresi",
                $"Oyun için tasarlanmış fare. {DemoNote}",
                Spec("Oyun faresi"),
                [
                    new("SSC-5002-KBL", "Halka Oyun Faresi - Kablolu", 899.00m, new Dictionary<string, string> { ["baglanti"] = "kablolu", ["sensor"] = "standart" }),
                    new("SSC-5002-KBS", "Halka Oyun Faresi - Kablosuz", 1199.00m, new Dictionary<string, string> { ["baglanti"] = "kablosuz", ["sensor"] = "standart" }),
                    new("SSC-5002-KBY", "Halka Oyun Faresi - Kablosuz, yüksek hassasiyet", 1499.00m, new Dictionary<string, string> { ["baglanti"] = "kablosuz", ["sensor"] = "yuksek" }),
                ]),
            new("zemin-genis-mouse-pad", "Zemin Geniş Mouse Pad",
                $"Masa genişliğinde mouse pad. {DemoNote}",
                Spec("Mouse pad"),
                [
                    new("SSC-5003-M", "Zemin Geniş Mouse Pad - Orta boy", 349.00m, Opt("boyut", "orta")),
                    new("SSC-5003-L", "Zemin Geniş Mouse Pad - Geniş boy", 549.00m, Opt("boyut", "genis")),
                ]),
        ]),
    ];
}

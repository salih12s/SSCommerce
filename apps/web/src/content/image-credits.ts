// Üçüncü taraf fotoğraf listesi. /gorsel-kaynaklari sayfası bunu okur; public/images/CREDITS.md bu dosyadan üretilir.
// Hepsi Unsplash'ta "Unsplash License" ile yayımlanmış fotoğraflardır (Unsplash+ değil; kaynak sayfada ücretsiz lisans görünür).
// Unsplash License ticari kullanıma izin verir ve atıf zorunlu kılmaz; yine de yazar ve kaynak burada verilir.
// Her dosya yeniden boyutlandırılıp WebP'ye çevrilmiştir. Denetim: 2026-10-28, her fotoğraf yüksek çözünürlükte gözle
// incelendi (görünür logo/marka yazısı, ikonik model, yüz, filigran aranarak). Okunamayacak kadar küçük baskı
// işaretleri (ör. düzenleyici simgeler) elenmez; bu bir hukuki marka taraması değildir.

export type ImageCredit = {
  // public/ altındaki yol (sitede kullanılan URL).
  file: string;
  place: string;
  page: string;
  author: string;
  authorUrl: string;
  license: string;
  licenseUrl: string;
  change: string;
  // Denetimde bu fotoğrafın neden kaldığı.
  audit: string;
};

const LICENSE = "Unsplash License";
const LICENSE_URL = "https://unsplash.com/license";
const RESIZE = "800 piksel genişliğe yeniden boyutlandırıldı, WebP'ye çevrildi.";
const CLEAN = "Görünür logo ya da marka yazısı yok; tanınabilir bir marka tasarımı değil; yüz yok.";

function credit(
  file: string,
  place: string,
  shortId: string,
  author: string,
  username: string,
  change: string,
  audit: string,
): ImageCredit {
  return {
    file,
    place,
    page: `https://unsplash.com/photos/${shortId}`,
    author,
    authorUrl: `https://unsplash.com/@${username}`,
    license: LICENSE,
    licenseUrl: LICENSE_URL,
    change,
    audit,
  };
}

export const imageCredits: ImageCredit[] = [
  credit(
    "/images/hero/masa-ustu-teknoloji.webp",
    "Hero sahnesi",
    "B10AxHKLvnQ",
    "Jack B",
    "nervum",
    "1800 piksel genişliğe yeniden boyutlandırıldı, WebP'ye çevrildi.",
    "Koyu odada monitör, klavye, bilgisayar kasası ve masa lambaları. Okunur logo ya da marka yazısı yok; yüz yok.",
  ),
  credit(
    "/images/promo/kulaklik.webp",
    "Tanıtım kartı: kulaklık",
    "PDX_a_82obo",
    "C D-X",
    "cdx2",
    "4:3 kırpıldı, 1000 piksel genişliğe boyutlandırıldı, WebP'ye çevrildi.",
    "Sarı zeminde siyah kulaküstü kulaklık. Marka yazısı görünmüyor.",
  ),
  credit(
    "/images/promo/akilli-isik.webp",
    "Tanıtım kartı: akıllı yaşam",
    "TuAtSs8peoM",
    "Federico Bottos",
    "landscapeplaces",
    "4:3 kırpıldı, 1000 piksel genişliğe boyutlandırıldı, WebP'ye çevrildi.",
    "Siyah zeminde yalın beyaz ampul. Logo ya da yazı yok.",
  ),
  credit(
    "/images/banner/oyun-masasi.webp",
    "Banner: oyun",
    "mHm1ASYNC0I",
    "Joshua Kettle",
    "joshuakettle",
    "Soldaki marka yazılı hoparlör ve sağdaki bilgisayar kasası kadraj dışı kalacak şekilde kırpıldı; 1200 piksel genişliğe boyutlandırıldı, WebP'ye çevrildi.",
    "Kırpmadan sonra kavisli monitör, klavye ve fare kalıyor; görünür logo yok.",
  ),
  credit(
    "/images/banner/calisma-alani.webp",
    "Banner: çalışma alanı",
    "EYobsXWCY-k",
    "Arthur Lambillotte",
    "artlambi",
    "Alttaki klavye, fare ve marka logolu fare altlığı kadraj dışı kalacak şekilde kırpıldı; 1200 piksel genişliğe boyutlandırıldı, WebP'ye çevrildi.",
    "Kırpmadan sonra masa lambası, monitör ve raf üstü eşyalar kalıyor; okunur logo yok.",
  ),
  credit(
    "/images/banner/akilli-ev.webp",
    "Banner: akıllı ev",
    "z_nks-MiWpM",
    "Jakub Żerdzicki",
    "jakubzerdzicki",
    "2:1 kırpıldı, 1200 piksel genişliğe boyutlandırıldı, WebP'ye çevrildi.",
    "Renkli ışıkta ampul ve iki küçük sensör; logo ya da yazı yok.",
  ),
  credit(
    "/images/kategori/bilgisayarlar.webp",
    "Kategori: Bilgisayarlar",
    "j4uuKnN43_M",
    "Alex Knight",
    "agk42",
    "4:3 kırpıldı, " + RESIZE,
    "Dizüstünün ön yüzü ve klavyesi görünüyor; kapak logosu görünmüyor, ekran kapalı.",
  ),
  credit(
    "/images/kategori/oyun.webp",
    "Kategori: Oyun",
    "mHm1ASYNC0I",
    "Joshua Kettle",
    "joshuakettle",
    "Alt orta bölge 4:3 kırpıldı (klavye, fare, monitör alt kenarı); " + RESIZE,
    "Kırpma hoparlör ve bilgisayar kasasını dışarıda bırakıyor; klavye ve fare üzerinde okunur logo yok.",
  ),
  credit(
    "/images/kategori/ses-sistemleri.webp",
    "Kategori: Ses Sistemleri",
    "dBwadhWa-lI",
    "C D-X",
    "cdx2",
    "4:3 kırpıldı, " + RESIZE,
    "Sarı zeminde siyah kulaküstü kulaklık. " + CLEAN,
  ),
  credit(
    "/images/kategori/akilli-yasam.webp",
    "Kategori: Akıllı Yaşam",
    "uxYLtGRyGKQ",
    "Jakub Żerdzicki",
    "jakubzerdzicki",
    "4:3 kırpıldı, " + RESIZE,
    "Kamera, priz, sensör ve ampul; üzerlerinde okunur logo yok.",
  ),
  credit(
    "/images/kategori/telefon-saat.webp",
    "Kategori: Telefon & Saat",
    "FPEBZJLD8Rs",
    "pratik prasad",
    "pratik19",
    "Saat çevresine kırpıldı, kenarlar bulanık dolguyla 4:3'e tamamlandı; " + RESIZE,
    "Genel biçimli kare saat; ekranda marka arayüzü ya da logo yok.",
  ),
  credit(
    "/images/kategori/aksesuarlar.webp",
    "Kategori: Aksesuarlar",
    "9QTFMkh-ezM",
    "Markus Winkler",
    "markuswinkler",
    "4:3 kırpıldı, " + RESIZE,
    "Fiş adaptörü üzerinde yalnızca uygunluk simgeleri var; marka yazısı yok.",
  ),
  credit(
    "/images/kategori/calisma-alani.webp",
    "Kategori: Çalışma Alanı",
    "KZnfwqi-B0U",
    "Rohit",
    "romiem",
    "Sağdaki logolu cihaz kadraj dışı kalacak şekilde kırpıldı; " + RESIZE,
    "Kırpmadan sonra monitör, bitki, hoparlör ve klavye kalıyor; logo görünmüyor.",
  ),
  credit(
    "/images/kategori/canta-tasima.webp",
    "Kategori: Çanta & Taşıma",
    "8TSqJoI-NVs",
    "Luis Quintero",
    "jibarox",
    "Sol kenar kırpıldı, düz dolguyla 4:3'e tamamlandı; " + RESIZE,
    "Siyah kumaş sırt çantası; logo yok. Çantayı tutan elin yalnızca bir kısmı görünüyor, yüz yok.",
  ),
  credit(
    "/images/urun/dizustu.webp",
    "Ürün: Aero 14 İnce Dizüstü",
    "1SAnrIxw5OY",
    "Kari Shea",
    "karishea",
    "4:3 kırpıldı, " + RESIZE,
    "Ön görünüm, ekran kapalı; kapak logosu görünmüyor. Genel bir dizüstü biçimi.",
  ),
  credit(
    "/images/urun/monitor.webp",
    "Ürün: Vista 34 Kavisli Monitör",
    "9ixBVUCo6NU",
    "Mediamodifier",
    "mediamodifier",
    "4:3 kırpıldı, " + RESIZE,
    "Boş beyaz ekranlı kavisli monitör (maket fotoğrafı); logo ya da yazı yok.",
  ),
  credit(
    "/images/urun/telefon.webp",
    "Ürün: Nova Katlanır Telefon",
    "sXVzE285xo0",
    "2H Media",
    "2hmedia",
    "4:3 kırpıldı, " + RESIZE,
    "Yatan siyah ekranlı gövde; arka yüz ve marka yazısı görünmüyor.",
  ),
  credit(
    "/images/urun/kulaklik.webp",
    "Ürün: Ton Kulaküstü Kulaklık",
    "xR4JHzr69Og",
    "Jason Leung",
    "ninjason",
    "4:3 kırpıldı, " + RESIZE,
    "Siyah zeminde siyah kulaklık; marka yazısı görünmüyor.",
  ),
  credit(
    "/images/urun/hoparlor.webp",
    "Ürün: Çember Taşınabilir Hoparlör",
    "VSYq9nS-fY0",
    "Jonas Jacobsson",
    "jonasjacobsson",
    "Hoparlör çevresine kırpıldı, kenarlar bulanık dolguyla 4:3'e tamamlandı; " + RESIZE,
    "Gözenekli ızgaralı hoparlör; logo görünmüyor.",
  ),
  credit(
    "/images/urun/kulakici.webp",
    "Ürün: Nokta Kablosuz Kulakiçi",
    "_h50cvQCj_M",
    "Yasin Hasan",
    "yasin",
    "4:3 kırpıldı, " + RESIZE,
    "Kulakiçi kutusunda yalnızca L ve R işaretleri var; marka yazısı yok.",
  ),
  credit(
    "/images/urun/akilli-ampul.webp",
    "Ürün: Işık Akıllı Ampul",
    "iEDKPLfJrEo",
    "Ian Talmacs",
    "iantalmacs",
    "4:3 kırpıldı, " + RESIZE,
    "Yalın beyaz ampul; yazı yok.",
  ),
  credit(
    "/images/urun/ic-mekan-kamera.webp",
    "Ürün: Göz Ev İçi Kamera",
    "Ldbf6igNiRg",
    "Jakub Żerdzicki",
    "jakubzerdzicki",
    "4:3 kırpıldı, " + RESIZE,
    "Beyaz zeminde ev kamerası; üzerinde logo görünmüyor.",
  ),
  credit(
    "/images/urun/klavye.webp",
    "Ürün: Tuş 75 Mekanik Klavye",
    "cVUPic1cbd4",
    "Martin Garrido",
    "martingarrido",
    "4:3 kırpıldı, " + RESIZE,
    "Üstten görünüşte yalnızca tuş yazıları var; marka logosu yok.",
  ),
  credit(
    "/images/urun/kablosuz-sarj.webp",
    "Ürün: Disk Kablosuz Şarj Pedi",
    "iE-MsY-8vEw",
    "Matteo Grobberio",
    "peotus",
    "4:3 kırpıldı, " + RESIZE,
    "Yalın beyaz şarj pedi; yazı yok.",
  ),
  credit(
    "/images/urun/sarj-adaptoru.webp",
    "Ürün: Hızlı Şarj Adaptörü",
    "tzfD-clzUTU",
    "Markus Winkler",
    "markuswinkler",
    "Adaptör çevresine kırpıldı, " + RESIZE,
    "Adaptör üzerinde yalnızca uygunluk simgeleri var; marka yazısı yok.",
  ),
  credit(
    "/images/urun/sirt-cantasi.webp",
    "Ürün: Şehir Sırt Çantası",
    "iFelMiWMuO4",
    "Marina Zaharkina",
    "minton",
    "Çantaya yaklaşıldı, kenarlar bulanık dolguyla 4:3'e tamamlandı; " + RESIZE,
    "Siyah deri sırt çantası; logo görünmüyor.",
  ),
];

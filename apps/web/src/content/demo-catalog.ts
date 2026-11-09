// DEMO VERİ (D-010): gerçek katalog değildir, Faz 3'te backend'den gelecek.
// Fiyatlar kuruş cinsinden tam sayıdır (priceMinor: 4299900 = 42.999,00 TL).
// Ürün adları kurgusaldır; gerçek bir marka ya da modelle ilgisi yoktur.
// Görseller `public/images` altındaki lisanslı fotoğraflardır; kaynak ve lisansları
// `image-credits.ts` içinde listelenir (D-025). Alt metinlerde marka adı geçmez.

export type DemoCategory = {
  slug: string;
  name: string;
  blurb: string;
  image: string;
  imageAlt: string;
};

export type DemoProduct = {
  code: string;
  name: string;
  categorySlug: string;
  priceMinor: number;
  image: string;
  imageAlt: string;
};

export type DemoBanner = {
  slug: string;
  title: string;
  text: string;
  cta: string;
  categorySlug: string;
  tone: "dark" | "light";
  image: string;
  imageAlt: string;
};

export type DemoPromo = {
  slug: string;
  title: string;
  text: string;
  cta: string;
  categorySlug: string;
  image: string;
  imageAlt: string;
};

export const categories: DemoCategory[] = [
  {
    slug: "bilgisayarlar",
    name: "Bilgisayarlar",
    blurb: "Dizüstü ve masaüstü bilgisayarlar",
    image: "/images/kategori/bilgisayarlar.webp",
    imageAlt: "Ahşap masada açık duran dizüstü bilgisayar ve yanında bir fincan kahve",
  },
  {
    slug: "oyun",
    name: "Oyun",
    blurb: "Klavye, fare ve oyun masası ekipmanı",
    image: "/images/kategori/oyun.webp",
    imageAlt: "Renkli ışıklı bir oyun masasında mekanik klavye, fare ve kavisli monitörün alt kenarı",
  },
  {
    slug: "ses-sistemleri",
    name: "Ses Sistemleri",
    blurb: "Kulaklık, kulakiçi ve hoparlör",
    image: "/images/kategori/ses-sistemleri.webp",
    imageAlt: "Sarı zemin üzerinde duran siyah kulaküstü kulaklık",
  },
  {
    slug: "akilli-yasam",
    name: "Akıllı Yaşam",
    blurb: "Aydınlatma, kamera ve sensörler",
    image: "/images/kategori/akilli-yasam.webp",
    imageAlt: "Beyaz zeminde bir ev kamerası, akıllı priz, hareket sensörü ve ampul",
  },
  {
    slug: "telefon-saat",
    name: "Telefon & Saat",
    blurb: "Akıllı telefonlar ve saatler",
    image: "/images/kategori/telefon-saat.webp",
    imageAlt: "Açık renk zeminde beyaz kayışlı, ekranı saati gösteren bir akıllı saat",
  },
  {
    slug: "aksesuarlar",
    name: "Aksesuarlar",
    blurb: "Şarj cihazları ve günlük küçük parçalar",
    image: "/images/kategori/aksesuarlar.webp",
    imageAlt: "Turuncu zeminde duran beyaz bir fiş şarj adaptörü",
  },
  {
    slug: "calisma-alani",
    name: "Çalışma Alanı",
    blurb: "Monitör, klavye ve masa başı düzeni",
    image: "/images/kategori/calisma-alani.webp",
    imageAlt: "Beyaz bir çalışma masasında monitör, saksı bitkisi ve klavye",
  },
  {
    slug: "canta-tasima",
    name: "Çanta & Taşıma",
    blurb: "Teknolojiyi yanında taşımak için",
    image: "/images/kategori/canta-tasima.webp",
    imageAlt: "Açık gri duvar önünde elle tutulan siyah bir sırt çantası",
  },
];

export const products: DemoProduct[] = [
  {
    code: "SSC-1001",
    name: "Aero 14 İnce Dizüstü",
    categorySlug: "bilgisayarlar",
    priceMinor: 4299900,
    image: "/images/urun/dizustu.webp",
    imageAlt: "Açık renk ahşap bir komodinin üzerinde duran, ekranı kapalı dizüstü bilgisayar",
  },
  {
    code: "SSC-1002",
    name: "Vista 34 Kavisli Monitör",
    categorySlug: "calisma-alani",
    priceMinor: 1349000,
    image: "/images/urun/monitor.webp",
    imageAlt: "Beyaz zeminde, siyah ayaklı kavisli geniş bir monitör",
  },
  {
    code: "SSC-2001",
    name: "Nova Katlanır Telefon",
    categorySlug: "telefon-saat",
    priceMinor: 2449900,
    image: "/images/urun/telefon.webp",
    imageAlt: "Beyaz zeminde yatay duran, ekranı kapalı siyah bir akıllı telefon",
  },
  {
    code: "SSC-3001",
    name: "Ton Kulaküstü Kulaklık",
    categorySlug: "ses-sistemleri",
    priceMinor: 389000,
    image: "/images/urun/kulaklik.webp",
    imageAlt: "Siyah zeminde duran siyah kulaküstü kulaklık",
  },
  {
    code: "SSC-3002",
    name: "Çember Taşınabilir Hoparlör",
    categorySlug: "ses-sistemleri",
    priceMinor: 219000,
    image: "/images/urun/hoparlor.webp",
    imageAlt: "Pencere önünde duran, gözenekli ızgaralı siyah ve gri bir hoparlör",
  },
  {
    code: "SSC-3003",
    name: "Nokta Kablosuz Kulakiçi",
    categorySlug: "ses-sistemleri",
    priceMinor: 149900,
    image: "/images/urun/kulakici.webp",
    imageAlt: "Açık yeşil zeminde, kapağı açık şarj kutusunda duran siyah kablosuz kulaklıklar",
  },
  {
    code: "SSC-4001",
    name: "Işık Akıllı Ampul",
    categorySlug: "akilli-yasam",
    priceMinor: 34900,
    image: "/images/urun/akilli-ampul.webp",
    imageAlt: "Beyaz zeminde yan yatan beyaz bir ampul",
  },
  {
    code: "SSC-4002",
    name: "Göz Ev İçi Kamera",
    categorySlug: "akilli-yasam",
    priceMinor: 129900,
    image: "/images/urun/ic-mekan-kamera.webp",
    imageAlt: "Beyaz zeminde duran, dönebilen başlıklı beyaz bir ev içi kamera",
  },
  {
    code: "SSC-5001",
    name: "Tuş 75 Mekanik Klavye",
    categorySlug: "oyun",
    priceMinor: 289900,
    image: "/images/urun/klavye.webp",
    imageAlt: "Açık renk masada çapraz duran, kırmızı bir tuşlu siyah mekanik klavye",
  },
  {
    code: "SSC-6001",
    name: "Disk Kablosuz Şarj Pedi",
    categorySlug: "aksesuarlar",
    priceMinor: 79900,
    image: "/images/urun/kablosuz-sarj.webp",
    imageAlt: "Ahşap yüzeyde duran, kablosu bağlı beyaz yuvarlak şarj pedi",
  },
  {
    code: "SSC-6002",
    name: "Hızlı Şarj Adaptörü",
    categorySlug: "aksesuarlar",
    priceMinor: 54900,
    image: "/images/urun/sarj-adaptoru.webp",
    imageAlt: "Turuncu zeminde, kablosu takılı beyaz bir fiş şarj adaptörü",
  },
  {
    code: "SSC-7001",
    name: "Şehir Sırt Çantası",
    categorySlug: "canta-tasima",
    priceMinor: 119900,
    image: "/images/urun/sirt-cantasi.webp",
    imageAlt: "Sarı yeşil zeminde duran siyah deri sırt çantası",
  },
];

// Hero'nun yanındaki iki dikey tanıtım kartı. Rota her zaman var olan bir kategori sayfasıdır.
export const promos: DemoPromo[] = [
  {
    slug: "kulaklik",
    title: "Kulaklık ve hoparlörler",
    text: "Ses sistemleri kategorisindeki örnek ürünlere göz at.",
    cta: "Ses sistemleri",
    categorySlug: "ses-sistemleri",
    image: "/images/promo/kulaklik.webp",
    imageAlt: "Sarı zemin üzerinde yan yatan siyah kulaküstü kulaklık",
  },
  {
    slug: "akilli-isik",
    title: "Akıllı yaşam",
    text: "Işık, kamera ve sensör ürünlerini incele.",
    cta: "Akıllı yaşam",
    categorySlug: "akilli-yasam",
    image: "/images/promo/akilli-isik.webp",
    imageAlt: "Siyah zeminde dik duran beyaz bir ampul",
  },
];

// Kategori ızgarasının altındaki üç geniş banner. Metinler vaat değil, kategori yönlendirmesidir.
export const banners: DemoBanner[] = [
  {
    slug: "oyun",
    title: "Oyun masanı kur",
    text: "Klavye ve masa başı ekipmanı.",
    cta: "Oyun ürünleri",
    categorySlug: "oyun",
    tone: "dark",
    image: "/images/banner/oyun-masasi.webp",
    imageAlt: "Mor ve mavi ışıkla aydınlanan oyun masasında kavisli monitör ve klavye",
  },
  {
    slug: "calisma-alani",
    title: "Çalışma alanını yeniden düzenle",
    text: "Monitör, klavye ve masa başı parçaları.",
    cta: "Çalışma alanı",
    categorySlug: "calisma-alani",
    tone: "light",
    image: "/images/banner/calisma-alani.webp",
    imageAlt: "Beyaz masada monitör, masa lambası, saksı bitkisi ve raf üstü ekipman",
  },
  {
    slug: "akilli-ev",
    title: "Evi akıllı ürünlerle tamamla",
    text: "Ampul ve sensörler.",
    cta: "Akıllı yaşam",
    categorySlug: "akilli-yasam",
    tone: "dark",
    image: "/images/banner/akilli-ev.webp",
    imageAlt: "Turuncu ve pembe ışıkta bir akıllı ampul ile iki küçük yuvarlak sensör",
  },
];

export const hero = {
  image: "/images/hero/masa-ustu-teknoloji.webp",
  imageAlt: "Koyu bir odada kırmızı ve mavi ışıkla aydınlanan masa; monitör, klavye ve bilgisayar kasası",
};

export function getCategory(slug: string): DemoCategory | undefined {
  return categories.find((c) => c.slug === slug);
}

export function getProduct(code: string): DemoProduct | undefined {
  return products.find((p) => p.code === code);
}

export function productsInCategory(slug: string): DemoProduct[] {
  return products.filter((p) => p.categorySlug === slug);
}

// Sekme olarak yalnızca en az iki ürünü olan kategoriler gösterilir (veriden türer).
export function tabCategories(): DemoCategory[] {
  return categories.filter((c) => productsInCategory(c.slug).length >= 2);
}

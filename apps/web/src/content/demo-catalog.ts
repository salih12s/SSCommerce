// DEMO VERİ (D-010): gerçek katalog değildir, Faz 3'te backend'den gelecek.
// Fiyatlar kuruş cinsinden tam sayıdır (priceMinor: 4299900 = 42.999,00 TL).

export type ArtKind =
  | "laptop"
  | "monitor"
  | "phone"
  | "watch"
  | "headphones"
  | "speaker"
  | "bulb"
  | "sensor";

export type DemoCategory = {
  slug: string;
  name: string;
  blurb: string;
  art: ArtKind;
};

export type DemoProduct = {
  code: string;
  name: string;
  categorySlug: string;
  priceMinor: number;
  specs: string[];
  art: ArtKind;
  artAlt: string;
};

export type DemoCollection = {
  slug: string;
  title: string;
  blurb: string;
  productCodes: string[];
};

export const categories: DemoCategory[] = [
  { slug: "bilgisayar-oyun", name: "Bilgisayar & Oyun", blurb: "Dizüstü, monitör ve masa başı donanımı", art: "laptop" },
  { slug: "telefon-giyilebilir", name: "Telefon & Giyilebilir", blurb: "Akıllı telefon ve saatler", art: "phone" },
  { slug: "ses-goruntu", name: "Ses & Görüntü", blurb: "Kulaklık ve hoparlörler", art: "headphones" },
  { slug: "akilli-ev-yasam", name: "Akıllı Ev & Yaşam", blurb: "Aydınlatma ve ev sensörleri", art: "bulb" },
];

export const products: DemoProduct[] = [
  {
    code: "SSC-1001",
    name: "Aero 14 İnce Dizüstü",
    categorySlug: "bilgisayar-oyun",
    priceMinor: 4299900,
    specs: ["14 inç", "16 GB", "1,2 kg"],
    art: "laptop",
    artAlt: "Açık duran ince bir dizüstü bilgisayarın çizimi",
  },
  {
    code: "SSC-1002",
    name: "Vista 27 Monitör",
    categorySlug: "bilgisayar-oyun",
    priceMinor: 1349000,
    specs: ["27 inç", "QHD", "144 Hz"],
    art: "monitor",
    artAlt: "Ayaklı bir bilgisayar monitörünün çizimi",
  },
  {
    code: "SSC-2001",
    name: "Nova Akıllı Telefon",
    categorySlug: "telefon-giyilebilir",
    priceMinor: 2449900,
    specs: ["6,1 inç", "128 GB", "USB-C"],
    art: "phone",
    artAlt: "Ön yüzü görünen bir akıllı telefonun çizimi",
  },
  {
    code: "SSC-2002",
    name: "Tempo Akıllı Saat",
    categorySlug: "telefon-giyilebilir",
    priceMinor: 579000,
    specs: ["1,4 inç", "5 ATM", "7 gün pil"],
    art: "watch",
    artAlt: "Kayışlı bir akıllı saatin çizimi",
  },
  {
    code: "SSC-3001",
    name: "Ton Kulaküstü Kulaklık",
    categorySlug: "ses-goruntu",
    priceMinor: 389000,
    specs: ["ANC", "Bluetooth 5.3", "30 sa"],
    art: "headphones",
    artAlt: "Kulaküstü bir kulaklığın çizimi",
  },
  {
    code: "SSC-3002",
    name: "Çember Taşınabilir Hoparlör",
    categorySlug: "ses-goruntu",
    priceMinor: 219000,
    specs: ["IP67", "USB-C", "0,6 kg"],
    art: "speaker",
    artAlt: "Silindir biçimli taşınabilir bir hoparlörün çizimi",
  },
  {
    code: "SSC-4001",
    name: "Işık Akıllı Ampul",
    categorySlug: "akilli-ev-yasam",
    priceMinor: 34900,
    specs: ["E27", "9 W", "RGB"],
    art: "bulb",
    artAlt: "Işık saçan bir akıllı ampulün çizimi",
  },
  {
    code: "SSC-4002",
    name: "Derece Akıllı Termostat",
    categorySlug: "akilli-ev-yasam",
    priceMinor: 249000,
    specs: ["Wi-Fi", "21 °C", "Dokunmatik"],
    art: "sensor",
    artAlt: "Yuvarlak ekranlı bir akıllı termostatın çizimi",
  },
];

export const collections: DemoCollection[] = [
  {
    slug: "ev-ofisi",
    title: "Ev ofisi kurulumu",
    blurb: "Masa başında uzun günler için ekran, ses ve ışık.",
    productCodes: ["SSC-1001", "SSC-1002", "SSC-3001", "SSC-4001"],
  },
  {
    slug: "yolda",
    title: "Yolda ve hareketli",
    blurb: "Cebe, çantaya ve bileğe sığan parçalar.",
    productCodes: ["SSC-2001", "SSC-2002", "SSC-3002"],
  },
  {
    slug: "akilli-oda",
    title: "Akıllı oda",
    blurb: "Evin ısısını ve ışığını tek yerden yönetmek için.",
    productCodes: ["SSC-4001", "SSC-4002", "SSC-3002"],
  },
];

export function getCategory(slug: string): DemoCategory | undefined {
  return categories.find((c) => c.slug === slug);
}

export function getProduct(code: string): DemoProduct | undefined {
  return products.find((p) => p.code === code);
}

export function productsInCategory(slug: string): DemoProduct[] {
  return products.filter((p) => p.categorySlug === slug);
}

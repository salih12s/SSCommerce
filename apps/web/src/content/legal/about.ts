import type { LegalDocument } from "./types";

export const about: LegalDocument = {
  slug: "hakkimizda",
  navLabel: "Hakkımızda",
  title: "Hakkımızda",
  description: "SSCommerce, elektronik ve teknoloji ürünleri satan tek satıcılı bir demo mağazadır; portföy projesidir.",
  lead: "SSCommerce, elektronik ve teknoloji ürünleri satan tek satıcılı bir demo mağazadır. Bir portföy projesi olarak yazılmıştır.",
  toc: false,
  sections: [
    {
      id: "nedir",
      title: "SSCommerce nedir?",
      blocks: [
        {
          type: "p",
          text: "SSCommerce, bilgisayar, telefon, ses ve akıllı ev ürünlerinin listelendiği, tek bir satıcının sattığı fiziksel ürünlerden oluşan örnek bir e-ticaret vitrinidir. Gerçek bir şirketin mağazası değildir; bir yazılım portföyünde, uçtan uca bir alışveriş akışının nasıl kurulabileceğini göstermek için yapılmıştır.",
        },
        {
          type: "p",
          text: "Vitrinde görünen ürünler, markalar, fiyatlar ve kategoriler kurgusaldır. Fotoğraflar ürünlerin gerçek görüntüsü değil, temsilî örneklerdir.",
        },
      ],
    },
    {
      id: "neler-yapilir",
      title: "Burada neler yapabilirsiniz?",
      blocks: [
        {
          type: "ul",
          items: [
            "Kategorilere göz atabilir, ürün arayabilir ve ürün ayrıntılarını görebilirsiniz.",
            "Hesap açıp giriş yapabilir, teslimat adreslerinizi kaydedebilir ve parolanızı değiştirebilirsiniz.",
            "Ürünleri sepete ekleyebilir ve favorilerinize alabilirsiniz.",
            "Sipariş verebilir, siparişinizin durumunu izleyebilir ve henüz hazırlanmaya başlamamış bir siparişi iptal edebilirsiniz.",
          ],
        },
        {
          type: "p",
          text: "Mağazanın arka tarafında stokları, ürünleri ve siparişleri yöneten yönetici ve depo ekranları da vardır; bunlar yalnızca ilgili yetkili hesaplara açıktır.",
        },
      ],
    },
    {
      id: "neler-yapilmaz",
      title: "Neler yapılmaz?",
      blocks: [
        {
          type: "note",
          title: "Sipariş verilir, ödeme alınmaz",
          text: "Bu vitrinde sipariş verilebilir; ancak ödeme alınmaz, kargo ücreti hesaplanmaz ve hiçbir ürün gönderilmez. Siparişler yalnızca demo kaydıdır.",
        },
        {
          type: "p",
          text: "Bu yüzden kart bilgisi, banka bilgisi ya da ödeme yöntemi istenmez. Kampanya, indirim kodu ya da üyelik avantajı yoktur.",
        },
      ],
    },
    {
      id: "gorseller",
      title: "Görseller ve içerik",
      blocks: [
        {
          type: "p",
          text: "Sitedeki fotoğrafların yazarları, kaynakları ve lisansları Görsel kaynakları sayfasında tek tek listelenir. Yasal sayfalar (gizlilik, mesafeli satış, çerez politikası) da örnek metindir.",
        },
      ],
    },
    {
      id: "yapi",
      title: "Nasıl yapıldı?",
      blocks: [
        {
          type: "p",
          text: "Arka uç ASP.NET Core ve PostgreSQL ile, ön yüz Next.js ve TypeScript ile yazılmıştır. İş kuralları (fiyat, stok, sipariş durumları, yetki) yalnızca sunucuda çalışır; tarayıcı yalnızca gösterir ve istek gönderir.",
        },
      ],
    },
  ],
};

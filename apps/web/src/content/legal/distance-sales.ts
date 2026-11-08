import type { LegalDocument } from "./types";

export const distanceSales: LegalDocument = {
  slug: "mesafeli-satis",
  navLabel: "Mesafeli satış sözleşmesi",
  title: "Mesafeli satış sözleşmesi",
  description: "SSCommerce demo vitrini için örnek mesafeli satış sözleşmesi taslağı: taraflar, konu, teslimat, cayma hakkı ve uyuşmazlık.",
  lead: "6502 sayılı Tüketicinin Korunması Hakkında Kanun ve Mesafeli Sözleşmeler Yönetmeliği'ne göre düzenlenen bir sözleşmenin yapısını gösteren örnek taslaktır. Bağlayıcı bir sözleşme değildir.",
  toc: true,
  sections: [
    {
      id: "once-bilin",
      title: "Önce şunu bilin",
      blocks: [
        {
          type: "note",
          title: "Demo kaydı",
          text: "Bu vitrinde ödeme alınmaz, kargo hesaplanmaz ve ürün gönderilmez. Verdiğiniz siparişler demo kaydıdır; aşağıdaki maddeler gerçek bir mağazada bu kayıtların nasıl bir sözleşmeye bağlanacağını örneklemek içindir.",
        },
      ],
    },
    {
      id: "taraflar",
      title: "Taraflar",
      blocks: [
        {
          type: "facts",
          items: [
            { label: "Satıcı", value: "SSCommerce (demo), kurgusal ad. Gerçek unvan, vergi numarası ya da MERSİS numarası yoktur." },
            { label: "Satıcı adresi", value: "Örnek Mah. Demo Sok. No:1, İstanbul (kurgusal adres)" },
            { label: "Satıcı e-posta", value: "destek@sscommerce.example (örnek)" },
            { label: "Alıcı", value: "Sipariş sırasında hesabınızın e-posta adresi ve seçtiğiniz teslimat adresindeki alıcı adı." },
          ],
        },
      ],
    },
    {
      id: "konu",
      title: "Sözleşmenin konusu",
      blocks: [
        {
          type: "p",
          text: "Sözleşmenin konusu, alıcının sitede sipariş verdiği ürünlerin satışı ve teslimi ile tarafların hak ve yükümlülükleridir. Ürünler fiziksel, elektronik ve teknoloji ürünleridir.",
        },
      ],
    },
    {
      id: "urun-fiyat",
      title: "Ürün ve fiyat bilgisi",
      blocks: [
        {
          type: "ul",
          items: [
            "Ürünün adı, seçeneği, adedi ve birim fiyatı sipariş özetinde gösterilir. Fiyatlar Türk lirası (TRY) cinsindendir.",
            "Sepetinizdeki bir ürünün fiyatı ya da stoğu siz sipariş vermeden önce değişirse, sepet sizi uyarır; sipariş yalnızca güncel fiyat ve stokla verilebilir.",
            "Siparişi verdiğiniz anda fiyat ve teslimat adresi siparişe kaydedilir; sonradan yapılan fiyat değişikliği verilmiş siparişi etkilemez.",
            "Vitrindeki ürünler, markalar ve fiyatlar kurgusaldır.",
          ],
        },
      ],
    },
    {
      id: "odeme",
      title: "Ödeme",
      blocks: [
        {
          type: "p",
          text: "Gerçek bir mağazada ödeme yöntemi ve tutar sipariş özetinde belirtilir. Bu vitrinde ödeme alınmaz: kart ya da banka bilgisi istenmez, hesabınızdan para çekilmez.",
        },
      ],
    },
    {
      id: "teslimat",
      title: "Teslimat",
      blocks: [
        {
          type: "ul",
          items: [
            "Ürünler siparişte seçtiğiniz teslimat adresine gönderilirdi. Bu vitrinde kargo ücreti hesaplanmaz ve sipariş özetinde “hesaplanmadı” olarak görünür.",
            "Sipariş şu durumlardan geçer: Sipariş alındı, Hazırlanıyor, Kargoya verildi, Teslim edildi. Siparişin iptal edilmesi de mümkündür. Durumu Siparişlerim sayfasından izleyebilirsiniz.",
            "Sipariş alındı durumundaki bir siparişi Siparişlerim sayfasından siz iptal edebilirsiniz; ayrılan stok iptalde geri verilir.",
            "Hiçbir ürün fiilen gönderilmez; teslim edildi durumu da demo kaydıdır.",
          ],
        },
      ],
    },
    {
      id: "cayma",
      title: "Cayma hakkı",
      blocks: [
        {
          type: "p",
          text: "Alıcı, mal teslim alındıktan sonra 14 gün içinde herhangi bir gerekçe göstermeden ve cezai şart ödemeden sözleşmeden cayma hakkına sahiptir. Cayma süresi, ürünün alıcıya ya da alıcının belirlediği kişiye tesliminden itibaren başlar. Cayma bildirimi satıcıya yazılı olarak yapılır; satıcı bedeli, bildirimin kendisine ulaşmasından itibaren 14 gün içinde iade eder.",
        },
        {
          type: "p",
          text: "Mesafeli Sözleşmeler Yönetmeliği'nin 15. maddesine göre aşağıdaki durumlarda cayma hakkı kullanılamaz (örnek özet; tam liste yönetmelikte yer alır):",
        },
        {
          type: "ul",
          items: [
            "Alıcının istekleri ya da kişisel ihtiyaçları doğrultusunda hazırlanan, kişiye özel ürünler.",
            "Teslimden sonra ambalajı açılmış; iadesi sağlık ve hijyen açısından uygun olmayan ürünler (örneğin kulak içi kulaklık uçları).",
            "Ambalajı açılmış ses veya görüntü kayıtları, yazılım programları ve bilgisayar sarf malzemeleri.",
            "Teslimden sonra başka ürünlerle karışan ve doğası gereği ayrıştırılamayan ürünler.",
            "Elektronik ortamda anında ifa edilen hizmetler ve anında teslim edilen dijital içerikler.",
          ],
        },
        {
          type: "p",
          text: "Bu vitrinde ödeme alınmadığı için iade edilecek bir bedel yoktur ve cayma bildirimini alacak gerçek bir kanal bulunmaz.",
        },
      ],
    },
    {
      id: "uyusmazlik",
      title: "Uyuşmazlıkların çözümü",
      blocks: [
        {
          type: "p",
          text: "Uyuşmazlık hâlinde, her yıl güncellenen parasal sınırlar içinde alıcının yerleşim yerindeki ya da işlemin yapıldığı yerdeki Tüketici Hakem Heyetine, sınırı aşan durumlarda Tüketici Mahkemesine başvurulabilir.",
        },
        {
          type: "p",
          text: "Bu demo kurgusal bir işletmeye ait olduğundan gerçek bir başvuru kurumu ya da yetkili mahkeme belirlenmemiştir.",
        },
      ],
    },
    {
      id: "yururluk",
      title: "Yürürlük",
      blocks: [
        {
          type: "p",
          text: "Gerçek bir sözleşme, alıcının siparişi onayladığı anda yürürlüğe girerdi. Bu metin yalnızca örnek olduğundan sipariş vermek herhangi bir hukuki bağ doğurmaz.",
        },
      ],
    },
  ],
};

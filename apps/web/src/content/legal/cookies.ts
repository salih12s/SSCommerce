import type { LegalDocument } from "./types";

// Çerez adları ve bayrakları backend/src/SSCommerce.Api/Security/AuthCookieSettings.cs'ten; depo anahtarları
// src/lib/stock-intent.ts ve src/features/checkout/order-key.ts'ten alınmıştır. Yeni çerez ya da depo anahtarı eklenince bu sayfa da güncellenmelidir.
export const cookies: LegalDocument = {
  slug: "cerez-politikasi",
  navLabel: "Çerez politikası",
  title: "Çerez politikası",
  description: "SSCommerce demo vitrininde kullanılan çerezler ve tarayıcı depolaması; analitik ya da reklam çerezi yoktur.",
  lead: "Bu vitrin yalnızca oturumu ve güvenliği sağlayan zorunlu çerezleri kullanır. Analitik, reklam ya da üçüncü taraf çerezi yoktur.",
  toc: false,
  sections: [
    {
      id: "kisaca",
      title: "Kısaca",
      blocks: [
        {
          type: "p",
          text: "Çerez, bir siteyi ziyaret ettiğinizde tarayıcınıza yazılan küçük bir metin dosyasıdır. Bu vitrin yalnızca giriş yapmış kalmanızı ve isteklerinizin sizden geldiğinin doğrulanmasını sağlayan iki çerez kullanır. Pazarlama ya da ölçümleme amacıyla çerez bırakmaz.",
        },
      ],
    },
    {
      id: "cerezler",
      title: "Kullandığımız çerezler",
      blocks: [
        {
          type: "table",
          caption: "Zorunlu çerezler",
          head: ["Ad", "Amaç", "Süre", "Bayraklar"],
          rows: [
            [
              "__Host-ssc.auth (yerel geliştirmede: ssc.auth)",
              "Kimlik doğrulama oturumu. Giriş yaptığınızı hatırlar.",
              "En çok 8 saat; işlem yaptıkça uzar. Kalıcı değildir, tarayıcı kapanınca biter. Çıkışta silinir.",
              "HttpOnly, SameSite=Lax, Secure (geliştirmede yalnızca HTTPS isteğinde), Path=/",
            ],
            [
              "__Host-ssc.csrf (yerel geliştirmede: ssc.csrf)",
              "Sahte istek (CSRF) korumasının belirteci. Sepet, adres ve sipariş gibi değiştiren isteklerin sizden geldiğini doğrular.",
              "Oturum çerezi: tarayıcı kapanınca silinir.",
              "HttpOnly, SameSite=Strict, Secure (geliştirmede yalnızca HTTPS isteğinde), Path=/",
            ],
          ],
        },
        {
          type: "p",
          text: "__Host- önekli adlar, tarayıcının çerezi yalnızca güvenli bağlantıda, alan adı belirtilmeden ve tüm yol için kabul etmesini zorunlu kılar. Geliştirme ortamında HTTP üzerinde çalışabilmek için önek kullanılmaz.",
        },
      ],
    },
    {
      id: "depolama",
      title: "Tarayıcı depolaması",
      blocks: [
        {
          type: "p",
          text: "Aşağıdaki kayıtlar çerez değildir ve sunucuya kendiliğinden gönderilmez. Yalnızca sekme (oturum) süresince sessionStorage içinde durur; sekmeyi kapattığınızda silinir. localStorage ve IndexedDB kullanılmaz.",
        },
        {
          type: "table",
          caption: "sessionStorage anahtarları",
          head: ["Anahtar", "İçerik", "Amaç"],
          rows: [
            [
              "ssc.order-intent",
              "Rastgele bir anahtar, kullanıcı ve adres kimlikleri (uuid) ve sunucunun hesapladığı fiyat özeti. E-posta, ad ya da adres metni yoktur.",
              "Bağlantı koparsa siparişi yeniden denerken aynı siparişin iki kez oluşmasını önlemek.",
            ],
            [
              "ssc.stock-intent",
              "Yalnızca yönetici ve depo personelinin stok işleminde: rastgele anahtar, kullanıcı kimliği ve işlemin kendisi (depo, ürün seçeneği, adet, neden).",
              "Aynı stok işleminin yeniden denemede iki kez uygulanmasını önlemek.",
            ],
          ],
        },
        {
          type: "p",
          text: "Sepetiniz ve favorileriniz tarayıcıda kalıcı olarak saklanmaz; hesabınıza bağlı olarak sunucuda tutulur ve çıkış yapınca ekrandan temizlenir.",
        },
      ],
    },
    {
      id: "kullanmadiklarimiz",
      title: "Kullanmadıklarımız",
      blocks: [
        {
          type: "ul",
          items: [
            "Analitik ya da ölçümleme çerezi (örneğin ziyaretçi sayacı).",
            "Reklam, yeniden hedefleme ya da sosyal medya izleyicisi.",
            "Üçüncü taraf çerezi: sayfalar yalnızca kendi alan adından çerez bırakır.",
          ],
        },
      ],
    },
    {
      id: "onay-bandi",
      title: "Neden çerez onay bandı yok?",
      blocks: [
        {
          type: "p",
          text: "Kimlik doğrulama ve güvenlik çerezleri, istediğiniz hizmeti sunmak için kesinlikle gereklidir; bu tür çerezler için ayrıca açık rıza istenmez. Seçebileceğiniz isteğe bağlı bir çerez bulunmadığı için bir tercih bandı gösterilmez. Analitik ya da reklam çerezi eklenirse, onay alınmadan önce çalıştırılmayacak şekilde bir onay bandı da eklenmelidir ve bu sayfa güncellenmelidir.",
        },
      ],
    },
    {
      id: "yonetim",
      title: "Çerezleri yönetmek",
      blocks: [
        {
          type: "p",
          text: "Çerezleri tarayıcınızın ayarlarından silebilir ya da engelleyebilirsiniz. Zorunlu çerezleri engellerseniz giriş yapamaz, sepete ürün ekleyemez ve sipariş veremezsiniz; ürünlere göz atmaya devam edebilirsiniz.",
        },
      ],
    },
  ],
};

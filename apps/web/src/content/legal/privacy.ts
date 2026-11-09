import type { LegalDocument } from "./types";

// Veri envanteri uygulamanın gerçek davranışına göre yazılmıştır (backend Domain varlıkları, kimlik ve güvenlik ayarları, web sunucusu günlükleri).
export const privacy: LegalDocument = {
  slug: "gizlilik-kvkk",
  navLabel: "Gizlilik ve KVKK",
  title: "Gizlilik ve KVKK aydınlatma metni",
  description:
    "SSCommerce demo vitrininde hangi kişisel verilerin neden işlendiğini, nasıl saklandığını ve KVKK m.11 haklarınızı anlatan örnek aydınlatma metni.",
  lead: "6698 sayılı Kişisel Verilerin Korunması Kanunu'nun 10. maddesi biçiminde hazırlanmış örnek bir aydınlatma metnidir. Yalnızca bu demo vitrinin gerçekten işlediği verileri anlatır.",
  toc: true,
  sections: [
    {
      id: "veri-sorumlusu",
      title: "Veri sorumlusu",
      blocks: [
        {
          type: "p",
          text: "Kişisel verileriniz bu demo vitrinde veri sorumlusu sıfatıyla “SSCommerce (demo)” tarafından işlenir. Bu ad kurgusaldır: arkasında gerçek bir şirket, vergi numarası, MERSİS ya da VERBİS kaydı yoktur. Örnek iletişim bilgileri İletişim sayfasındadır.",
        },
      ],
    },
    {
      id: "islenen-veriler",
      title: "İşlenen kişisel veriler",
      blocks: [
        {
          type: "p",
          text: "Aşağıdaki tablo uygulamanın gerçekte sakladığı verileri sıralar. Listede olmayan bir veri toplanmaz.",
        },
        {
          type: "table",
          caption: "İşlenen kişisel veri grupları",
          head: ["Veri grubu", "Hangi veriler", "Ne zaman oluşur"],
          rows: [
            [
              "Hesap",
              "E-posta adresi; parola (yalnızca geri döndürülemeyen özet olarak, parolanın kendisi saklanmaz); rol (müşteri, depo, yönetici); üyelik tarihi; başarısız giriş sayacı ve geçici kilit zamanı; oturum güvenlik damgası.",
              "Kayıt olduğunuzda; giriş, parola değişikliği ve çıkışta güncellenir.",
            ],
            [
              "Teslimat adresleri",
              "Adres etiketi, alıcı adı, telefon, il, ilçe, açık adres, posta kodu (varsa) ve varsayılan adres işareti.",
              "Hesabım sayfasından ya da ilk siparişte adres eklediğinizde.",
            ],
            [
              "Sepet",
              "Seçtiğiniz ürün seçenekleri ve adetleri (hesabınıza bağlı, sunucuda).",
              "Sepete ürün eklediğinizde.",
            ],
            ["Favoriler", "Favoriye aldığınız ürünler.", "Kalp simgesine bastığınızda."],
            [
              "Siparişler",
              "Sipariş numarası, kalemler, fiyatlar ve tutarlar; sipariş anındaki teslimat adresinin kopyası (alıcı adı, telefon, adres); sipariş durumu ve durum geçmişi (zaman, değişikliği yapan kullanıcının kimliği, varsa iptal gerekçesi); aynı siparişin iki kez oluşmasını önleyen rastgele anahtar.",
              "Sipariş verdiğinizde; sipariş ilerledikçe ya da iptal edildiğinde güncellenir.",
            ],
            [
              "Güvenlik kayıtları",
              "Giriş, çıkış ve parola yenileme olaylarında kullanıcı kimliği (rastgele uuid; e-posta değil). Web sunucusu erişim günlüğünde yöntem, yol (sorgu dizesi olmadan), durum kodu, süre ve istek kimliği; IP adresi günlüğe yazılmaz.",
              "Siteyi kullandıkça.",
            ],
            [
              "Hız sınırı",
              "Aşırı sık isteği durdurmak için IP adresi, istek sırasında bellekte kısa süreli tutulur; veritabanına ya da günlüğe yazılmaz.",
              "Her istekte, yalnızca sınırlama penceresi boyunca.",
            ],
            [
              "Yönetim denetim kaydı",
              "Yalnızca yönetici ve depo personeli için: işlemi yapanın kimliği, eylem, hedef kayıt ve zaman.",
              "Yetkili hesap yönetim ekranında değişiklik yaptığında.",
            ],
          ],
        },
        {
          type: "p",
          text: "Ödeme verisi (kart, banka, IBAN) işlenmez, çünkü bu vitrinde ödeme alınmaz. Reklam ya da analitik izleyicisi, davranış profili ve konum verisi de yoktur.",
        },
      ],
    },
    {
      id: "amaclar",
      title: "İşleme amaçları",
      blocks: [
        {
          type: "ul",
          items: [
            "Üyelik oluşturmak, kimliğinizi doğrulamak ve oturumunuzu yönetmek.",
            "Sepet, favori ve teslimat adresi gibi alışveriş işlevlerini sunmak.",
            "Siparişinizi kaydetmek, stoğu ayırmak, durumunu izletmek ve iptal edilirse stoğu geri vermek.",
            "Hesaplara yönelik deneme-yanılma saldırılarını ve aşırı yüklenmeyi önlemek (hız sınırı, geçici hesap kilidi).",
            "Hataları bulmak ve sistemin çalıştığını izlemek.",
          ],
        },
      ],
    },
    {
      id: "hukuki-sebepler",
      title: "Hukuki sebepler",
      blocks: [
        { type: "p", text: "Veriler KVKK m.5/2 kapsamındaki şu sebeplere dayanılarak işlenir:" },
        {
          type: "ul",
          items: [
            "Bir sözleşmenin kurulması veya ifasıyla doğrudan ilgili olması (m.5/2-c): hesap, sepet, adres ve sipariş verileri.",
            "Veri sorumlusunun hukuki yükümlülüğünü yerine getirebilmesi için zorunlu olması (m.5/2-ç): gerçek bir işletmede sipariş ve fatura kayıtlarının yasal süre saklanması.",
            "Temel hak ve özgürlüklerinize zarar vermemek kaydıyla veri sorumlusunun meşru menfaati (m.5/2-f): güvenlik kayıtları ve hız sınırı.",
          ],
        },
        {
          type: "p",
          text: "Bu vitrin açık rızaya dayanan bir işleme yapmaz; pazarlama iletisi göndermez ve izleyici kullanmaz.",
        },
      ],
    },
    {
      id: "aktarim",
      title: "Verilerin aktarılması",
      blocks: [
        {
          type: "p",
          text: "Verileriniz üçüncü kişilere aktarılmaz. Ödeme kuruluşu, kargo firması, reklam ya da analitik sağlayıcısı gibi bir entegrasyon yoktur. Yazı tipleri ve görseller site tarafından kendi sunucusundan verilir; sayfayı açarken sizin adınıza dış bir hizmete istek gönderilmez.",
        },
        {
          type: "p",
          text: "Veriler yazarın kendi altyapısında çalışan bir veritabanında tutulur. Yurt dışına veri aktaran bir hizmet kullanılmaz.",
        },
      ],
    },
    {
      id: "saklama",
      title: "Saklama süresi",
      blocks: [
        {
          type: "p",
          text: "Bu bir demo olduğundan veriler için kesin bir saklama süresi ya da otomatik silme düzeni tanımlanmamıştır. Veriler, demo ortamı proje sahibi tarafından sıfırlanana kadar kalır. Uygulamada hesap silme ekranı yoktur.",
        },
        {
          type: "ul",
          items: [
            "Oturum çerezi en çok 8 saat geçerlidir ve işlem yaptıkça uzar; tarayıcı kapanınca da biter.",
            "Adresi Hesabım sayfasından silebilir, favorileri tek tek kaldırabilir, sepetten ürün çıkarabilirsiniz. Verilmiş siparişin adres kopyası siparişle birlikte kalır.",
            "Gerçek bir işletmede sipariş kayıtları yasal saklama süresi boyunca tutulur, süre sonunda silinir ya da anonim hale getirilirdi.",
          ],
        },
      ],
    },
    {
      id: "guvenlik",
      title: "Veri güvenliği",
      blocks: [
        {
          type: "ul",
          items: [
            "Parolalar tuzlanmış ve yavaş bir özet algoritmasıyla saklanır; kimse parolanızı göremez.",
            "Oturum ve CSRF çerezleri HttpOnly'dir; sayfadaki betikler okuyamaz.",
            "Beş yanlış girişten sonra hesap 15 dakika kilitlenir; parola değişince diğer oturumlar kapanır.",
            "IP başına hız sınırı uygulanır. Kişisel veri içeren sayfalar önbelleğe alınmaz.",
            "Kişisel veriler (adres, telefon) uygulama günlüklerine ve adres çubuğuna yazılmaz.",
          ],
        },
      ],
    },
    {
      id: "haklariniz",
      title: "Haklarınız (KVKK m.11)",
      blocks: [
        { type: "p", text: "Veri sahibi olarak şu haklara sahipsiniz:" },
        {
          type: "ol",
          items: [
            "Kişisel verinizin işlenip işlenmediğini öğrenmek,",
            "İşlenmişse buna ilişkin bilgi talep etmek,",
            "İşlenme amacını ve amacına uygun kullanılıp kullanılmadığını öğrenmek,",
            "Yurt içinde ya da yurt dışında aktarıldığı üçüncü kişileri bilmek,",
            "Eksik veya yanlış işlenmişse düzeltilmesini istemek,",
            "KVKK m.7 çerçevesinde silinmesini veya yok edilmesini istemek ve bunun aktarılan üçüncü kişilere bildirilmesini talep etmek,",
            "İşlenen verilerin yalnızca otomatik sistemlerle analiz edilmesi nedeniyle aleyhinize bir sonuç çıkmasına itiraz etmek,",
            "Kanuna aykırı işleme nedeniyle zarara uğrarsanız zararın giderilmesini talep etmek.",
          ],
        },
        {
          type: "p",
          text: "Başvurular veri sorumlusuna yazılı olarak ya da güvenli elektronik imzayla iletilir. Bu demoda başvuruyu alacak gerçek bir kişi ya da kanal yoktur; İletişim sayfasındaki adres örnektir. Hesabınızın adres ve parola bilgilerini Hesabım sayfasından zaten kendiniz düzenleyebilirsiniz.",
        },
      ],
    },
    {
      id: "degisiklikler",
      title: "Değişiklikler",
      blocks: [
        {
          type: "p",
          text: "Uygulamanın veri işleme biçimi değişirse bu metin güncellenir. Sayfanın sonundaki tarih son güncelleme tarihidir.",
        },
      ],
    },
  ],
};

import type { LegalDocument } from "./types";

export const contact: LegalDocument = {
  slug: "iletisim",
  navLabel: "İletişim",
  title: "İletişim",
  description: "SSCommerce demo vitrininin örnek iletişim bilgileri. Adres, telefon ve e-posta kurgusaldır.",
  lead: "Aşağıdaki bilgiler örnektir. Adres, telefon numarası ve e-posta kurgusaldır; bu adreslere yazılan iletileri kimse okumaz.",
  toc: false,
  sections: [
    {
      id: "bilgiler",
      title: "İletişim bilgileri",
      blocks: [
        {
          type: "facts",
          items: [
            { label: "E-posta", value: "destek@sscommerce.example" },
            { label: "Telefon", value: "0850 000 00 00 (örnek numara)" },
            { label: "Çalışma saatleri", value: "Hafta içi 09.00 – 18.00 (örnek)" },
            { label: "Adres", value: "Örnek Mah. Demo Sok. No:1, İstanbul (kurgusal adres)" },
          ],
        },
        {
          type: "p",
          text: "Bu demoda iletişim formu yoktur ve bu bilgilerin arkasında çalışan bir destek ekibi bulunmaz. Gerçek bir şirket, kişi, vergi numarası ya da MERSİS kaydı da yoktur. example uzantısı, adresin bilerek geçersiz bırakıldığını gösterir.",
        },
      ],
    },
    {
      id: "hesap-siparis",
      title: "Hesap ve sipariş işleri",
      blocks: [
        {
          type: "p",
          text: "Gerçek bir mağazada sipariş ve hesap sorularınız bu kanallardan karşılanırdı. Burada ise her işi kendiniz yapabilirsiniz: Hesabım sayfasından adreslerinizi ve parolanızı yönetebilir, Siparişlerim sayfasından siparişinizin durumuna bakabilir ve henüz hazırlanmaya başlamamış bir siparişi iptal edebilirsiniz.",
        },
      ],
    },
    {
      id: "kisisel-veri",
      title: "Kişisel verilerle ilgili başvurular",
      blocks: [
        {
          type: "p",
          text: "Kişisel veri haklarınıza ilişkin bilgi Gizlilik ve KVKK sayfasındadır. Bu demoda başvuru alacak gerçek bir kanal bulunmadığı için orada da örnek iletişim bilgisi kullanılır.",
        },
      ],
    },
  ],
};

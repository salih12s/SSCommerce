// Yasal ve kurumsal sayfaların içerik şeması. Saf veri: bileşen ya da özellik içe aktarmaz (D-087: content ortak katmandır).
// Hepsi ÖRNEK metindir: portföy vitrini için yazılmıştır, gerçek bir işletmeye ait değildir.
export type LegalBlock =
  | { type: "p"; text: string }
  | { type: "ul"; items: string[] }
  | { type: "ol"; items: string[] }
  | { type: "note"; title: string; text: string }
  | { type: "facts"; items: { label: string; value: string }[] }
  | { type: "table"; caption: string; head: string[]; rows: string[][] };

export type LegalSection = { id: string; title: string; blocks: LegalBlock[] };

export type LegalDocument = {
  slug: string;
  /** Alt bilgi bağlantısında görünen kısa ad. */
  navLabel: string;
  title: string;
  description: string;
  /** Başlığın altındaki kısa özet. */
  lead: string;
  /** true: sayfa başında içindekiler ve numaralı başlıklar. */
  toc: boolean;
  sections: LegalSection[];
};

/** Her sayfanın başındaki uyarı (tam metin, birebir). */
export const SAMPLE_NOTICE =
  "Bu metin bir portföy projesi için hazırlanmış örnektir; gerçek bir işletmeye ait değildir ve hukuki danışmanlık yerine geçmez.";

export const LAST_UPDATED = { iso: "2026-11-08", text: "8 Kasım 2026" } as const;

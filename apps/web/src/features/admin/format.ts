// Tarih/yaş biçimleri. Sunucu UTC verir; gösterim Europe/Istanbul saatindedir (lib/format.ts). Yalnız istemcide (veri geldikten sonra) çağrılır.
import { formatDateTimeIstanbul } from "@/lib/format";
export const formatDateTime = (iso: string) => formatDateTimeIstanbul(iso, "short");

/** Verilen andan "şimdi"ye geçen süre: "12 dk", "3 sa 20 dk", "2 gün 4 sa". */
export function formatAge(fromIso: string, now: number): string {
  const minutes = Math.max(0, Math.floor((now - new Date(fromIso).getTime()) / 60_000));
  if (minutes < 1) return "1 dakikadan az";
  if (minutes < 60) return `${minutes} dk`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} sa ${minutes % 60} dk`;
  return `${Math.floor(hours / 24)} gün ${hours % 24} sa`;
}

/** Türkçe karakterleri sadeleştirip slug önerisi üretir (kullanıcı değiştirebilir; sunucu yine doğrular). */
export function slugify(text: string): string {
  return text.toLocaleLowerCase("tr-TR").replace(/ı/g, "i").replace(/ğ/g, "g").replace(/ü/g, "u").replace(/ş/g, "s").replace(/ö/g, "o").replace(/ç/g, "c")
    .replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 120);
}

export const opLabel: Record<string, string> = {
  receiving: "Mal girişi", adjustment: "Düzeltme", correction: "Hata düzeltmesi", order_placement: "Sipariş düşümü", order_cancellation: "Sipariş iadesi",
};
export const actorKindLabel: Record<string, string> = { customer: "Müşteri", staff: "Personel", warehouse: "Depo", admin: "Yönetici", system: "Sistem" };
export const actionLabel: Record<string, string> = {
  "category.create": "Kategori oluşturuldu", "category.update": "Kategori güncellendi", "product.create": "Ürün oluşturuldu", "product.update": "Ürün güncellendi",
  "product.images_replace": "Ürün görselleri değişti", "variant.create": "Varyant oluşturuldu", "variant.update": "Varyant güncellendi", "warehouse.create": "Depo oluşturuldu",
  "warehouse.update": "Depo güncellendi", "role.grant": "Rol verildi", "role.revoke": "Rol alındı", "role.bootstrap": "İlk yönetici atandı", "order.delivery_view": "Teslimat bilgisi görüntülendi",
};
export const targetLabel: Record<string, string> = { category: "Kategori", product: "Ürün", variant: "Varyant", warehouse: "Depo", user: "Hesap", order: "Sipariş" };

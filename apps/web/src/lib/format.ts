// Yalnızca görüntüleme biçimi. Fiyat hesabı yapılmaz; tutar API/fixture'dan kuruş olarak gelir.
const tryFormatter = new Intl.NumberFormat("tr-TR", { style: "currency", currency: "TRY" });

export function formatPriceMinor(priceMinor: number): string {
  return tryFormatter.format(priceMinor / 100);
}

/** Tarih ve saat: saklama UTC, gösterim Europe/Istanbul ve Türkçe. `style: "long"` → "8 Kasım 2026 12:00"; "short" → "08.11.2026 12:00". Geçersiz değerde ham metin. */
export function formatDateTimeIstanbul(iso: string, style: "long" | "short" = "long"): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  return date.toLocaleString("tr-TR", { dateStyle: style, timeStyle: "short", timeZone: "Europe/Istanbul" });
}

/** Tarih (saat yok): saklama UTC, gösterim Europe/Istanbul ve Türkçe ("8 Kasım 2026"). Geçersiz değerde null (satır gizlenir). */
export function formatDateIstanbul(iso: string): string | null {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return null;
  return date.toLocaleDateString("tr-TR", { dateStyle: "long", timeZone: "Europe/Istanbul" });
}

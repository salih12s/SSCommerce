// Yalnızca görüntüleme biçimi. Fiyat hesabı yapılmaz; tutar API/fixture'dan kuruş olarak gelir.
const tryFormatter = new Intl.NumberFormat("tr-TR", { style: "currency", currency: "TRY" });

export function formatPriceMinor(priceMinor: number): string {
  return tryFormatter.format(priceMinor / 100);
}

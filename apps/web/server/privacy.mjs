// Kişiye özel sayfalar `private, no-store` ile sunulur. Eşleşme ham URL metnine değil, ayrıştırılıp çözülmüş yol parçasına bakar:
// "/%73epet", "//sepet", "/sepet?x=1", "/hesabim/adresler" aynı kurala girer; bozuk yüzde kodlaması istisna fırlatmaz, güvenli tarafa (no-store) düşer.
const PRIVATE_FIRST_SEGMENTS = new Set(["sepet", "checkout", "hesabim", "siparisler", "giris", "kayit"]);

export function isPrivateRoute(rawUrl) {
  try {
    const text = typeof rawUrl === "string" ? rawUrl : "/";
    const pathname = new URL(`http://x${text.startsWith("/") ? "" : "/"}${text}`).pathname;
    const first = decodeURIComponent(pathname).split("/").filter(Boolean)[0];
    return first !== undefined && PRIVATE_FIRST_SEGMENTS.has(first.toLowerCase());
  } catch {
    return true;
  }
}

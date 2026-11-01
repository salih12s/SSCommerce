// Girişten sonra dönülecek adres yalnızca aynı site içi göreli yol olabilir (açık yönlendirme engeli).
// Reddedilenler: "//evil.com", "https://...", "/\evil.com" (tarayıcı "\" işaretini "/" sayar), satır sonu/sekme/boşluk (tarayıcılar bunları atar).
export const DEFAULT_NEXT = "/hesabim";
const PROBE = "http://same-site.invalid";

const bad = (text: string, spaceOk = false) => { for (const ch of text) { const code = ch.charCodeAt(0); if (ch === "\\" || code < 0x20 || code === 0x7f || (code === 0x20 && !spaceOk)) return true; } return false; };
const AUTH_PATHS = new Set(["/giris", "/kayit"]);

export function safeNext(value: string | null | undefined): string {
  if (typeof value !== "string" || value.length === 0 || value.length > 512) return DEFAULT_NEXT;
  if (!value.startsWith("/") || value.startsWith("//")) return DEFAULT_NEXT;
  // Yalnızca ASCII: tam genişlikli "／" gibi benzer karakterler normalize edilirken "/" olabilir.
  for (const ch of value) if (ch.charCodeAt(0) > 0x7e) return DEFAULT_NEXT;
  if (bad(value)) return DEFAULT_NEXT;
  // Yüzde kodlu biçimler de çözülüp denetlenir: "/%09/evil", "/%2F%2F", "/%5Cevil" tarayıcıya "//" ya da "\" olarak ulaşabilir.
  let decoded: string;
  try { decoded = decodeURIComponent(value); } catch { return DEFAULT_NEXT; }
  if (decoded.startsWith("//") || bad(decoded, true)) return DEFAULT_NEXT;
  let path: string;
  try {
    const url = new URL(value, PROBE);
    if (url.origin !== PROBE) return DEFAULT_NEXT;
    path = url.pathname.replace(/\/+$/, "") || "/";
  } catch { return DEFAULT_NEXT; }
  // Giriş/kayıt sayfasına geri yönlendirme döngü yaratır.
  if (AUTH_PATHS.has(path.toLowerCase())) return DEFAULT_NEXT;
  return value;
}

export const loginHref = (next: string) => `/giris?next=${encodeURIComponent(next)}`;
export const registerHref = (next: string) => `/kayit?next=${encodeURIComponent(next)}`;

// Sipariş niyeti ve Idempotency-Key. Aynı niyet (kullanıcı + adres + quote) aynı anahtarla tekrarlanır; böylece kayıp yanıttan sonra
// yeniden denemek ikinci sipariş yaratmaz (sunucu ilk siparişi 200 replayed ile döndürür). Anahtar yalnız şu durumlarda değişir:
// quote ya da adres değişti, sipariş başarıyla verildi ya da sunucu anahtarı reddetti (idempotency_key_reused).
// Sonucu BİLİNMEYEN bir deneme (ağ hatası, zaman aşımı, 5xx) anahtarı ASLA yenilemez: `unknown` işareti çözülene kadar kalır.
// Depoda kişisel veri yoktur: yalnız rastgele anahtar, kullanıcı/adres kimliği (uuid) ve sunucunun quote özeti tutulur.
const STORAGE_KEY = "ssc.order-intent";
const KEY_PATTERN = /^[A-Za-z0-9_-]{16,128}$/;
const HEX64 = /^[0-9a-f]{64}$/;

export type OrderIntent = { userId: string; addressId: string; quote: string; key: string; unknown: boolean };

// sessionStorage kullanılamazsa (gizli mod, kota, politika) anahtar sayfanın ömrü boyunca bellekte kalır.
let memory: OrderIntent | null = null;

function valid(value: unknown): value is OrderIntent {
  if (typeof value !== "object" || value === null) return false;
  const v = value as Record<string, unknown>;
  return typeof v.userId === "string" && typeof v.addressId === "string" && typeof v.quote === "string" && HEX64.test(v.quote)
    && typeof v.key === "string" && KEY_PATTERN.test(v.key) && typeof v.unknown === "boolean";
}

function read(): OrderIntent | null {
  try {
    const raw = window.sessionStorage.getItem(STORAGE_KEY);
    if (raw === null) return null;
    const parsed: unknown = JSON.parse(raw);
    return valid(parsed) ? parsed : null;
  } catch { return memory; }
}
function write(intent: OrderIntent | null) {
  memory = intent;
  try {
    if (intent === null) window.sessionStorage.removeItem(STORAGE_KEY);
    else window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify(intent));
  } catch { /* depo yok: bellekteki kopya geçerli */ }
}

/** randomUUID yalnız güvenli bağlamda vardır (HTTPS/localhost); yoksa getRandomValues ile 128 bit rastgele, tire/harf/rakamdan oluşan anahtar üretilir. */
export function newIdempotencyKey(): string {
  const c = globalThis.crypto;
  if (typeof c?.randomUUID === "function") return c.randomUUID();
  const bytes = new Uint8Array(16);
  c.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

/** Bu niyet için kayıtlı anahtar varsa onu, yoksa (ya da niyet değiştiyse) yenisini kaydedip döndürür. */
export function intentFor(userId: string, addressId: string, quote: string): OrderIntent {
  const current = read();
  if (current && current.userId === userId && current.addressId === addressId && current.quote === quote) return current;
  const next: OrderIntent = { userId, addressId, quote, key: newIdempotencyKey(), unknown: false };
  write(next);
  return next;
}

/** Bu kullanıcının sonucu çözülmemiş denemesi (varsa). Yeni anahtar üretmeden önce bu çözülmelidir. */
export function pendingIntent(userId: string): OrderIntent | null {
  const current = read();
  return current && current.userId === userId && current.unknown ? current : null;
}
/** Kayıtlı niyet (işaretten bağımsız); yalnız okuma. */
export function storedIntent(): OrderIntent | null { return read(); }

export function markUnknown(intent: OrderIntent) { write({ ...intent, unknown: true }); }
/** Sunucu "oluşmadı" dedi (red): anahtar yeniden kullanılabilir ama artık belirsiz değil. */
export function markSettled(intent: OrderIntent) { write({ ...intent, unknown: false }); }
/** Sipariş başarıyla verildi ya da anahtar reddedildi: bir sonraki niyet yeni anahtar alır. */
export function clearIntent() { write(null); }

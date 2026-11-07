// Stok giriş/düzeltme niyeti ve Idempotency-Key (D-066 deseninin yönetim karşılığı, lib/order-key.ts ile aynı kurallar).
// Aynı niyet (kullanıcı + işlem + içerik) aynı anahtarla tekrarlanır; kayıp yanıttan sonra yeniden göndermek ikinci hareket yazmaz (sunucu ilk sonucu 200 replayed döndürür).
// Sonucu BİLİNMEYEN bir deneme (ağ hatası, zaman aşımı, busy dışı 5xx) anahtarı ASLA yenilemez: `unknown` işareti kesin bir yanıt gelene kadar kalır
// ve arayüz yeni işlem başlatmayı kapatır. Anahtar yalnız kesin yanıttan (başarı ya da red) sonra, ya da içerik değişince yenilenir.
// Depoda yalnız anahtar, kullanıcı kimliği (uuid) ve işlemin kendisi (depo/varyant kimliği, adet, neden, referans) vardır; e-posta, ad, adres yoktur.
const STORAGE_KEY = "ssc.stock-intent";
const KEY_PATTERN = /^[A-Za-z0-9_-]{16,128}$/;

export type StockOp = "receive" | "adjust";
/** label: yalnız ekranda "hangi işlem belirsiz" göstermek içindir (SKU, depo kodu, adet); isteğe girmez. */
export type StockIntent = { userId: string; op: StockOp; payload: string; label: string; key: string; unknown: boolean };

// sessionStorage kullanılamazsa (gizli mod, kota, politika) niyet sayfanın ömrü boyunca bellekte kalır.
let memory: StockIntent | null = null;

function valid(value: unknown): value is StockIntent {
  if (typeof value !== "object" || value === null) return false;
  const v = value as Record<string, unknown>;
  return typeof v.userId === "string" && (v.op === "receive" || v.op === "adjust") && typeof v.payload === "string" && v.payload.length <= 4096 && typeof v.label === "string" && v.label.length <= 300
    && typeof v.key === "string" && KEY_PATTERN.test(v.key) && typeof v.unknown === "boolean";
}
function read(): StockIntent | null {
  try {
    const raw = window.sessionStorage.getItem(STORAGE_KEY);
    if (raw === null) return memory;
    const parsed: unknown = JSON.parse(raw);
    return valid(parsed) ? parsed : null;
  } catch { return memory; }
}
function write(intent: StockIntent | null) {
  memory = intent;
  try {
    if (intent === null) window.sessionStorage.removeItem(STORAGE_KEY);
    else window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify(intent));
  } catch { /* depo yok: bellekteki kopya geçerli */ }
}

/** "order-" ile başlamaz (sunucu sipariş anahtarlarına ayırmıştır). randomUUID yoksa getRandomValues ile 128 bit rastgele. */
export function newStockKey(): string {
  const c = globalThis.crypto;
  if (typeof c?.randomUUID === "function") return `stk-${c.randomUUID()}`;
  const bytes = new Uint8Array(16);
  c.getRandomValues(bytes);
  return `stk-${Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("")}`;
}

/** Bu niyet için kayıtlı anahtar varsa onu, yoksa (ya da içerik değiştiyse) yenisini kaydedip döndürür. */
export function stockIntentFor(userId: string, op: StockOp, payload: string, label: string): StockIntent {
  const current = read();
  if (current && current.userId === userId && current.op === op && current.payload === payload) return current;
  const next: StockIntent = { userId, op, payload, label: label.slice(0, 300), key: newStockKey(), unknown: false };
  write(next);
  return next;
}
/** Bu kullanıcının sonucu çözülmemiş denemesi (varsa). Yeni işlem başlatmadan önce bu çözülmelidir. */
export function pendingStockIntent(userId: string): StockIntent | null {
  const current = read();
  return current && current.userId === userId && current.unknown ? current : null;
}
export function markStockUnknown(intent: StockIntent) { write({ ...intent, unknown: true }); }
/** Sunucu "uygulanmadı" dedi (red): anahtar yeniden kullanılabilir ama artık belirsiz değil. */
export function markStockSettled(intent: StockIntent) { write({ ...intent, unknown: false }); }
/** İşlem uygulandı (ya da replay döndü): sıradaki niyet yeni anahtar alır. */
export function clearStockIntent() { write(null); }
/** Çıkışta çağrılır (auth-client logout). */
export function clearStockIntents() { clearStockIntent(); }

/** Sonucu belirsiz yanıt: ağ hatası/zaman aşımı (istek gönderildiyse) ve busy/stock_busy/429 dışındaki 5xx. */
export function isStockUnknown(result: { ok: boolean; status?: number; code?: string; sent?: boolean }): boolean {
  if (result.ok) return false;
  if (result.status === 0) return result.sent === true;
  if (result.status === 503 && (result.code === "busy" || result.code === "stock_busy")) return false; // sözleşme: hiçbir şey yazılmadı
  return (result.status ?? 0) >= 500;
}

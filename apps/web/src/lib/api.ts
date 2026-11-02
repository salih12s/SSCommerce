// Tarayıcıdan same-origin /api/backend üzerinden backend'e istek: çerez kimliği + yazmalarda her seferinde taze CSRF belirteci
// (auth-client.ts ile aynı desen). Para ve toplamlar burada hesaplanmaz.
// sent: asıl istek sunucuya gönderildi mi (CSRF alınamadıysa false: sunucu hiçbir şey görmedi). retryAfter: 429/503'teki Retry-After saniyesi.
export type ApiFailure = { ok: false; status: number; code: string; message: string; errors: Record<string, string[]>; extra: Record<string, unknown>; sent: boolean; retryAfter: number | null };
export type ApiResult<T> = { ok: true; data: T } | ApiFailure;
export const API_ROOT = "/api/backend/api/v1";

const messages: Record<string, string> = {
  unauthenticated: "Oturumun sona erdi. Lütfen yeniden giriş yap.",
  variant_not_found: "Bu ürün seçeneği bulunamadı.",
  variant_inactive: "Bu ürün şu an satışta değil.",
  insufficient_stock: "İstenen adet için yeterli stok görünmüyor.",
  cart_quantity_limit: "Bir satırda en çok 20 adet olabilir.",
  cart_line_limit: "Sepette en çok 50 farklı ürün olabilir.",
  cart_total_too_large: "Sepet toplamı izin verilen üst sınırı aşıyor.",
  cart_item_not_found: "Bu satır sepetinde artık yok.",
  cart_empty: "Sepetin boş.",
  address_not_found: "Bu adres bulunamadı.",
  address_limit: "En çok 10 adres kaydedebilirsin.",
  service_unavailable: "Hizmet şu an kullanılamıyor. Birkaç dakika sonra tekrar dene; yaptığın işlemin sonucu doğrulanamadı.",
  busy: "Sistem şu an meşgul. Birkaç saniye sonra tekrar dene.",
  rate_limited: "Çok fazla istek gönderildi. Biraz bekleyip tekrar dene.",
  csrf_invalid: "İstek güvenlik kontrolünden geçmedi. Sayfayı yenileyip tekrar dene.",
  origin_not_allowed: "İstek güvenlik kontrolünden geçmedi. Sayfayı yenileyip tekrar dene.",
  validation_failed: "Girilen bilgileri kontrol et.",
  forbidden: "Bu işlem için yetkin yok.",
  order_not_found: "Sipariş bulunamadı.",
  order_quote_changed: "Fiyatlar ya da sepet değişti. Güncel özeti inceleyip siparişi yeniden onayla.",
  order_item_inactive: "Sepetteki bazı ürünler artık satışta değil.",
  idempotency_key_reused: "Bu sipariş denemesi başka bir içerikle daha önce kullanılmış. Özet yenilendi; siparişi yeniden onayla.",
  order_transition_invalid: "Siparişin durumu değişmiş; bu işlem artık yapılamıyor.",
  stock_limit_exceeded: "Stok iadesi şu an yapılamadı; sipariş değişmedi. Biraz sonra tekrar dene.",
  timeout: "Yanıt zamanında gelmedi.",
};

async function failure(response: Response, sent = true): Promise<ApiFailure> {
  let body: Record<string, unknown> = {};
  try { body = await response.json() as Record<string, unknown>; } catch { /* gövde yok */ }
  const code = typeof body.code === "string" ? body.code : `http_${response.status}`;
  const errors = (body.errors && typeof body.errors === "object" ? body.errors : {}) as Record<string, string[]>;
  const message = messages[code] ?? (response.status >= 500 ? "Sunucu şu an yanıt veremiyor. Lütfen tekrar dene." : "İşlem tamamlanamadı. Lütfen tekrar dene.");
  const raw = Number(response.headers.get("Retry-After"));
  const retryAfter = Number.isFinite(raw) && raw > 0 && raw <= 3600 ? Math.ceil(raw) : null;
  return { ok: false, status: response.status, code, message, errors, extra: body, sent, retryAfter };
}
const offlineFailure = (sent: boolean, timedOut = false): ApiFailure => ({
  ok: false, status: 0, code: timedOut ? "timeout" : "network", sent, retryAfter: null, errors: {}, extra: {},
  message: timedOut ? messages.timeout : "Sunucuya bağlanılamadı. Bağlantını kontrol edip tekrar dene.",
});
const offline = offlineFailure(true);

export async function apiGet<T>(path: string): Promise<ApiResult<T>> {
  try {
    const response = await fetch(`${API_ROOT}${path}`, { credentials: "same-origin", cache: "no-store" });
    return response.ok ? { ok: true, data: await response.json() as T } : await failure(response);
  } catch { return offline; }
}

/** headers: ek başlıklar (örn. Idempotency-Key). timeoutMs: istek (gövde okuma dahil) bu sürede bitmezse iptal edilir ve status 0, code "timeout" döner. */
export type SendOptions = { headers?: Record<string, string>; timeoutMs?: number };

export async function apiSend<T>(method: "POST" | "PUT" | "PATCH" | "DELETE", path: string, body?: unknown, options: SendOptions = {}): Promise<ApiResult<T>> {
  let sent = false;
  const controller = new AbortController();
  let timedOut = false;
  const timer = options.timeoutMs === undefined ? null : setTimeout(() => { timedOut = true; controller.abort(); }, options.timeoutMs);
  try {
    const tokenResponse = await fetch(`${API_ROOT}/auth/csrf`, { credentials: "same-origin", cache: "no-store", signal: controller.signal });
    if (!tokenResponse.ok) return await failure(tokenResponse, false);
    const { token } = await tokenResponse.json() as { token: string };
    sent = true;
    const response = await fetch(`${API_ROOT}${path}`, {
      method, credentials: "same-origin", cache: "no-store", signal: controller.signal,
      headers: { "X-CSRF-TOKEN": token, ...(options.headers ?? {}), ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) return await failure(response);
    return { ok: true, data: (response.status === 204 ? undefined : await response.json()) as T };
  } catch { return offlineFailure(sent, timedOut); }
  finally { if (timer !== null) clearTimeout(timer); }
}

/** CSRF belirtecinin alınabildiğini doğrular (yazma düğmesini açmadan önce). Belirteç saklanmaz: her yazma kendi taze belirtecini alır. */
export const checkCsrf = () => apiGet<{ token: string }>("/auth/csrf");

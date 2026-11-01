// Tarayıcıdan same-origin /api/backend üzerinden backend'e istek: çerez kimliği + yazmalarda her seferinde taze CSRF belirteci
// (auth-client.ts ile aynı desen). Para ve toplamlar burada hesaplanmaz.
export type ApiFailure = { ok: false; status: number; code: string; message: string; errors: Record<string, string[]>; extra: Record<string, unknown> };
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
};

async function failure(response: Response): Promise<ApiFailure> {
  let body: Record<string, unknown> = {};
  try { body = await response.json() as Record<string, unknown>; } catch { /* gövde yok */ }
  const code = typeof body.code === "string" ? body.code : `http_${response.status}`;
  const errors = (body.errors && typeof body.errors === "object" ? body.errors : {}) as Record<string, string[]>;
  const message = messages[code] ?? (response.status >= 500 ? "Sunucu şu an yanıt veremiyor. Lütfen tekrar dene." : "İşlem tamamlanamadı. Lütfen tekrar dene.");
  return { ok: false, status: response.status, code, message, errors, extra: body };
}
const offline: ApiFailure = { ok: false, status: 0, code: "network", message: "Sunucuya bağlanılamadı. Bağlantını kontrol edip tekrar dene.", errors: {}, extra: {} };

export async function apiGet<T>(path: string): Promise<ApiResult<T>> {
  try {
    const response = await fetch(`${API_ROOT}${path}`, { credentials: "same-origin", cache: "no-store" });
    return response.ok ? { ok: true, data: await response.json() as T } : await failure(response);
  } catch { return offline; }
}

export async function apiSend<T>(method: "POST" | "PUT" | "PATCH" | "DELETE", path: string, body?: unknown): Promise<ApiResult<T>> {
  try {
    const tokenResponse = await fetch(`${API_ROOT}/auth/csrf`, { credentials: "same-origin", cache: "no-store" });
    if (!tokenResponse.ok) return await failure(tokenResponse);
    const { token } = await tokenResponse.json() as { token: string };
    const response = await fetch(`${API_ROOT}${path}`, {
      method, credentials: "same-origin", cache: "no-store",
      headers: { "X-CSRF-TOKEN": token, ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) return await failure(response);
    return { ok: true, data: (response.status === 204 ? undefined : await response.json()) as T };
  } catch { return offline; }
}

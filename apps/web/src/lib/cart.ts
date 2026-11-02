import { useEffect, useSyncExternalStore } from "react";
import { apiGet, apiSend, type ApiResult } from "./api";

export type CartItemStatus = "available" | "insufficient_stock" | "out_of_stock" | "unavailable";
export type CartItem = {
  id: string; variantId: string; sku: string; productSlug: string; productName: string; variantName: string;
  imageUrl: string | null; imageAlt: string | null; quantity: number;
  unitPriceMinor: number | null; lineSubtotalMinor: number | null; previousUnitPriceMinor: number | null; priceChanged: boolean;
  /** En çok 20 ("en az bu kadar"); kesin depo miktarı değildir. */
  availableQuantity: number; status: CartItemStatus;
};
export type Cart = { items: CartItem[]; itemCount: number; subtotalMinor: number; currency: string; hasIssues: boolean };
export type CheckoutIssue = { code: "out_of_stock" | "insufficient_stock" | "unavailable" | "price_changed"; itemId?: string };
export type CheckoutPreview = {
  lines: CartItem[];
  address: { id: string; label: string; recipientName: string; phone: string; province: string; district: string; streetAddress: string; postalCode: string | null; isDefault: boolean };
  itemCount: number; subtotalMinor: number; shippingMinor: number | null; shippingStatus: string; grandTotalMinor: number | null; currency: string;
  canProceed: boolean; issues: CheckoutIssue[]; orderingAvailable: boolean; calculatedAt: string;
  /** Sunucunun bu sepet için hesapladığı fiyat onayı; yalnız canProceed iken dolu. POST /orders gövdesinde aynen geri gönderilir. */
  quote: string | null;
};

// Sepet istemci deposu: üst gezinti (sayaç), ürün sayfası ve /sepet aynı veriyi paylaşır. Sunucudan gelen HTML'de kişisel veri yoktur;
// sepet yalnızca tarayıcıda, oturum çerezi ile okunur. localStorage/sessionStorage kullanılmaz (sipariş niyeti anahtarı hariç: lib/order-key.ts).
export type CartState =
  | { status: "unknown" }
  | { status: "loading"; cart: Cart | null }
  | { status: "anonymous" }
  | { status: "ready"; cart: Cart }
  | { status: "error"; message: string; cart: Cart | null };

let state: CartState = { status: "unknown" };
let inflight: Promise<void> | null = null;
const listeners = new Set<() => void>();
const serverState: CartState = { status: "unknown" };
// generation: resetCart() ile artar; eski kimliğe ait hiçbir yanıt durumu değiştiremez.
// writeCount: uygulanan her yazma ile artar; yazmadan ÖNCE başlamış bir GET sonradan dönerse eskidir ve yok sayılır.
// getSeq: yalnızca en son başlayan GET uygulanır (zorla yenileme eskisini geçersiz kılar).
let generation = 0;
let writeCount = 0;
let getSeq = 0;
function set(next: CartState) { state = next; listeners.forEach((l) => l()); }
const lastCart = () => ("cart" in state ? state.cart : null);

export function loadCart(force = false): Promise<void> {
  if (!force && (state.status === "ready" || state.status === "anonymous")) return Promise.resolve();
  if (!force && inflight) return inflight;
  const gen = generation; const writes = writeCount; const seq = ++getSeq;
  set({ status: "loading", cart: lastCart() });
  const mine: Promise<void> = apiGet<Cart>("/cart").then((result) => {
    if (gen !== generation || writes !== writeCount || seq !== getSeq) return; // eski kimlik, eski GET ya da araya giren yazma
    if (result.ok) set({ status: "ready", cart: result.data });
    else if (result.status === 401) set({ status: "anonymous" });
    else set({ status: "error", message: result.message, cart: lastCart() });
  }).finally(() => { if (inflight === mine) inflight = null; });
  inflight = mine;
  return mine;
}
/** Giriş ya da çıkıştan (ve bfcache geri yüklemesinden) sonra çağrılır: önceki kimliğin sepeti bellekte kalmaz, geç gelen yanıt yok sayılır. */
export function resetCart() {
  generation++; inflight = null;
  set({ status: "unknown" });
  if (listeners.size > 0) void loadCart(true);
}

/** Yazma yanıtı: yalnızca başladığı kimlikte uygulanır. Ağ/5xx hatasında etki bilinmez: sepet zorla yeniden okunur ve hata `unconfirmed` işaretlenir. */
async function write(request: () => Promise<ApiResult<Cart>>): Promise<ApiResult<Cart> & { unconfirmed?: boolean }> {
  const gen = generation;
  const result = await request();
  if (gen !== generation) return result;
  if (result.ok) { writeCount++; set({ status: "ready", cart: result.data }); return result; }
  if (result.status === 401) { set({ status: "anonymous" }); return result; }
  if (result.status === 0 || result.status >= 500) { await loadCart(true); return { ...result, unconfirmed: true }; }
  return result;
}
export const UNCONFIRMED_MESSAGE = "İşlemin sonucu doğrulanamadı. Sepetin güncel hâli gösteriliyor; lütfen kontrol et.";
export const addToCart = (variantId: string, quantity: number) => write(() => apiSend<Cart>("POST", "/cart/items", { variantId, quantity }));
export const setCartQuantity = (itemId: string, quantity: number) => write(() => apiSend<Cart>("PATCH", `/cart/items/${encodeURIComponent(itemId)}`, { quantity }));
export const removeCartItem = (itemId: string) => write(() => apiSend<Cart>("DELETE", `/cart/items/${encodeURIComponent(itemId)}`));
export const previewCheckout = (addressId: string) => apiSend<CheckoutPreview>("POST", "/checkout/preview", { addressId });

export function useCart(options: { refresh?: boolean } = {}): CartState {
  const current = useSyncExternalStore((l) => { listeners.add(l); return () => { listeners.delete(l); }; }, () => state, () => serverState);
  const refresh = options.refresh ?? false;
  useEffect(() => { void loadCart(refresh); }, [refresh]);
  return current;
}

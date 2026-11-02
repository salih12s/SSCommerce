import { apiGet, apiSend, type ApiResult } from "./api";

// Sipariş sözleşmesi (docs/CONTRACT_PHASE_7.md, OrderContracts.cs). Para kuruştur ve burada hesaplanmaz; durum geçişi ve iptal izni sunucudandır.
export type OrderStatus = "placed" | "processing" | "shipped" | "completed" | "cancelled";
export const statusLabel: Record<OrderStatus, string> = {
  placed: "Alındı",
  processing: "Hazırlanıyor",
  shipped: "Kargoda",
  completed: "Tamamlandı",
  cancelled: "İptal edildi",
};
/** Bilinmeyen bir durum gelirse ham metin gösterilir (sunucu yeni durum eklerse ekran bozulmaz). */
export const labelOf = (status: string) => statusLabel[status as OrderStatus] ?? status;

export type OrderItem = {
  id: string; variantId: string; sku: string; productName: string; variantName: string; productSlug: string;
  imageUrl: string | null; imageAlt: string | null; unitPriceMinor: number; quantity: number; lineTotalMinor: number;
};
/** Sipariş anındaki adres kopyası: sonradan adres düzenlense de silinse de değişmez. */
export type OrderAddress = { label: string; recipientName: string; phone: string; province: string; district: string; streetAddress: string; postalCode: string | null };
export type OrderHistoryEntry = { fromStatus: string | null; toStatus: string; occurredAt: string };
export type Order = {
  id: string; orderNumber: string; status: string; createdAt: string; updatedAt: string; currency: string; itemCount: number;
  subtotalMinor: number; shippingMinor: number | null; shippingStatus: string; grandTotalMinor: number | null;
  items: OrderItem[]; deliveryAddress: OrderAddress; statusHistory: OrderHistoryEntry[]; canCancel: boolean; replayed: boolean;
};
export type OrderPreviewItem = { productName: string; variantName: string; imageUrl: string | null; imageAlt: string | null; quantity: number };
export type OrderSummary = {
  id: string; orderNumber: string; status: string; createdAt: string; currency: string; itemCount: number;
  subtotalMinor: number; shippingStatus: string; canCancel: boolean; previewItems: OrderPreviewItem[];
};
export type Paged<T> = { items: T[]; page: number; pageSize: number; totalCount: number; totalPages: number };

/** İstemci zaman aşımı: bu sürede yanıt gelmezse sonuç BİLİNMİYOR sayılır (sunucu siparişi yine de işlemiş olabilir). */
export const PLACE_TIMEOUT_MS = 15_000;
export const LIST_PAGE_SIZE = 10;

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export const isOrderId = (value: string) => uuid.test(value);

export const placeOrder = (addressId: string, quote: string, idempotencyKey: string): Promise<ApiResult<Order>> =>
  apiSend<Order>("POST", "/orders", { addressId, quote }, { headers: { "Idempotency-Key": idempotencyKey }, timeoutMs: PLACE_TIMEOUT_MS });
export const listOrders = (page: number) => apiGet<Paged<OrderSummary>>(`/orders?page=${page}&pageSize=${LIST_PAGE_SIZE}`);
export const getOrder = (id: string) => apiGet<Order>(`/orders/${encodeURIComponent(id)}`);
export const cancelOrder = (id: string) => apiSend<Order>("POST", `/orders/${encodeURIComponent(id)}/cancel`, undefined, { timeoutMs: PLACE_TIMEOUT_MS });

/** Sonucu belirsiz yanıt: ağ hatası/zaman aşımı (istek gönderildiyse) ve 503 busy dışındaki 5xx. Bu durumda sunucu işlemi yapmış olabilir. */
export function isUnknownOutcome(result: ApiResult<unknown>): boolean {
  if (result.ok) return false;
  if (result.status === 0) return result.sent;
  if (result.status === 503 && result.code === "busy") return false; // hiçbir şey yazılmadı (sözleşme)
  return result.status >= 500;
}

// Sipariş verildikten hemen sonra detay sayfasında "siparişin alındı" bilgisi gösterilsin diye bellekte bırakılan tek seferlik işaret.
// URL parametresi kullanılmaz (sorgu dizgesine güvenilmez); sayfa yenilenirse işaret kaybolur ve sayfa yalnız sunucu verisini gösterir.
const justPlaced = new Map<string, boolean>();
export const markJustPlaced = (orderId: string, replayed: boolean) => { justPlaced.set(orderId, replayed); };
export function takeJustPlaced(orderId: string): { replayed: boolean } | null {
  const value = justPlaced.get(orderId);
  justPlaced.delete(orderId);
  return value === undefined ? null : { replayed: value };
}

// Faz 8 yönetim sözleşmesinin (docs/CONTRACT_PHASE_8.md) istemci tipleri. Para kuruştur (`...Minor`), zaman UTC ISO'dur.
// Sözleşmenin alan listesini vermediği yanıtlar (ürün detayı, denetim satırı, personel) için alan adları varsayımdır; bkz. rapor "Açık konular".
import type { Paged } from "@/features/orders/orders";

export type { Paged };

export type Category = { id: string; name: string; slug: string; parentId: string | null; sortOrder: number; isActive: boolean; version: number; productCount: number; updatedAt: string };

export type ProductSummary = {
  id: string; name: string; slug: string; categoryId: string; categoryName: string; isActive: boolean; isDemo: boolean;
  variantCount: number; activeVariantCount: number; minPriceMinor: number | null; primaryImageUrl: string | null; version: number; updatedAt: string;
};
export type AdminVariant = { id: string; sku: string; displayName: string; options: Record<string, string>; priceMinor: number; isActive: boolean; version: number };
export type AdminImage = { storageKey: string; altText: string; isPrimary: boolean; sortOrder?: number; url?: string };
export type ProductDetail = {
  id: string; name: string; slug: string; brand: string | null; description: string | null; specifications: Record<string, string> | null;
  categoryId: string; categoryName?: string; isActive: boolean; isDemo: boolean; version: number; updatedAt?: string;
  variants: AdminVariant[]; images: AdminImage[];
};
export type ImageAsset = { storageKey: string; creditKey: string; author: string };
export type VariantHit = { id: string; sku: string; productName: string; displayName: string; isActive: boolean; productIsActive: boolean; onHandTotal: number };

export type Warehouse = { id: string; code: string; name: string; isActive: boolean; createdAt: string; updatedAt: string };
export type Balance = { warehouseId: string; warehouseCode: string; variantId: string; sku: string; onHandQuantity: number; version: number; updatedAt: string };
export type Movement = {
  id: string; warehouseId: string; variantId: string; sku: string; quantityDelta: number; operationType: string; reason: string; reference: string | null;
  onHandAfter: number; occurredAt: string; actorUserId: string; actorEmail: string | null; replayed: boolean;
};

export type AdminOrderSummary = { id: string; orderNumber: string; status: string; createdAt: string; updatedAt: string; currency: string; itemCount: number; subtotalMinor: number; shippingStatus: string };
export type AdminOrderItem = {
  id: string; variantId: string; sku: string; productName: string; variantName: string; productSlug: string; imageUrl: string | null; imageAlt: string | null;
  unitPriceMinor: number; quantity: number; lineTotalMinor: number;
};
export type AdminHistory = { fromStatus: string | null; toStatus: string; occurredAt: string; actorKind: string; reason: string | null };
export type AdminOrder = {
  id: string; orderNumber: string; status: string; createdAt: string; updatedAt: string; currency: string; itemCount: number; subtotalMinor: number;
  shippingMinor: number | null; shippingStatus: string; grandTotalMinor: number | null; items: AdminOrderItem[]; statusHistory: AdminHistory[]; replayed: boolean;
  allocations?: { sku: string; warehouseCode: string; quantity: number }[];
};
export type DeliveryView = { recipientName: string; phone: string; province: string; district: string; streetAddress: string; postalCode: string | null; label: string };

export type Dashboard = {
  generatedAt: string;
  orders: { placed: number; processing: number; shipped: number; completed: number; cancelled: number };
  openOrders: { count: number; unpaidSubtotalMinor: number; agedCount: number; agedHours: number; oldestCreatedAt: string | null };
  catalog: { products: number; activeProducts: number; variants: number; activeVariants: number };
  stock: { threshold: number; lowStockVariantCount: number; outOfStockVariantCount: number };
  agedOpenOrders: AdminOrderSummary[];
  recentMovements: Movement[];
  recentOrderEvents: { orderId: string; orderNumber: string; fromStatus: string | null; toStatus: string; actorKind: string; occurredAt: string }[];
};
export type LowStockRow = { variantId: string; sku: string; productName: string; displayName: string; onHandTotal: number };
export type AuditRow = { id: string; actorEmail: string | null; actorUserId?: string; action: string; targetType: string; targetId: string; changes: Record<string, unknown>; occurredAt: string };
export type StaffUser = { id: string; email: string; emailConfirmed: boolean; createdAt: string; roles: string[] };
export type RoleChange = { changed: boolean; roles: string[] };

/** GET listesi dizi ya da sayfa nesnesi olabilir; ikisi de güvenle diziye çevrilir. */
export function listOf<T>(value: T[] | { items?: T[] } | null | undefined): T[] {
  if (Array.isArray(value)) return value;
  return value && Array.isArray(value.items) ? value.items : [];
}

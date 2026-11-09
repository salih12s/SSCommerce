import "server-only";

export type Category = { id: string; slug: string; name: string; parentSlug: string | null; sortOrder: number };
export type ProductListItem = {
  id: string;
  slug: string;
  name: string;
  category: { slug: string; name: string };
  primaryImage: { url: string; altText: string } | null;
  startingPriceMinor: number;
  currency: string;
  isDemo: boolean;
  /** Herhangi bir aktif varyant stokta mı. Stoksuz ürün listede kalır. */
  inStock: boolean;
};
export type ProductPage = { items: ProductListItem[]; page: number; pageSize: number; totalCount: number; totalPages: number };
export type ProductDetail = Omit<ProductListItem, "primaryImage"> & {
  brand: string | null;
  description: string | null;
  specifications: Record<string, string> | null;
  variants: {
    id: string;
    sku: string;
    displayName: string;
    options: Record<string, string>;
    priceMinor: number;
    currency: string;
    inStock: boolean;
    /** En çok 20 ("en az bu kadar gösterilebilir"); depodaki kesin miktar değildir (D-048). */
    availableQuantity: number;
  }[];
  images: { url: string; altText: string; isPrimary: boolean; sortOrder: number }[];
};
export type CatalogResult<T> = { state: "ok"; data: T } | { state: "not-found" } | { state: "invalid"; errors: Record<string, string[]> } | { state: "unavailable" };

const base = (process.env.API_BASE_URL ?? "http://localhost:5080").replace(/\/+$/, "");

async function catalogGet<T>(path: string): Promise<CatalogResult<T>> {
  try {
    const response = await fetch(`${base}/api/v1${path}`, { cache: "no-store", signal: AbortSignal.timeout(7000) });
    if (response.status === 404) return { state: "not-found" };
    if (response.status === 400) {
      const problem = await response.json() as { errors?: Record<string, string[]> };
      return { state: "invalid", errors: problem.errors ?? {} };
    }
    if (!response.ok) return { state: "unavailable" };
    return { state: "ok", data: await response.json() as T };
  } catch {
    return { state: "unavailable" };
  }
}

export const getCategories = () => catalogGet<Category[]>("/categories");
export const getProducts = (params: URLSearchParams) => catalogGet<ProductPage>(`/products?${params.toString()}`);
export const getProduct = (slug: string) => catalogGet<ProductDetail>(`/products/${encodeURIComponent(slug)}`);

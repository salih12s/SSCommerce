import AxeBuilder from "@axe-core/playwright";
import { expect, type APIRequestContext, type BrowserContext, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";

export const api = "/api/backend/api/v1";
export const FIXTURE_URL = "http://127.0.0.1:5082";
export const password = "Browser-test-42-Only";
export const newEmail = () => `cart-${randomUUID()}@example.test`;

// E2E fixture kuralı (FixtureControl.cs): tüm SKU'lar ordinal sıralanır; 0 tabanlı sıra no mod 4 → 0, 3, 12, 50 adet.
// JS varsayılan sort() UTF-16 kod birimine göre sıralar; SKU'lar yalnız [A-Z0-9-] olduğundan C# StringComparer.Ordinal ile aynıdır.
export const FIXTURE_STOCK = [0, 3, 12, 50] as const;
export const PUBLIC_CAP = 20;

export type Variant = {
  id: string; sku: string; displayName: string; priceMinor: number; inStock: boolean; availableQuantity: number;
  productSlug: string; productName: string; index: number; stock: number;
};

export async function loadVariants(request: APIRequestContext): Promise<Variant[]> {
  const list = await request.get(`${api}/products?pageSize=50`);
  expect(list.ok()).toBeTruthy();
  const items = (await list.json()).items as { slug: string; name: string }[];
  const all: Omit<Variant, "index" | "stock">[] = [];
  for (const item of items) {
    const response = await request.get(`${api}/products/${item.slug}`);
    expect(response.ok(), item.slug).toBeTruthy();
    const detail = await response.json() as { variants: { id: string; sku: string; displayName: string; priceMinor: number; inStock: boolean; availableQuantity: number }[] };
    for (const v of detail.variants) all.push({ ...v, productSlug: item.slug, productName: item.name });
  }
  const skus = all.map((v) => v.sku).sort();
  expect(new Set(skus).size).toBe(skus.length);
  return all.map((v) => {
    const index = skus.indexOf(v.sku);
    return { ...v, index, stock: FIXTURE_STOCK[index % 4] };
  });
}
/** Varyant satırı: radio değeri varyant kimliğidir (görünen ad başka bir adın öneki olabilir). */
export const variantRow = (page: Page, v: Pick<Variant, "id">) => page.locator(`section[aria-labelledby='varyantlar'] li:has(input[value="${v.id}"])`);
export const withStock = (variants: Variant[], stock: number, skip = 0) => variants.filter((v) => v.stock === stock)[skip];

export async function csrf(request: APIRequestContext) {
  const response = await request.get(`${api}/auth/csrf`);
  expect(response.status()).toBe(200);
  return (await response.json()).token as string;
}
export async function send(request: APIRequestContext, method: "POST" | "PUT" | "PATCH" | "DELETE", path: string, origin: string, data?: unknown) {
  const token = await csrf(request);
  return await request.fetch(`${api}${path}`, { method, data, headers: { "X-CSRF-TOKEN": token, Origin: origin } });
}
/** Yeni kullanıcı kaydeder; çerezler bağlamın request'inde (ve o bağlamdaki sayfalarda) kalır. */
export async function registerApi(request: APIRequestContext, origin: string) {
  const email = newEmail();
  const response = await send(request, "POST", "/auth/register", origin, { email, password });
  expect(response.status()).toBe(201);
  return email;
}
export async function fixture(request: APIRequestContext, path: "stock" | "price" | "active", body: Record<string, unknown>) {
  const response = await request.post(`${FIXTURE_URL}/fixture/${path}`, { data: body });
  expect(response.status(), `${path} ${JSON.stringify(body)}`).toBe(204);
}
export const goodAddress = { label: "Ev", recipientName: "Ayşe Yılmaz", phone: "5321234567", province: "İstanbul", district: "Kadıköy", streetAddress: "Moda Cd. No 1 Daire 2", postalCode: "34710" };
export async function addressApi(request: APIRequestContext, origin: string, overrides: Partial<typeof goodAddress> = {}) {
  const response = await send(request, "POST", "/addresses", origin, { ...goodAddress, ...overrides });
  expect(response.status()).toBe(201);
  return await response.json() as { id: string };
}
export async function cartApi(request: APIRequestContext, origin: string, variantId: string, quantity: number) {
  const response = await send(request, "POST", "/cart/items", origin, { variantId, quantity });
  expect(response.status()).toBe(200);
  return await response.json() as { items: { id: string; variantId: string; quantity: number }[]; subtotalMinor: number; itemCount: number };
}
export async function getCart(request: APIRequestContext) {
  const response = await request.get(`${api}/cart`);
  expect(response.status()).toBe(200);
  return await response.json() as { items: { id: string; variantId: string; quantity: number; status: string; unitPriceMinor: number | null; lineSubtotalMinor: number | null }[]; subtotalMinor: number; itemCount: number; hasIssues: boolean };
}

export async function axeSerious(page: Page) {
  const results = await new AxeBuilder({ page }).analyze();
  return results.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`);
}
export const noHorizontalOverflow = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);
export async function newUserContext(browser: import("@playwright/test").Browser, baseURL: string): Promise<{ context: BrowserContext; page: Page; email: string }> {
  const context = await browser.newContext({ baseURL });
  const page = await context.newPage();
  const email = await registerApi(context.request, baseURL);
  return { context, page, email };
}
export const viewports = [
  { width: 390, height: 844 },
  { width: 768, height: 1024 },
  { width: 1440, height: 900 },
];

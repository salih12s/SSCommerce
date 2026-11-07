import { expect, type APIRequestContext, type Browser, type BrowserContext, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import type { AdminOrder, AdminVariant, Category, Dashboard, Paged, ProductDetail } from "../../src/lib/admin/types";
import { FIXTURE_URL, api, goodAddress, orderSend, password, placeOrderApi, registerApi, send, type ApiOrder } from "./support";

// Faz 8 yönetim E2E yardımcıları. Roller yalnız test-only yoldan verilir: kayıt API'si + E2eHost'un loopback `/fixture/role` ucu. Sabit/kalıcı admin ve sabit parola yoktur.
// Yazan spec'ler kendi verisini (benzersiz slug/SKU) üretir ve `retire` ile pasife alarak bırakır; yönetim API'sinde silme yoktur.

export type StaffRole = "Admin" | "Warehouse";
export type Actor = { context: BrowserContext; page: Page; request: APIRequestContext; email: string; id: string; origin: string };

export const uid = () => randomUUID().replace(/-/g, "").slice(0, 10);
export const WAREHOUSE_CODE = "E2E-DEPO";

export async function meApi(request: APIRequestContext) {
  const response = await request.get(`${api}/auth/me`);
  expect(response.status()).toBe(200);
  return await response.json() as { id: string; email: string; roles: string[] };
}
export async function loginApi(request: APIRequestContext, origin: string, email: string) {
  const response = await send(request, "POST", "/auth/login", origin, { email, password });
  expect(response.status(), await response.text()).toBe(200);
}
/**
 * Test-only rol atama: yalnız E2eHost'un loopback `/fixture/role` ucu. 204 dışında her yanıt açık hata fırlatır; yedek yol, yeniden deneme ya da atlama yoktur.
 * Uç her çağrıda güvenlik damgasını yeniler: o ana kadarki çerez düşer, bu yüzden girişi rol atamasından SONRA yap.
 */
export async function grantRoleFixture(request: APIRequestContext, email: string, role: StaffRole) {
  const response = await request.post(`${FIXTURE_URL}/fixture/role`, { data: { email, role } });
  if (response.status() !== 204) throw new Error(`rol fixture'ı başarısız: POST /fixture/role (${role}) ${response.status()} döndü, 204 bekleniyordu`);
}

/** Yeni hesap: role verilirse fixture ile Admin/Warehouse yapılır (güvenlik damgası yenilenir) ve yeniden giriş yapılır; null ise Customer kalır. */
export async function actor(browser: Browser, origin: string, role: StaffRole | null): Promise<Actor> {
  const context = await browser.newContext({ baseURL: origin, viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();
  const email = await registerApi(context.request, origin);
  if (role) {
    await grantRoleFixture(context.request, email, role);
    await loginApi(context.request, origin, email); // eski çerez damga değişince düştü
  }
  const me = await meApi(context.request);
  expect(me.roles.includes(role ?? "Customer")).toBe(true);
  return { context, page, request: context.request, email, id: me.id, origin };
}

// ---- yönetim API ----
export async function aGet<T>(request: APIRequestContext, path: string): Promise<T> {
  const response = await request.get(`${api}/admin${path}`);
  expect(response.status(), `GET ${path}: ${await response.text()}`).toBe(200);
  return await response.json() as T;
}
/** Yazma: beklenen durumu doğrular, gövdeyi döndürür. */
export async function aWrite<T>(a: Pick<Actor, "request" | "origin">, method: "POST" | "PUT" | "PATCH" | "DELETE", path: string, data: unknown, expected: number): Promise<T> {
  const response = await send(a.request, method, `/admin${path}`, a.origin, data);
  expect(response.status(), `${method} ${path}: ${await response.text()}`).toBe(expected);
  return (response.status() === 204 ? undefined : await response.json()) as T;
}
/** Beklenen hata: durum + `code` doğrulanır, problem gövdesi döner. */
export async function aProblem(a: Pick<Actor, "request" | "origin">, method: "POST" | "PUT" | "PATCH" | "DELETE", path: string, data: unknown, status: number, code: string) {
  const response = await send(a.request, method, `/admin${path}`, a.origin, data);
  const body = await response.json().catch(() => ({})) as Record<string, unknown>;
  expect(response.status(), `${method} ${path}: ${JSON.stringify(body)}`).toBe(status);
  expect(body.code, `${method} ${path}`).toBe(code);
  // RFC 9457: her hata gövdesi problem+json'dur ve type/title/status taşır (sözleşme: type, title, status, code)
  expect(response.headers()["content-type"], `${method} ${path}`).toContain("application/problem+json");
  expect(typeof body.type === "string" && body.type !== "", `${method} ${path} type`).toBe(true);
  expect(typeof body.title === "string" && body.title !== "", `${method} ${path} title`).toBe(true);
  expect(body.status, `${method} ${path} status`).toBe(status);
  return body;
}

export async function mkCategory(a: Actor, over: Record<string, unknown> = {}): Promise<Category> {
  const slug = `e2e-${uid()}`;
  return await aWrite<Category>(a, "POST", "/categories", { name: `Deneme ${slug}`, slug, ...over }, 201);
}
export async function mkProduct(a: Actor, categoryId: string, over: Record<string, unknown> = {}): Promise<ProductDetail> {
  const slug = `e2e-urun-${uid()}`;
  return await aWrite<ProductDetail>(a, "POST", "/products", { categoryId, name: `Deneme ürünü ${slug}`, slug, ...over }, 201);
}
export const mkSku = () => `E2E-${uid().toUpperCase()}`;
export async function mkVariant(a: Actor, productId: string, over: Record<string, unknown> = {}): Promise<AdminVariant> {
  return await aWrite<AdminVariant>(a, "POST", `/products/${productId}/variants`, { sku: mkSku(), displayName: "Standart", priceMinor: 129_990, ...over }, 201);
}
export async function warehouseId(request: APIRequestContext, code = WAREHOUSE_CODE) {
  const list = await aGet<{ id: string; code: string }[]>(request, "/warehouses");
  const found = list.find((w) => w.code === code);
  expect(found, `depo ${code}`).toBeTruthy();
  return found!.id;
}
export async function receiveApi(a: Actor, whId: string, variantId: string, quantity: number, key = `stk-${randomUUID()}`) {
  const response = await orderSend(a.request, a.origin, "/admin/inventory/receive", { warehouseId: whId, variantId, quantity, reason: "E2E stok girişi" }, key);
  expect(response.status(), await response.text()).toBe(201);
  return await response.json() as { onHandAfter: number; replayed: boolean };
}

export type Sellable = { category: Category; product: ProductDetail; variant: AdminVariant; slug: string; sku: string };
/** Vitrinde satın alınabilir kendi kaydı: aktif kategori + aktif ürün + aktif varyant + E2E-DEPO'da stok. Test sonunda `retire` ile pasife alınır. */
export async function mkSellable(a: Actor, opts: { priceMinor?: number; stock?: number; categoryOver?: Record<string, unknown> } = {}): Promise<Sellable> {
  const category = await mkCategory(a, opts.categoryOver);
  const product = await mkProduct(a, category.id, { isActive: true });
  const variant = await mkVariant(a, product.id, { priceMinor: opts.priceMinor ?? 129_990 });
  if ((opts.stock ?? 20) > 0) await receiveApi(a, await warehouseId(a.request), variant.id, opts.stock ?? 20);
  return { category, product, variant, slug: product.slug, sku: variant.sku };
}
/** Silme yok: ürünü ve kategoriyi pasife alır (vitrinden kalkar, paylaşılan katalog eski hâline döner). Hata vermez; temizlik testi bozmasın. */
export async function retire(a: Actor, ...items: { product: { id: string }; category: { id: string } }[]) {
  for (const item of items) {
    try {
      const product = await aGet<ProductDetail>(a.request, `/products/${item.product.id}`);
      if (product.isActive) await send(a.request, "PATCH", `/admin/products/${product.id}`, a.origin, { version: product.version, isActive: false });
      const categories = await aGet<Category[]>(a.request, "/categories");
      const category = categories.find((c) => c.id === item.category.id);
      if (category?.isActive) await send(a.request, "PATCH", `/admin/categories/${category.id}`, a.origin, { version: category.version, isActive: false });
    } catch { /* temizlik en iyi çabadır */ }
  }
}

// ---- müşteri tarafı ----
export async function customerWithOrder(browser: Browser, origin: string, s: Sellable, quantity = 1): Promise<{ customer: Actor; order: ApiOrder }> {
  const customer = await actor(browser, origin, null);
  const cart = await send(customer.request, "POST", "/cart/items", origin, { variantId: s.variant.id, quantity });
  expect(cart.status(), await cart.text()).toBe(200);
  const address = await send(customer.request, "POST", "/addresses", origin, goodAddress);
  expect(address.status()).toBe(201);
  const order = await placeOrderApi(customer.request, origin, (await address.json()).id as string);
  return { customer, order };
}
export async function publicAvailable(request: APIRequestContext, slug: string, variantId: string) {
  const response = await request.get(`${api}/products/${slug}`);
  expect(response.status()).toBe(200);
  const detail = await response.json() as { variants: { id: string; availableQuantity: number }[] };
  return detail.variants.find((v) => v.id === variantId)!.availableQuantity;
}
export const adminOrder = (request: APIRequestContext, id: string) => aGet<AdminOrder>(request, `/orders/${id}`);
export const dashboardApi = (request: APIRequestContext) => aGet<Dashboard>(request, "/dashboard");
export type { Paged };

// ---- arayüz ----
/** Sayfa yüklemesi bitti: başlık görünür, "yükleniyor" durumları kalktı. */
export async function settled(page: Page) {
  await expect(page.locator("main h1").first()).toBeVisible();
  await page.waitForLoadState("networkidle");
  await expect(page.locator("main").getByText(/yükleniyor…|hazırlanıyor…|aranıyor…|denetleniyor…/)).toHaveCount(0);
}

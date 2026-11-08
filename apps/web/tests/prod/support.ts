import AxeBuilder from "@axe-core/playwright";
import { test as base, expect, type APIRequestContext, type APIResponse, type Browser, type BrowserContext, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { loadCreds } from "./creds";

// Üretim-eşdeğeri yığın E2E yardımcıları. Yığın dışarıda çalışır (https://localhost:8443, YEREL CA sertifikası: ignoreHTTPSErrors bilinçli).
// Testler tekrar koşturulabilir: müşteriler benzersiz e-postayla kayıt olur; stok yalnız artırılır.

export const API = "/api/backend/api/v1";
export const BASE = loadCreds().PROD_BASE_URL;
export const customerPassword = "Prod-drill-42-Only!";
export const viewports = [
  { width: 390, height: 844 },
  { width: 768, height: 1024 },
  { width: 1440, height: 900 },
] as const;
export const uid = () => randomUUID().replace(/-/g, "").slice(0, 10);
export const newEmail = () => `prod-${uid()}@example.test`;

// Yığının hız sınırları (IP başına, 60 sn pencere): login/register 10, csrf/me/logout 60, yazmalar 120. Tüm tarayıcılar aynı IP'den geldiği için paket bunlara takılabilir.
// Bu YIĞIN davranışıdır (hata değil); testler kendilerini yavaşlatır: kimlik bilgisi çağrıları arasında en az CREDENTIAL_GAP_MS bekler, 429'da Retry-After kadar bekleyip yeniden dener.
const CREDENTIAL_GAP_MS = 6300;
let lastCredentialAt = 0;
export async function credentialsPause() {
  const wait = lastCredentialAt + CREDENTIAL_GAP_MS - Date.now();
  if (wait > 0) await new Promise((resolve) => setTimeout(resolve, wait));
  lastCredentialAt = Date.now();
}
export async function retry429(call: () => Promise<APIResponse>): Promise<APIResponse> {
  let response = await call();
  for (let attempt = 0; attempt < 6 && response.status() === 429; attempt++) {
    const header = Number(response.headers()["retry-after"]);
    await new Promise((resolve) => setTimeout(resolve, (Number.isFinite(header) && header > 0 ? Math.min(header, 65) : 20) * 1000));
    response = await call();
  }
  return response;
}

export type Actor = { context: BrowserContext; email: string; id: string };

export function newCtx(browser: Browser, extra: Parameters<Browser["newContext"]>[0] = {}) {
  return browser.newContext({ baseURL: BASE, ignoreHTTPSErrors: true, viewport: { width: 1440, height: 900 }, ...extra });
}

// ---- API (tarayıcı bağlamının çerezleriyle) ----
export async function csrfToken(request: APIRequestContext) {
  const response = await retry429(() => request.get(`${API}/auth/csrf`));
  expect(response.status(), "GET /auth/csrf").toBe(200);
  return (await response.json()).token as string;
}
export async function send(request: APIRequestContext, method: "POST" | "PUT" | "PATCH" | "DELETE", path: string, data?: unknown, key?: string) {
  const token = await csrfToken(request);
  return await retry429(async () => request.fetch(`${API}${path}`, { method, data, headers: { "X-CSRF-TOKEN": token, Origin: BASE, ...(key ? { "Idempotency-Key": key } : {}) } }));
}
export async function me(request: APIRequestContext) {
  const response = await retry429(() => request.get(`${API}/auth/me`));
  expect(response.status(), "GET /auth/me").toBe(200);
  return await response.json() as { id: string; email: string; roles: string[] };
}
export async function login(request: APIRequestContext, email: string, password: string) {
  await credentialsPause();
  const response = await send(request, "POST", "/auth/login", { email, password });
  expect(response.status(), "POST /auth/login").toBe(200); // gövde yazdırılmaz
}
export async function register(request: APIRequestContext, email = newEmail()) {
  await credentialsPause();
  const response = await send(request, "POST", "/auth/register", { email, password: customerPassword });
  expect(response.status(), `POST /auth/register: ${await response.text()}`).toBe(201);
  return email;
}
/** Yeni, benzersiz müşteri (API ile kayıtlı + oturum açık). */
export async function customer(browser: Browser, extra: Parameters<Browser["newContext"]>[0] = {}): Promise<Actor> {
  const context = await newCtx(browser, extra);
  const email = await register(context.request);
  return { context, email, id: (await me(context.request)).id };
}
export async function staffLogin(browser: Browser, email: string, password: string): Promise<Actor> {
  const context = await newCtx(browser);
  await login(context.request, email, password);
  const info = await me(context.request);
  return { context, email, id: info.id };
}

// ---- yönetim API ----
export async function aGet<T>(request: APIRequestContext, path: string): Promise<T> {
  const response = await retry429(() => request.get(`${API}/admin${path}`));
  expect(response.status(), `GET /admin${path}`).toBe(200);
  return await response.json() as T;
}
export const items = <T>(data: T[] | { items: T[] }): T[] => (Array.isArray(data) ? data : data.items);

export type Sellable = { warehouseId: string; variantId: string; slug: string; productName: string; variantName: string; sku: string };

export async function onHand(request: APIRequestContext, variantId: string) {
  const rows = items(await aGet<{ onHandQuantity: number }[] | { items: { onHandQuantity: number }[] }>(request, `/inventory/balances?variantId=${variantId}&pageSize=100`));
  return rows.reduce((sum, row) => sum + row.onHandQuantity, 0);
}
export async function receive(request: APIRequestContext, s: Sellable, quantity: number) {
  const response = await send(request, "POST", "/admin/inventory/receive", { warehouseId: s.warehouseId, variantId: s.variantId, quantity, reason: "Prod drill stok girişi" }, `drill-${randomUUID()}`);
  expect(response.status(), `receive: ${await response.text()}`).toBe(201);
}

export type Address = { label: string; recipientName: string; phone: string; province: string; district: string; streetAddress: string; postalCode: string };
/** Benzersiz (aranabilir) teslimat verisi: D-071 testi bu değerlerin Depo'ya sızmadığını arar. */
export function uniqueAddress(): Address {
  const tag = uid();
  return { label: "Ev", recipientName: `Teslim Kisisi Q${tag}`, phone: `5${Math.floor(Math.random() * 1e9).toString().padStart(9, "0")}`, province: "İstanbul", district: "Kadıköy", streetAddress: `Gizli Sokak ${tag} No 7`, postalCode: "34710" };
}
export async function addAddress(request: APIRequestContext, address: Address = uniqueAddress()) {
  const response = await send(request, "POST", "/addresses", address);
  expect(response.status(), "POST /addresses").toBe(201);
  return { id: (await response.json()).id as string, address };
}
export async function addToCart(request: APIRequestContext, variantId: string, quantity = 1) {
  const response = await send(request, "POST", "/cart/items", { variantId, quantity });
  expect(response.status(), `POST /cart/items: ${await response.text()}`).toBe(200);
}
export type PlacedOrder = { id: string; orderNumber: string; status: string; replayed: boolean };
export async function placeOrder(request: APIRequestContext, addressId: string, key: string = randomUUID()) {
  const preview = await send(request, "POST", "/checkout/preview", { addressId });
  expect(preview.status(), "POST /checkout/preview").toBe(200);
  const { quote } = await preview.json() as { quote: string | null };
  expect(quote).toMatch(/^[0-9a-f]{64}$/);
  const response = await send(request, "POST", "/orders", { addressId, quote }, key);
  expect(response.status(), `POST /orders: ${await response.text()}`).toBe(201);
  return { order: await response.json() as PlacedOrder, key, quote: quote! };
}
/** Müşteri + sepet (1 adet) + adres + sipariş; hepsi API ile (hızlı kurulum). */
export async function customerWithOrder(browser: Browser, s: Sellable) {
  const actor = await customer(browser);
  await addToCart(actor.context.request, s.variantId);
  const { id, address } = await addAddress(actor.context.request);
  const placed = await placeOrder(actor.context.request, id);
  return { actor, address, addressId: id, ...placed };
}
export async function customerWithCartAndAddress(browser: Browser, s: Sellable, extra: Parameters<Browser["newContext"]>[0] = {}) {
  const actor = await customer(browser, extra);
  await addToCart(actor.context.request, s.variantId);
  await addAddress(actor.context.request);
  return actor;
}

// ---- worker kapsamlı fixture'lar: Admin oturumu, Depo rolü, satılabilir ürün ----
type WorkerFixtures = { admin: Actor; warehouse: Actor; sellable: Sellable };
export const test = base.extend<object, WorkerFixtures>({
  admin: [async ({ browser }, use) => {
    const creds = loadCreds();
    const actor = await staffLogin(browser, creds.PROD_ADMIN_EMAIL, creds.PROD_ADMIN_PASSWORD);
    expect((await me(actor.context.request)).roles, "Admin hesabı Admin rolünde olmalı").toContain("Admin");
    await use(actor);
    await actor.context.close();
  }, { scope: "worker" }],

  // Depo hesabı yalnız kayıtlı müşteridir: Admin API'siyle (hesap kimliğiyle) Depo rolü verilir; rol değişince oturum düşer, yeniden giriş yapılır.
  warehouse: [async ({ browser, admin }, use) => {
    const creds = loadCreds();
    let actor = await staffLogin(browser, creds.PROD_WAREHOUSE_EMAIL, creds.PROD_WAREHOUSE_PASSWORD);
    if (!(await me(actor.context.request)).roles.includes("Warehouse")) {
      const grant = await send(admin.context.request, "PUT", `/admin/users/${actor.id}/roles/Warehouse`);
      expect(grant.status(), `Depo rolü verme: ${await grant.text()}`).toBe(200);
      await actor.context.close();
      actor = await staffLogin(browser, creds.PROD_WAREHOUSE_EMAIL, creds.PROD_WAREHOUSE_PASSWORD);
    }
    const roles = (await me(actor.context.request)).roles;
    expect(roles).toContain("Warehouse");
    expect(roles).not.toContain("Admin");
    await use(actor);
    await actor.context.close();
  }, { scope: "worker" }],

  // Katalogun ilk ürünü + ilk varyantı için depo (yoksa DRILL-ANA) ve stok girilir. Her koşuda +15; iptal/sipariş testleri bunun üstünde çalışır.
  sellable: [async ({ admin }, use) => {
    const request = admin.context.request;
    const warehouses = await aGet<{ id: string; code: string; isActive: boolean }[]>(request, "/warehouses");
    let warehouseId = warehouses.find((w) => w.isActive)?.id;
    if (!warehouseId) {
      const created = await send(request, "POST", "/admin/warehouses", { code: "DRILL-ANA", name: "Drill ana depo" });
      expect(created.status(), `depo oluşturma: ${await created.text()}`).toBe(201);
      warehouseId = (await created.json()).id as string;
    }
    const list = await request.get(`${API}/products?pageSize=50`);
    expect(list.status()).toBe(200);
    const products = (await list.json()).items as { slug: string; name: string }[];
    expect(products.length, "katalogda ürün olmalı").toBeGreaterThan(0);
    const detailResponse = await request.get(`${API}/products/${products[0].slug}`);
    const detail = await detailResponse.json() as { name: string; variants: { id: string; sku: string; displayName: string }[] };
    const variant = detail.variants[0];
    const s: Sellable = { warehouseId, variantId: variant.id, slug: products[0].slug, productName: detail.name, variantName: variant.displayName, sku: variant.sku };
    await receive(request, s, 15);
    await use(s);
  }, { scope: "worker" }],
});
export { expect };

// ---- sayfa gözlemcisi: CSP ihlalleri + konsol/sayfa hataları ----
type CspReport = { directive: string; blocked: string; sample: string; source: string };
export async function watch(page: Page) {
  const violations: string[] = [];
  const problems: string[] = [];
  await page.exposeFunction("__reportCsp", (report: CspReport) => { violations.push(`${report.directive} blocked=${report.blocked} sample=${report.sample.slice(0, 80)} at ${report.source}`); });
  await page.addInitScript(() => {
    document.addEventListener("securitypolicyviolation", (event) => {
      (window as unknown as { __reportCsp: (r: CspReport) => void }).__reportCsp({ directive: event.violatedDirective, blocked: event.blockedURI, sample: event.sample, source: `${event.sourceFile}:${event.lineNumber}` });
    });
  });
  // Süzgeç gerekçesi: beklenen 401/403/404 yanıtları (anonim /auth/me ve /cart yoklaması, 403 yönetim sayfası, 404 sayfası) Chromium'da "Failed to load resource" konsol hatası üretir;
  // bu CSP ihlali ya da uygulama hatası değildir. Başka her konsol hatası (CSP iletisi, hidrasyon uyuşmazlığı, betik hatası) başarısızlıktır.
  page.on("console", (message) => {
    if (message.type() === "error" && !/^Failed to load resource: the server responded with a status of (401|403|404)/.test(message.text())) problems.push(`console: ${message.text()}`);
  });
  page.on("pageerror", (error) => problems.push(`pageerror: ${error.message}`));
  return { violations, problems };
}
export type Watcher = Awaited<ReturnType<typeof watch>>;

export async function ready(page: Page) {
  await page.waitForLoadState("networkidle");
  await expect(page.locator("main").getByText(/yükleniyor…|hazırlanıyor…|aranıyor…|denetleniyor…/)).toHaveCount(0);
}

export async function axeSerious(page: Page) {
  const results = await new AxeBuilder({ page }).analyze();
  return results.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`);
}

export const variantRow = (page: Page, variantId: string) => page.locator(`section[aria-labelledby='varyantlar'] li:has(input[value="${variantId}"])`);

import AxeBuilder from "@axe-core/playwright";
import { expect, test, type APIRequestContext, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { formatPriceMinor } from "../../src/lib/format";

const api = "/api/backend/api/v1";
const password = "Browser-test-42-Only";
const email = () => `browser-${randomUUID()}@example.test`;
type Item = { slug: string; name: string; startingPriceMinor: number; category: { slug: string; name: string } };
async function list(request: APIRequestContext, query = "") {
  const r = await request.get(`${api}/products?${query}`);
  expect(r.ok()).toBeTruthy();
  return await r.json() as { items: Item[]; totalCount: number; totalPages: number };
}
async function csrf(request: APIRequestContext) {
  const r = await request.get(`${api}/auth/csrf`);
  expect(r.status()).toBe(200);
  return (await r.json()).token as string;
}
async function signup(page: Page, address: string) {
  await page.goto("/kayit");
  await page.getByLabel("E-posta", { exact: true }).fill(address);
  await page.getByLabel("Parola", { exact: true }).fill(password);
  const registered = page.waitForResponse((response) => response.url().endsWith("/auth/register") && response.request().method() === "POST", { timeout: 30_000 });
  await page.getByRole("button", { name: "Hesap oluştur", exact: true }).click();
  expect((await registered).status()).toBe(201);
  await expect(page).toHaveURL(/\/hesabim$/);
  await expect(page.locator("main")).toContainText(address);
}
async function axe(page: Page) {
  const results = await new AxeBuilder({ page }).analyze();
  expect(results.violations.filter((v) => v.impact === "serious" || v.impact === "critical")).toEqual([]);
}

test("home cards contain API prices and navigate to full product variants", async ({ page, request }) => {
  const products = await list(request, "pageSize=50");
  await page.goto("/");
  await expect(page.locator("main article:has(h3):visible")).toHaveCount(products.items.length);
  for (const product of products.items) {
    const card = page.locator("main article").filter({ has: page.getByRole("heading", { name: product.name, exact: true }) });
    await expect(card).toContainText(formatPriceMinor(product.startingPriceMinor));
    await expect(card.getByRole("link", { name: product.name, exact: true })).toHaveAttribute("href", `/urun/${product.slug}`);
  }
  const p = products.items[0];
  await page.getByRole("link", { name: p.name, exact: true }).click();
  await expect(page.getByRole("heading", { level: 1, name: p.name })).toBeVisible();
  const detail = await (await request.get(`${api}/products/${p.slug}`)).json();
  for (const v of detail.variants) {
    await expect(page.locator("main")).toContainText(v.displayName);
    await expect(page.locator("main")).toContainText(formatPriceMinor(v.priceMinor));
  }
  await expect(page.locator("main")).toContainText("Demo ürün");
  await expect(page.getByRole("button", { name: /satın al/i })).toHaveCount(0);
});

test("search filters, sorting, prices and reload follow the API", async ({ page, request }) => {
  await page.goto("/arama");
  const form = page.locator("main form");
  await form.getByRole("combobox", { name: "Kategori", exact: true }).selectOption("ses-sistemleri");
  await form.getByLabel("En düşük ₺").fill("1500");
  await form.getByLabel("En yüksek ₺").fill("4000");
  await form.getByLabel("Sıralama").selectOption("price_asc");
  await form.getByRole("button", { name: "Ürünleri göster" }).click();
  await expect(page).toHaveURL(/category=ses-sistemleri/);
  const expected = await list(request, "category=ses-sistemleri&minPriceMinor=150000&maxPriceMinor=400000&sort=price_asc&pageSize=12");
  await expect(page.locator("main article:visible h3")).toHaveText(expected.items.map((p) => p.name));
  await page.reload();
  await expect(page.locator("main article:visible h3")).toHaveText(expected.items.map((p) => p.name));
  await expect(form.getByLabel("Sıralama")).toHaveValue("price_asc");
  await expect(form.getByLabel("En düşük ₺")).toHaveValue("1500");
});

test("pagination keeps filters in URL and shows the next API page", async ({ page, request }) => {
  await page.goto("/arama?sort=price_asc&pageSize=6");
  const first = await list(request, "sort=price_asc&pageSize=6&page=1");
  await expect(page.locator("main article:visible h3")).toHaveText(first.items.map((p) => p.name));
  await page.getByRole("link", { name: "Sonraki", exact: true }).click();
  await expect(page).toHaveURL(/page=2/);
  expect(new URL(page.url()).searchParams.get("pageSize")).toBe("6");
  const second = await list(request, "sort=price_asc&pageSize=6&page=2");
  await expect(page.locator("main article:visible h3")).toHaveText(second.items.map((p) => p.name));
  await page.reload();
  await expect(page.locator("main article:visible h3")).toHaveText(second.items.map((p) => p.name));
});

test("invalid filters and empty searches are honest", async ({ page }) => {
  await page.goto("/arama?sort=bogus");
  await expect(page.locator("main").getByRole("alert")).toContainText(/kontrol|geçersiz/i);
  await expect(page.locator("main article:has(h3):visible")).toHaveCount(0);
  await page.goto(`/arama?q=${randomUUID()}`);
  await expect(page.locator("main")).toContainText("ürün bulunamadı");
  await expect(page.locator("main article:has(h3):visible")).toHaveCount(0);
});

for (const route of ["/kategori/yok", "/kategori/bilgisayarlar", "/urun/olmayan-urun"]) {
  test(`real HTTP 404 before streaming: ${route}`, async ({ request }) => {
    const response = await request.get(route);
    expect(response.status()).toBe(404);
  });
}

for (const width of [390, 768, 1440]) {
  test.describe(`real forms and catalog at ${width}px`, () => {
    test.use({ viewport: { width, height: width === 390 ? 844 : 1024 } });
    test("register, refresh, logout and login through same-origin cookie proxy", async ({ page, context }, info) => {
      const address = email();
      await signup(page, address);
      await expect(page.locator("main")).toContainText("Doğrulanmadı");
      const cookies = await context.cookies();
      const authCookie = cookies.find((c) => c.name === "ssc.auth");
      expect(authCookie?.httpOnly).toBe(true);
      expect(authCookie?.sameSite).toBe("Lax");
      expect(await page.evaluate(() => document.cookie)).not.toMatch(/ssc\.(auth|csrf)/);
      expect(await page.evaluate(() => ({ ...localStorage }))).toEqual({});
      await page.reload();
      await expect(page.locator("main")).toContainText(address);
      await axe(page);
      const file = info.outputPath(`account-${width}.png`);
      await page.screenshot({ path: file, fullPage: true });
      await info.attach("account", { path: file, contentType: "image/png" });
      await page.getByRole("button", { name: "Çıkış yap", exact: true }).click();
      await expect(page).toHaveURL(/\/giris$/);
      expect((await context.request.get(`${api}/auth/me`)).status()).toBe(401);
      await page.getByLabel("E-posta", { exact: true }).fill(address);
      await page.getByLabel("Parola", { exact: true }).fill(password);
      await page.getByRole("button", { name: "Giriş yap", exact: true }).click();
      await expect(page).toHaveURL(/\/hesabim$/);
      await expect(page.locator("main")).toContainText(address);
      expect((await context.request.get(`${api}/admin/me`)).status()).toBe(403);
      await page.goto("/arama?category=ses-sistemleri&sort=price_asc");
      await expect(page.locator("main article").first()).toBeVisible();
      await axe(page);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    });
    test("login and registration are accessible and fit the viewport", async ({ page }, info) => {
      for (const route of ["giris", "kayit"]) {
        await page.goto(`/${route}`);
        await expect(page.getByLabel("E-posta", { exact: true })).toBeVisible();
        await axe(page);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        const file = info.outputPath(`${route}-${width}.png`);
        await page.screenshot({ path: file, fullPage: true });
        await info.attach(route, { path: file, contentType: "image/png" });
      }
    });
  });
}

test("anonymous account and admin are denied; personal responses are not cached", async ({ page, request }) => {
  for (const path of ["auth/me", "admin/me"]) {
    const response = await request.get(`${api}/${path}`);
    expect(response.status()).toBe(401);
    expect(response.headers()["cache-control"]).toContain("no-store");
    expect(response.headers()["location"]).toBeUndefined();
  }
  const response = await page.goto("/hesabim");
  expect(response?.headers()["cache-control"]).toContain("no-store");
  await expect(page.locator("main")).toContainText("giriş yap");
});

test("missing, invalid and cross-origin CSRF requests fail; role injection cannot grant Admin", async ({ request, baseURL }) => {
  expect((await request.post(`${api}/auth/register`, { data: { email: email(), password } })).status()).toBe(400);
  const token = await csrf(request);
  expect((await request.post(`${api}/auth/register`, { data: { email: email(), password }, headers: { "X-CSRF-TOKEN": "invalid" } })).status()).toBe(400);
  expect((await request.post(`${api}/auth/register`, { data: { email: email(), password }, headers: { "X-CSRF-TOKEN": token, Origin: "https://untrusted.example" } })).status()).toBe(403);
  const registered = await request.post(`${api}/auth/register`, { data: { email: email(), password, role: "Admin", isAdmin: true }, headers: { "X-CSRF-TOKEN": token, Origin: baseURL! } });
  expect(registered.status()).toBe(201);
  expect((await registered.json()).roles).toEqual(["Customer"]);
  expect((await request.get(`${api}/admin/me`)).status()).toBe(403);
  // A token issued before registration belongs to the anonymous principal.
  expect((await request.post(`${api}/auth/logout`, { headers: { "X-CSRF-TOKEN": token } })).status()).toBe(400);
  const renewed = await csrf(request);
  expect((await request.post(`${api}/auth/logout`, { headers: { "X-CSRF-TOKEN": renewed } })).status()).toBe(204);
  expect((await request.get(`${api}/auth/me`)).status()).toBe(401);
});

test("two browser customers remain isolated and old cookies fail after logout", async ({ browser, baseURL }) => {
  const a = await browser.newContext({ baseURL });
  const b = await browser.newContext({ baseURL });
  try {
    const pageA = await a.newPage(); const pageB = await b.newPage();
    const emailA = email(); const emailB = email();
    await signup(pageA, emailA); await signup(pageB, emailB);
    await expect(pageA.locator("main")).not.toContainText(emailB);
    await expect(pageB.locator("main")).not.toContainText(emailA);
    const old = await a.cookies();
    await pageA.getByRole("button", { name: "Çıkış yap", exact: true }).click();
    await expect(pageA).toHaveURL(/\/giris$/);
    await a.addCookies(old);
    expect((await a.request.get(`${api}/auth/me`)).status()).toBe(401);
    expect((await b.request.get(`${api}/auth/me`)).status()).toBe(200);
    await pageB.reload();
    await expect(pageB.locator("main")).toContainText(emailB);
  } finally { await a.close(); await b.close(); }
});

test("incorrect credentials and backend validation are shown in the form", async ({ page }) => {
  await page.goto("/giris");
  await page.getByLabel("E-posta", { exact: true }).fill(email());
  await page.getByLabel("Parola", { exact: true }).fill("Wrong-only-42");
  await page.getByRole("button", { name: "Giriş yap", exact: true }).click();
  await expect(page.locator("main").getByRole("alert")).toContainText("E-posta veya parola hatalı");
  await page.goto("/kayit");
  await page.getByLabel("E-posta", { exact: true }).fill(email());
  await page.getByLabel("Parola", { exact: true }).fill("abc");
  await page.getByRole("button", { name: "Hesap oluştur", exact: true }).click();
  await expect(page.locator("main").getByRole("alert")).toBeVisible();
  await expect(page.getByLabel("Parola", { exact: true })).toHaveAttribute("aria-invalid", "true");
});

test("successful auth needs no redundant token request and expired logout has truthful copy", async ({ page, context }) => {
  let tokens = 0;
  page.on("request", (request) => { if (request.url().endsWith("/auth/csrf")) tokens++; });
  await signup(page, email());
  expect(tokens).toBe(1);
  await context.clearCookies();
  await page.getByRole("button", { name: "Çıkış yap", exact: true }).click();
  await expect(page.locator("main").getByRole("alert")).toContainText("Oturumun sona erdi");
  expect(tokens).toBe(2);
});

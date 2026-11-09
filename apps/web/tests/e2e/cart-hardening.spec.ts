import { expect, test, type Page, type Request } from "@playwright/test";
import { formatPriceMinor } from "../../src/lib/format";
import { addressApi, cartApi, getCart, loadVariants, newUserContext, password, registerApi, viewports, withStock, type Variant } from "./support";

// F6 kapısı düzeltmeleri (D-057): kimlik geçişinde eski sepet, geç/kayıp yanıtlar, bfcache, no-store, para hesaplamama.
// Yalnızca kendi kullanıcılarının verisini değiştirir; katalog fiyat/stok fixture'ına dokunmaz.
let stock12: Variant;
test.beforeAll(async ({ request }) => { stock12 = withStock(await loadVariants(request), 12); });
test.use({ viewport: { width: 1440, height: 900 } });

const line = (page: Page) => page.getByTestId("cart-line");
const headerCount = (page: Page) => page.locator("header").getByTestId("cart-count");
const CART = "**/api/backend/api/v1/cart";
async function loginUi(page: Page, email: string, next?: string) {
  await page.goto(next === undefined ? "/giris" : `/giris?next=${encodeURIComponent(next)}`);
  await page.getByLabel("E-posta", { exact: true }).fill(email);
  await page.getByLabel("Parola", { exact: true }).fill(password);
  await page.getByRole("button", { name: "Giriş yap", exact: true }).click();
}

test("aynı sekmede çıkış yapıp başka kullanıcıyla giriş: eski sepet ne sayaçta ne /sepet'te görünür", async ({ page, browser, baseURL }) => {
  const other = await newUserContext(browser, baseURL!);
  try {
    await registerApi(page.request, baseURL!);
    await cartApi(page.request, baseURL!, stock12.id, 2);
    await page.goto("/sepet");
    await expect(line(page)).toHaveCount(1);
    await expect(headerCount(page)).toHaveText("2");
    // Aynı sekme, istemci içi gezinti: depo bellekte yaşar.
    await page.locator("header").getByRole("link", { name: /Hesabım/ }).click();
    await page.getByRole("button", { name: "Çıkış yap", exact: true }).click();
    await expect(page).toHaveURL(/\/giris$/);
    await expect(headerCount(page)).toHaveCount(0);
    await page.getByLabel("E-posta", { exact: true }).fill(other.email);
    await page.getByLabel("Parola", { exact: true }).fill(password);
    await page.getByRole("button", { name: "Giriş yap", exact: true }).click();
    await expect(page).toHaveURL(/\/hesabim$/);
    await expect(page.locator("main")).toContainText(other.email);
    await expect(headerCount(page)).toHaveCount(0);
    await page.locator("header").getByRole("link", { name: /Sepetim/ }).click();
    await expect(page.getByTestId("cart-empty")).toBeVisible();
    await expect(line(page)).toHaveCount(0);
    await expect(headerCount(page)).toHaveCount(0);
  } finally { await other.context.close(); }
});

test("kimlik değişiminden sonra geç dönen eski GET /cart yanıtı yok sayılır", async ({ page, browser, baseURL }) => {
  const other = await newUserContext(browser, baseURL!);
  try {
    await registerApi(page.request, baseURL!);
    await cartApi(page.request, baseURL!, stock12.id, 2);
    let first = true;
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => { release = resolve; });
    await page.route(CART, async (route) => {
      if (route.request().method() !== "GET" || !first) return route.continue();
      first = false;
      const response = await route.fetch(); // A'nın sepeti şimdi alınır, ekrana geç verilir
      await gate;
      // Geç yanıtın Set-Cookie başlığı (kayan oturum yenilemesi) tarayıcıya B'nin çerezini ezmesin: test yalnızca gövdeyi geciktirir.
      const { "set-cookie": _ignored, ...headers } = response.headers();
      void _ignored;
      await route.fulfill({ response, headers });
    });
    await page.goto("/hesabim");
    await expect.poll(() => first).toBe(false);
    await page.getByRole("button", { name: "Çıkış yap", exact: true }).click();
    await expect(page).toHaveURL(/\/giris$/);
    await page.getByLabel("E-posta", { exact: true }).fill(other.email);
    await page.getByLabel("Parola", { exact: true }).fill(password);
    await page.getByRole("button", { name: "Giriş yap", exact: true }).click();
    await expect(page).toHaveURL(/\/hesabim$/);
    release();
    await page.waitForTimeout(800); // geç yanıtın işlenmesi için süre tanınır; sonra hâlâ B'nin boş sepeti görünmeli
    await expect(headerCount(page)).toHaveCount(0);
    await page.locator("header").getByRole("link", { name: /Sepetim/ }).click();
    await expect(page.getByTestId("cart-empty")).toBeVisible();
    await expect(line(page)).toHaveCount(0);
  } finally { await other.context.close(); }
});

test.describe("kayıp yanıt: POST /cart/items", () => {
  test("sunucu uyguladı ama yanıt kayboldu: sepet yeniden okunur, dürüst ileti, 'eklenmedi/değişmedi' denmez", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await page.goto(`/urun/${stock12.productSlug}`);
    await expect(page.getByRole("heading", { level: 2, name: "Seçenekler" })).toBeVisible();
    await page.locator(`input[value="${stock12.id}"]`).check();
    const gets: Request[] = [];
    page.on("request", (r) => { if (r.method() === "GET" && r.url().endsWith("/api/v1/cart")) gets.push(r); });
    await page.route(`${CART}/items`, async (route) => {
      if (route.request().method() !== "POST") return route.continue();
      await route.fetch(); // sunucu isteği uygular
      await route.abort(); // yanıt tarayıcıya ulaşmaz
    });
    const before = gets.length;
    await page.getByRole("button", { name: "Sepete ekle" }).click();
    const alert = page.locator("main").getByRole("alert");
    await expect(alert).toContainText("doğrulanamadı");
    await expect(alert).not.toContainText(/eklenmedi|değişmedi/);
    expect(gets.length).toBeGreaterThan(before); // zorla yeniden okuma yapıldı
    await expect(headerCount(page)).toHaveText("1"); // gerçek durum: ekleme uygulanmıştı
    expect((await getCart(page.request)).itemCount).toBe(1);
  });

  test("500: sepet yeniden okunur, ileti dürüst, sayaç gerçek durumu gösterir", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await page.goto(`/urun/${stock12.productSlug}`);
    await expect(page.getByRole("heading", { level: 2, name: "Seçenekler" })).toBeVisible();
    await page.locator(`input[value="${stock12.id}"]`).check();
    let gets = 0;
    page.on("request", (r) => { if (r.method() === "GET" && r.url().endsWith("/api/v1/cart")) gets++; });
    await page.route(`${CART}/items`, (route) => route.request().method() === "POST"
      ? route.fulfill({ status: 500, contentType: "application/problem+json", body: JSON.stringify({ title: "Hata", code: "internal_error" }) })
      : route.continue());
    const before = gets;
    await page.getByRole("button", { name: "Sepete ekle" }).click();
    await expect(page.locator("main").getByRole("alert")).toContainText("doğrulanamadı");
    await expect.poll(() => gets).toBeGreaterThan(before);
    await expect(headerCount(page)).toHaveCount(0);
    expect((await getCart(page.request)).itemCount).toBe(0);
  });
});

test("yenileme başarısızken eldeki sepet 'son bilinen' olarak işaretlenir: ara toplam ve devam bağlantısı gizlenir", async ({ page, baseURL }) => {
  await registerApi(page.request, baseURL!);
  await cartApi(page.request, baseURL!, stock12.id, 1);
  await page.goto("/sepet");
  await expect(page.getByTestId("subtotal")).toHaveText(formatPriceMinor(stock12.priceMinor));
  await page.route(`${CART}/items/*`, (route) => route.abort());
  await page.route(CART, (route) => route.request().method() === "GET" ? route.abort() : route.continue());
  await line(page).getByRole("button", { name: /adedi: artır/ }).click();
  const banner = page.getByTestId("cart-stale");
  await expect(banner).toBeVisible();
  await expect(banner).toContainText("son bilinen sepet");
  await expect(page.getByTestId("subtotal")).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Teslimat adresine geç" })).toHaveCount(0);
  await expect(page.getByTestId("summary-stale")).toBeVisible();
  await page.unroute(CART);
  await page.unroute(`${CART}/items/*`);
  await banner.getByRole("button", { name: "Tekrar dene" }).click();
  await expect(page.getByTestId("cart-stale")).toHaveCount(0);
  await expect(page.getByTestId("subtotal")).toHaveText(formatPriceMinor(stock12.priceMinor)); // kayıp PATCH sunucuya hiç ulaşmadı
  await expect(page.getByRole("link", { name: "Teslimat adresine geç" })).toBeVisible();
});

test("bfcache geri yüklemesi (pageshow persisted): sepet, adresler ve ödeme özeti yeniden okunur", async ({ page, baseURL }) => {
  await registerApi(page.request, baseURL!);
  await cartApi(page.request, baseURL!, stock12.id, 1);
  await addressApi(page.request, baseURL!);
  const calls: string[] = [];
  page.on("request", (r) => { if (r.method() === "GET" && /\/api\/v1\/(cart|addresses)$/.test(r.url())) calls.push(r.url().split("/").pop()!); });
  const restore = (persisted: boolean) => page.evaluate((p) => window.dispatchEvent(new PageTransitionEvent("pageshow", { persisted: p })), persisted);
  for (const [route, endpoint, ready] of [["/sepet", "cart", () => line(page)], ["/hesabim/adresler", "addresses", () => page.getByTestId("address-item")], ["/checkout", "addresses", () => page.getByTestId("preview")]] as const) {
    await page.goto(route);
    await expect(ready().first()).toBeVisible();
    const before = calls.filter((c) => c === endpoint).length;
    await restore(false);
    await page.waitForTimeout(300);
    expect(calls.filter((c) => c === endpoint).length, `${route} persisted=false`).toBe(before);
    await restore(true);
    await expect.poll(() => calls.filter((c) => c === endpoint).length, { message: route }).toBeGreaterThan(before);
    await expect(ready().first()).toBeVisible();
  }
});

test("arayüz para hesaplamaz: sunucunun toplamı olduğu gibi gösterilir (sepet ve özet)", async ({ page, baseURL }) => {
  await registerApi(page.request, baseURL!);
  await cartApi(page.request, baseURL!, stock12.id, 2);
  await addressApi(page.request, baseURL!);
  const lineTotal = 1234567;
  const subtotal = 7654321;
  await page.route(CART, async (route) => {
    if (route.request().method() !== "GET") return route.continue();
    const response = await route.fetch();
    const json = await response.json();
    json.subtotalMinor = subtotal;
    json.items[0].lineSubtotalMinor = lineTotal;
    await route.fulfill({ response, json });
  });
  await page.route("**/api/backend/api/v1/checkout/preview", async (route) => {
    const response = await route.fetch();
    const json = await response.json();
    json.subtotalMinor = subtotal;
    json.lines[0].lineSubtotalMinor = lineTotal;
    json.grandTotalMinor = 99999; // sözleşme null der; gelse bile "ödenecek tutar" diye gösterilmemeli
    await route.fulfill({ response, json });
  });
  await page.goto("/sepet");
  await expect(page.getByTestId("subtotal")).toHaveText(formatPriceMinor(subtotal));
  await expect(page.getByTestId("line-total")).toContainText(formatPriceMinor(lineTotal));
  await expect(page.locator("main")).not.toContainText(formatPriceMinor(stock12.priceMinor * 2));
  await page.goto("/checkout");
  await expect(page.getByTestId("preview-subtotal")).toHaveText(formatPriceMinor(subtotal));
  await expect(page.getByTestId("preview-line")).toContainText(formatPriceMinor(lineTotal));
  await expect(page.locator("main")).not.toContainText(formatPriceMinor(99999));
  await expect(page.locator("main")).not.toContainText(formatPriceMinor(stock12.priceMinor * 2));
  await expect(page.getByTestId("preview-shipping")).toHaveText("Kargo henüz hesaplanmadı");
});

test("kişiye özel sayfalar parse edilmiş yola göre private, no-store sunulur", async ({ request }) => {
  for (const route of ["/hesabim", "/hesabim/adresler", "/giris", "/kayit", "/sepet", "/sepet?x=1", "/%73epet", "/checkout", "/checkout?a=b"]) {
    const response = await request.get(route);
    const cacheControl = response.headers()["cache-control"] ?? "";
    expect(cacheControl, `${route} (${response.status()})`).toContain("no-store");
    expect(cacheControl, route).toContain("private");
  }
});

test.describe("next: giriş yolu", () => {
  const goodUser = async (request: import("@playwright/test").APIRequestContext, baseURL: string) => registerApi(request, baseURL);
  test("güvenli next ile girişte oraya gidilir", async ({ page, request, baseURL }) => {
    const email = await goodUser(request, baseURL!);
    await loginUi(page, email, "/sepet");
    await expect(page).toHaveURL(/\/sepet$/);
    await expect(page.getByTestId("cart-empty")).toBeVisible();
  });
  for (const next of ["//evil.example", "https://evil.example/", "/\\evil.example", "/%2F%2Fevil.example", "/%09/evil", "/giris?next=/giris", "／／evil.example"]) {
    test(`giriş: ${JSON.stringify(next)} /hesabim'e düşer`, async ({ page, request, baseURL }) => {
      const email = await goodUser(request, baseURL!);
      await loginUi(page, email, next);
      await expect(page).toHaveURL(/\/hesabim$/);
      expect(new URL(page.url()).origin).toBe(new URL(baseURL!).origin);
    });
  }
});

test("API kapalıyken /sepet: dürüst hata, tekrar dene, hiçbir tutar yok", async ({ page, baseURL }) => {
  await registerApi(page.request, baseURL!);
  await cartApi(page.request, baseURL!, stock12.id, 1);
  await page.route("**/api/backend/**", (route) => route.abort());
  await page.goto("/sepet");
  const error = page.getByTestId("cart-error");
  await expect(error).toBeVisible();
  await expect(error).toContainText("Sepet yüklenemedi");
  await expect(error.getByRole("button", { name: "Tekrar dene" })).toBeVisible();
  await expect(page.locator("main")).not.toContainText("₺");
  await expect(page.getByRole("link", { name: "Teslimat adresine geç" })).toHaveCount(0);
  await expect(headerCount(page)).toHaveCount(0);
  await page.unroute("**/api/backend/**");
  await error.getByRole("button", { name: "Tekrar dene" }).click();
  await expect(line(page)).toHaveCount(1);
});

// Tasarım düzeltmeleri: dar ekranda küçük görsel, ödeme sayfasında kart içi başlık.
for (const vp of viewports) {
  test(`sepet satırı ${vp.width}px: görsel küçük, ekranı kaplamaz; checkout başlığı kartın içinde`, async ({ page, baseURL }, info) => {
    await page.setViewportSize(vp);
    await registerApi(page.request, baseURL!);
    await cartApi(page.request, baseURL!, stock12.id, 2);
    await addressApi(page.request, baseURL!);
    await page.goto("/sepet");
    const first = line(page).first();
    await expect(first).toBeVisible();
    const thumb = await first.locator(":scope > div").first().boundingBox();
    const card = await first.boundingBox();
    expect(thumb!.width).toBeLessThanOrEqual(vp.width < 640 ? 96 : 120);
    expect(thumb!.width).toBeGreaterThanOrEqual(80);
    if (vp.width < 640) expect(card!.height).toBeLessThan(340); // iki satır birlikte ilk ekrana yakın sığar
    await page.screenshot({ path: info.outputPath(`fix-sepet-${vp.width}.png`), fullPage: true });
    await page.goto("/checkout");
    await expect(page.getByTestId("preview")).toBeVisible();
    // İki dikdörtgen aynı anda ölçülür (arada düzen kayarsa karşılaştırma bozulmasın).
    const { legendTop, panelTop } = await page.evaluate(() => ({
      legendTop: document.querySelector("legend")!.getBoundingClientRect().top,
      panelTop: document.querySelector("fieldset")!.parentElement!.getBoundingClientRect().top,
    }));
    expect(legendTop).toBeGreaterThanOrEqual(panelTop + 16); // kenar çizgisinin üstünde değil, iç boşluğun içinde
    await expect(page.getByRole("heading", { level: 2, name: "Sepet özeti" })).toBeVisible();
    await expect(page.locator("main")).not.toContainText("Sipariş özeti");
    await page.screenshot({ path: info.outputPath(`fix-checkout-${vp.width}.png`), fullPage: true });
  });
}

test("ağ/5xx dışındaki yazma hatasında 'değişmedi' korunur (sunucu reddetti)", async ({ page, baseURL }) => {
  await registerApi(page.request, baseURL!);
  await cartApi(page.request, baseURL!, stock12.id, 1);
  await page.goto("/sepet");
  await expect(line(page)).toHaveCount(1);
  await page.route(`${CART}/items/*`, (route) => route.request().method() === "PATCH"
    ? route.fulfill({ status: 409, contentType: "application/problem+json", body: JSON.stringify({ title: "Yeterli stok yok", code: "insufficient_stock", availableQuantity: 1 }) })
    : route.continue());
  await line(page).getByRole("button", { name: /adedi: artır/ }).click();
  await expect(page.getByTestId("line-failure")).toContainText("sepetin değişmedi");
  expect((await getCart(page.request)).itemCount).toBe(1);
});

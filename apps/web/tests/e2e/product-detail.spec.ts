import { expect, test, type Browser, type BrowserContext, type Page } from "@playwright/test";
import { formatDateTimeIstanbul, formatPriceMinor } from "../../src/lib/format";
import type { ImageAsset, ProductDetail } from "../../src/features/admin/types";
import { actor, aGet, aWrite, customerWithOrder, mkCategory, mkProduct, mkSellable, mkVariant, receiveApi, retire, warehouseId, type Actor } from "./admin-support";
import { api, axeSerious, getCart, noHorizontalOverflow, orderSend, registerApi } from "./support";

// MD-3 Detay sayfaları: ürün galerisi, benzer ürünler, teslimat/iptal bloğu, favori kalbi, mobil sabit çubuk, sipariş zaman çizelgesi, hesap menüsü kenar boşluğu.
// Bu dosya vitrin kataloğuna KENDİ kategori ve ürünlerini ekler (yönetim API'si), bu yüzden "product-detail" projesinde, en sonda ve tek worker'da koşar;
// sonunda hepsini pasife alır (silme yok). Çoklu görselli ürün tohum veride olmadığından galeri verisi de burada kurulur.
let admin: Actor;
let target: ProductDetail;          // 3 görsel (2. kayıt ana), 2 seçilebilir varyant; kategorisinde toplam 6 ürün
let single: ProductDetail;          // 1 görsel, 1 seçilebilir varyant; kategorisinde yalnız o var (benzer ürün yok)
let quick: ProductDetail;          // 1 görsel, 1 seçilebilir varyant; çok ürünlü kategoride (sayfa uzun: form geride bırakılabilir)
let bare: ProductDetail;            // görselsiz, 1 varyant
const created: { product: { id: string }; category: { id: string } }[] = [];
const PRICE = 129_990;
/** Sayfayı asıl formun ALTINA kaydırır (form tamamen görünür alanın üstünde kalır): çubuğun belirme koşulu. */
async function pastForm(page: Page) {
  // Görseller ve yazı tipi yüklenip yerleşim oturmadan ölçme: aksi hâlde sonradan kayan yerleşim formu yeniden görünür alana sokabilir.
  await page.waitForLoadState("load");
  await page.evaluate(() => document.fonts.ready);
  await page.locator("section[aria-labelledby='varyantlar']").evaluate((el) => window.scrollTo(0, el.getBoundingClientRect().bottom + window.scrollY + 8));
}

async function showcase(categoryId: string, opts: { images: number; primary?: number; variants: number }) {
  const assets = await aGet<ImageAsset[]>(admin.request, "/catalog/image-assets");
  const product = await mkProduct(admin, categoryId, { isActive: true, description: "Galeri deneme ürünü." });
  let current = product;
  if (opts.images > 0) {
    const images = Array.from({ length: opts.images }, (_, i) => ({ storageKey: assets[i % assets.length].storageKey, altText: `Görsel ${"ABCDEFGH"[i]}`, isPrimary: i === (opts.primary ?? 0) }));
    current = await aWrite<ProductDetail>(admin, "PUT", `/products/${product.id}/images`, { version: product.version, images }, 200);
  }
  const wh = await warehouseId(admin.request);
  for (let i = 0; i < opts.variants; i++) {
    const variant = await mkVariant(admin, product.id, opts.variants > 1 ? { displayName: `Renk ${i + 1}`, options: { renk: `secenek-${i + 1}` }, priceMinor: PRICE + i * 1000 } : { priceMinor: PRICE });
    await receiveApi(admin, wh, variant.id, 20);
  }
  return aGet<ProductDetail>(admin.request, `/products/${current.id}`);
}

test.beforeAll(async ({ browser, baseURL }) => {
  admin = await actor(browser, baseURL!, "Admin");
  const catMany = await mkCategory(admin);
  target = await showcase(catMany.id, { images: 3, primary: 1, variants: 2 });
  created.push({ product: target, category: catMany });
  quick = await showcase(catMany.id, { images: 1, variants: 1 });
  created.push({ product: quick, category: catMany });
  for (let i = 0; i < 4; i++) created.push({ product: await showcase(catMany.id, { images: 1, variants: 1 }), category: catMany });
  const catSingle = await mkCategory(admin);
  single = await showcase(catSingle.id, { images: 1, variants: 1 });
  created.push({ product: single, category: catSingle });
  const catBare = await mkCategory(admin);
  bare = await showcase(catBare.id, { images: 0, variants: 1 });
  created.push({ product: bare, category: catBare });
});
test.afterAll(async () => {
  if (admin) { await retire(admin, ...created); await admin.context.close(); }
});

async function customerContext(browser: Browser, baseURL: string, viewport: { width: number; height: number }, extra: { reducedMotion?: "reduce" } = {}): Promise<BrowserContext> {
  const context = await browser.newContext({ baseURL, viewport, ...extra });
  await registerApi(context.request, baseURL);
  return context;
}

test.describe("masaüstü 1440", () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test("galeri: birincil görsel önce; tıkla; ok tuşları, Home/End; seçili küçük resim aria-current; odak şeritte tek durak", async ({ page }) => {
    await page.goto(`/urun/${target.slug}`);
    const main = page.getByTestId("gallery-main");
    const thumbs = page.getByTestId("gallery-thumb");
    await expect(thumbs).toHaveCount(3);
    // Ana görsel (isPrimary) dizi sırasında 2. olsa da ilk gösterilir: B, A, C
    await expect(main).toHaveAttribute("alt", "Görsel B");
    await expect(thumbs.nth(0)).toHaveAttribute("aria-current", "true");
    await expect(thumbs.nth(1)).not.toHaveAttribute("aria-current", "true");
    await expect(thumbs.nth(0)).toHaveAccessibleName("Görsel 1 / 3: Görsel B");
    await expect(page.getByTestId("gallery-live")).toHaveText("Görsel 1 / 3");

    await thumbs.nth(1).click();
    await expect(main).toHaveAttribute("alt", "Görsel A");
    await expect(thumbs.nth(1)).toHaveAttribute("aria-current", "true");
    await expect(thumbs.nth(0)).not.toHaveAttribute("aria-current", "true");
    await expect(page.getByTestId("gallery-live")).toHaveText("Görsel 2 / 3");
    // Roving tabindex: yalnız seçili küçük resim tab durağıdır
    await expect(thumbs.nth(1)).toHaveAttribute("tabindex", "0");
    await expect(thumbs.nth(0)).toHaveAttribute("tabindex", "-1");

    await thumbs.nth(1).focus();
    await page.keyboard.press("ArrowRight");
    await expect(thumbs.nth(2)).toBeFocused();
    await expect(main).toHaveAttribute("alt", "Görsel C");
    await page.keyboard.press("ArrowRight"); // sona gelince başa sarar
    await expect(thumbs.nth(0)).toBeFocused();
    await expect(main).toHaveAttribute("alt", "Görsel B");
    await page.keyboard.press("ArrowLeft"); // baştan sona sarar
    await expect(thumbs.nth(2)).toBeFocused();
    await page.keyboard.press("Home");
    await expect(thumbs.nth(0)).toBeFocused();
    await page.keyboard.press("End");
    await expect(thumbs.nth(2)).toBeFocused();
    await expect(thumbs.nth(2)).toHaveAttribute("aria-current", "true");
  });

  test("tek görselde şerit yok; görselsiz üründe mevcut boş durum", async ({ page }) => {
    await page.goto(`/urun/${single.slug}`);
    await expect(page.getByTestId("gallery-main")).toHaveAttribute("alt", "Görsel A");
    await expect(page.getByTestId("gallery-thumbs")).toHaveCount(0);
    await expect(page.getByTestId("gallery-thumb")).toHaveCount(0);
    await page.goto(`/urun/${bare.slug}`);
    await expect(page.getByTestId("product-gallery")).toContainText("Ürün görseli yok");
    await expect(page.getByTestId("gallery-main")).toHaveCount(0);
  });

  test("benzer ürünler: aynı kategoriden en çok 4, mevcut ürün yok; kategorisinde tek ürün olan sayfada bölüm yok", async ({ page }) => {
    await page.goto(`/urun/${target.slug}`);
    const section = page.getByTestId("similar-products");
    await expect(section.getByRole("heading", { level: 2, name: "Benzer ürünler" })).toBeVisible();
    await expect(section.locator("article")).toHaveCount(4); // kategoride 6 ürün var: 5 iste, mevcut çıkar/kırp
    await expect(section.locator(`a[href="/urun/${target.slug}"]`)).toHaveCount(0);
    for (const href of await section.locator("article h3 a").evaluateAll((els) => els.map((e) => e.getAttribute("href")))) expect(href).toMatch(/^\/urun\/e2e-urun-/);
    await page.goto(`/urun/${single.slug}`);
    await expect(page.getByRole("heading", { level: 1, name: single.name })).toBeVisible();
    await expect(page.getByTestId("similar-products")).toHaveCount(0);
    await expect(page.getByRole("heading", { name: "Benzer ürünler" })).toHaveCount(0);
  });

  test("teslimat ve iptal bloğu: yalnız doğru bilgi", async ({ page }) => {
    await page.goto(`/urun/${target.slug}`);
    const info = page.getByTestId("delivery-info");
    await expect(info.getByRole("heading", { level: 2, name: "Teslimat ve iptal" })).toBeVisible();
    await expect(info).toContainText("Sipariş verirken ödeme alınmaz.");
    await expect(info).toContainText("Kargo ücreti henüz hesaplanmaz.");
    await expect(info).toContainText("Sepet stok ayırmaz; stok sipariş verdiğin anda düşer.");
    await expect(info).toContainText("“Alındı” durumundaki siparişi hesabından kendin iptal edebilirsin; ürünler stoğa geri döner.");
    await expect(info).toContainText("İade (geri gönderme) süreci henüz yok.");
    // Uydurma vaat yok: süre, ücretsiz kargo, gün sayısı geçmez
    await expect(info).not.toContainText(/gün|ücretsiz|saat|garanti/i);
  });

  test("favori kalbi ürün sayfasında: ad sabit, aria-pressed değişir, yenilemede kalır; misafir girişe yönlenir", async ({ browser, baseURL }) => {
    const context = await customerContext(browser, baseURL!, { width: 1440, height: 900 });
    try {
      const page = await context.newPage();
      await page.goto(`/urun/${target.slug}`);
      const heart = page.getByRole("button", { name: `${target.name} favorilerde` });
      await expect(heart).toBeEnabled();
      await expect(heart).toHaveAttribute("aria-pressed", "false");
      await heart.click();
      await expect(heart).toHaveAttribute("aria-pressed", "true");
      await expect(heart).toHaveAccessibleName(`${target.name} favorilerde`);
      await expect(page.locator("header").getByTestId("favorites-count")).toHaveText("1");
      // Arayüz iyimser güncellenir; yenilemeden önce yazmanın sunucuya ulaşmasını bekle (yoksa yenileme uçuştaki PUT'u keser).
      await expect.poll(async () => ((await (await page.request.get(`${api}/favorites/ids`)).json()) as { productIds: string[] }).productIds).toEqual([target.id]);
      await page.reload();
      await expect(page.getByRole("button", { name: `${target.name} favorilerde` })).toHaveAttribute("aria-pressed", "true");
      expect(((await (await page.request.get(`${api}/favorites/ids`)).json()) as { productIds: string[] }).productIds).toEqual([target.id]);
    } finally { await context.close(); }
    const guest = await browser.newContext({ baseURL: baseURL!, viewport: { width: 1440, height: 900 } });
    try {
      const page = await guest.newPage();
      await page.goto(`/urun/${target.slug}`);
      const heart = page.getByRole("button", { name: `${target.name} favorilerde` });
      await expect(heart).toBeEnabled();
      await heart.click();
      await expect(page).toHaveURL(/\/giris\?next=/);
      expect(new URL(page.url()).searchParams.get("next")).toBe(`/urun/${target.slug}`);
    } finally { await guest.close(); }
  });

  test("masaüstünde sabit çubuk yok; erişilebilirlik (axe) ve taşma", async ({ page }) => {
    await page.goto(`/urun/${target.slug}`);
    await expect(page.getByRole("heading", { level: 2, name: "Seçenekler" })).toBeVisible();
    await expect(page.getByTestId("sticky-cart-bar")).toBeHidden();
    expect(await noHorizontalOverflow(page)).toBe(true);
    expect(await axeSerious(page)).toEqual([]);
  });

  test("sipariş ayrıntısı: durum geçmişi zaman çizelgesi (İstanbul saati) ve kalemde ürün bağlantısı", async ({ browser, baseURL }) => {
    const sellable = await mkSellable(admin, { stock: 5 });
    const { customer, order } = await customerWithOrder(browser, baseURL!, sellable, 1);
    try {
      const page = customer.page;
      await page.goto(`/siparisler/${order.id}`);
      await expect(page.getByTestId("order-number")).toBeVisible();
      const timeline = page.getByTestId("order-timeline");
      await expect(timeline.locator("li")).toHaveCount(1);
      expect(await timeline.evaluate((el) => el.tagName)).toBe("OL");
      await expect(timeline.locator("li").first()).toContainText("Alındı");
      await expect(timeline.locator("li").first()).toHaveAttribute("aria-current", "step");
      // Kalem adı ürün sayfasına bağlanır
      const link = page.getByTestId("order-item").getByRole("link", { name: sellable.product.name });
      await expect(link).toHaveAttribute("href", `/urun/${sellable.slug}`);

      // İptalden sonra ikinci adım eklenir; saatler API'deki UTC değerlerinin İstanbul gösterimidir
      const cancel = await orderSend(customer.request, baseURL!, `/orders/${order.id}/cancel`);
      expect(cancel.status()).toBe(200);
      await page.reload();
      await expect(page.getByTestId("order-number")).toBeVisible();
      const items = timeline.locator("li");
      await expect(items).toHaveCount(2);
      await expect(items.nth(0)).toContainText("Alındı");
      await expect(items.nth(1)).toContainText("İptal edildi");
      await expect(items.nth(1)).toHaveAttribute("aria-current", "step");
      await expect(items.nth(0)).not.toHaveAttribute("aria-current", "step");
      const detail = await (await customer.request.get(`${api}/orders/${order.id}`)).json() as { statusHistory: { occurredAt: string }[] };
      for (let i = 0; i < 2; i++) await expect(items.nth(i).locator("time")).toHaveText(formatDateTimeIstanbul(detail.statusHistory[i].occurredAt));
      expect(await axeSerious(page)).toEqual([]);

      await link.click();
      await expect(page).toHaveURL(new RegExp(`/urun/${sellable.slug}$`));
    } finally { await customer.context.close(); await retire(admin, sellable); }
  });
});

test.describe("mobil 390", () => {
  const viewport = { width: 390, height: 844 };
  test.use({ viewport });

  test("sabit çubuk (tek seçenek): ilk açılışta gizli (başlığı örtmez), formun altına inince belirir, form görününce gizlenir, alt gezintiyle çakışmaz, hızlı ekle sepete ekler", async ({ browser, baseURL }) => {
    const context = await customerContext(browser, baseURL!, viewport);
    try {
      const page = await context.newPage();
      await page.goto(`/urun/${quick.slug}`);
      const bar = page.getByTestId("sticky-cart-bar");
      const nav = page.getByRole("navigation", { name: "Alt gezinti" });
      const quickButton = bar.getByRole("button", { name: "Hızlı ekle" });
      // İlk açılış (scroll 0): form henüz aşağıda, çubuk gizli ve inert; başlık örtülmez
      await expect(page.getByRole("heading", { level: 1, name: quick.name })).toBeVisible();
      await expect(page.getByRole("button", { name: `${quick.name} favorilerde` })).toBeEnabled(); // hidrasyon bitti
      await expect(bar).toHaveAttribute("data-visible", "false");
      await expect(bar).toBeHidden();
      await expect(bar).toHaveAttribute("inert", "");
      await expect(page.getByRole("group", { name: "Hızlı sepete ekle" })).toHaveCount(0); // gizliyken erişilebilirlik ağacında yok

      // Formun altına inilince çubuk belirir
      await pastForm(page);
      await expect(bar).toHaveAttribute("data-visible", "true");
      await expect(bar).toContainText(quick.name);
      await expect(bar).toContainText(formatPriceMinor(PRICE));
      // Alt gezintinin hemen üstünde: dikeyde örtüşme yok
      await expect.poll(async () => { const b = await bar.boundingBox(); const n = await nav.boundingBox(); return b && n ? b.y + b.height <= n.y + 1 : false; }).toBe(true);
      expect((await bar.boundingBox())!.width).toBeGreaterThanOrEqual(viewport.width - 1);
      expect((await quickButton.boundingBox())!.height).toBeGreaterThanOrEqual(43.5);
      // Çubuktaki düğmenin adı farklıdır: asıl "Sepete ekle" tek kalır
      await expect(page.getByRole("button", { name: "Sepete ekle" })).toHaveCount(1);

      // Asıl form görünür alana gelince çubuk yine gizlenir
      await page.locator("section[aria-labelledby='varyantlar']").evaluate((el) => el.scrollIntoView({ block: "center" }));
      await expect(bar).toHaveAttribute("data-visible", "false");
      await expect(bar).toBeHidden();
      await expect(page.getByRole("button", { name: "Sepete ekle" })).toBeVisible();
      // En üste dönünce de gizli
      await page.evaluate(() => window.scrollTo(0, 0));
      await expect(bar).toHaveAttribute("data-visible", "false");

      // Tekrar formun altına in: hızlı ekle asıl add() ile sepete ekler
      await pastForm(page);
      await expect(bar).toHaveAttribute("data-visible", "true");
      await expect(quickButton).toBeEnabled();
      await quickButton.click();
      await expect(bar.getByRole("link", { name: "Sepete git" })).toBeVisible();
      await expect(page.getByRole("status").filter({ hasText: "Sepete eklendi" })).toHaveCount(1);
      const cart = await getCart(page.request);
      expect(cart.items).toHaveLength(1);
      expect(cart.items[0].quantity).toBe(1);
      expect(await noHorizontalOverflow(page)).toBe(true);
      expect(await axeSerious(page)).toEqual([]);
    } finally { await context.close(); }
  });

  test("birden çok seçenek: çubuk düğmesi asıl formdaki seçili seçeneğe kaydırıp odaklar; sepete eklemez", async ({ browser, baseURL }) => {
    const context = await customerContext(browser, baseURL!, viewport);
    try {
      const page = await context.newPage();
      await page.goto(`/urun/${target.slug}`);
      const bar = page.getByTestId("sticky-cart-bar");
      await expect(bar).toHaveAttribute("data-visible", "false"); // ilk açılışta gizli
      await pastForm(page);
      await expect(bar).toHaveAttribute("data-visible", "true");
      await expect(bar).toContainText("başlangıç");
      await bar.getByRole("button", { name: "Seçenekleri gör" }).click();
      await expect(page.locator("input[name='varyant']:checked")).toBeFocused();
      await expect(bar).toHaveAttribute("data-visible", "false");
      expect((await getCart(page.request)).items).toHaveLength(0);
    } finally { await context.close(); }
  });

  test("misafir: çubukta giriş bağlantısı; masaüstü genişliğe geçince çubuk yok", async ({ page }) => {
    await page.goto(`/urun/${quick.slug}`);
    const bar = page.getByTestId("sticky-cart-bar");
    await expect(bar).toHaveAttribute("data-visible", "false");
    await pastForm(page);
    await expect(bar).toHaveAttribute("data-visible", "true");
    await expect(bar.getByRole("link", { name: "Giriş yap" })).toHaveAttribute("href", /^\/giris\?next=/);
    await page.setViewportSize({ width: 1280, height: 900 });
    await expect(bar).toBeHidden();
  });

  test("azaltılmış hareket: çubuk geçişi neredeyse anlık", async ({ browser, baseURL }) => {
    const context = await customerContext(browser, baseURL!, viewport, { reducedMotion: "reduce" });
    try {
      const page = await context.newPage();
      await page.goto(`/urun/${quick.slug}`);
      const bar = page.getByTestId("sticky-cart-bar");
      await pastForm(page);
      await expect(bar).toHaveAttribute("data-visible", "true");
      const seconds = await bar.evaluate((el) => parseFloat(getComputedStyle(el).transitionDuration.split(",")[0]));
      expect(seconds).toBeLessThanOrEqual(0.001);
    } finally { await context.close(); }
  });

  test("ürün sayfası 390: galeri şeridi yatay kayar, sayfa taşmaz, kalp ve axe", async ({ page }) => {
    await page.goto(`/urun/${target.slug}`);
    await expect(page.getByTestId("gallery-thumb")).toHaveCount(3);
    expect(await noHorizontalOverflow(page)).toBe(true);
    await expect(page.getByRole("button", { name: `${target.name} favorilerde` })).toBeVisible();
    expect((await page.getByTestId("gallery-thumb").first().boundingBox())!.width).toBeGreaterThanOrEqual(44);
    expect(await axeSerious(page)).toEqual([]);
  });

  test("hesap menüsü 390: ilk etiket (Özet) diğer içerikle aynı 16px kenar boşluğunda; yatay kaydırma korunur", async ({ browser, baseURL }) => {
    const context = await customerContext(browser, baseURL!, viewport);
    try {
      const page = await context.newPage();
      await page.goto("/hesabim");
      const nav = page.getByTestId("account-nav");
      const first = nav.getByRole("link", { name: "Özet" });
      await expect(first).toBeVisible();
      await expect.poll(async () => Math.round((await first.boundingBox())!.x)).toBeGreaterThanOrEqual(16);
      expect(Math.round((await first.boundingBox())!.x)).toBeLessThanOrEqual(17);
      expect(await nav.evaluate((el) => el.scrollWidth > el.clientWidth)).toBe(true); // sığmayan etiketler hâlâ kaydırılır
      expect(await noHorizontalOverflow(page)).toBe(true);
    } finally { await context.close(); }
  });
});

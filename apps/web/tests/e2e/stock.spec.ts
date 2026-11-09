import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { loadVariants, PUBLIC_CAP, variantRow, withStock } from "./support";

// Stok durumu gerçek API'den okunur; test beklentiyi API yanıtından türetir, değer uydurmaz.
// E2E host'u E2E-DEPO fixture'ı ile belirlenimli stok kurar (demo katalogda gerçek stok seed edilmez, D-047); bu dosyanın
// sonundaki testler fixture kuralını canlı API'ye karşı doğrular.
type ListItem = { slug: string; name: string; category: { slug: string }; inStock: boolean };
type Variant = { sku: string; displayName: string; inStock: boolean; availableQuantity: number };
type Detail = ListItem & { variants: Variant[] };

const api = "/api/backend/api/v1";
let items: ListItem[];
let detail: Detail;

// Beklenen metin, uygulamadan bağımsız yazılmış kuraldır: 1..5 arası "Son N adet", cap (20) üstü sayı asla gösterilmez.
const expectedText = (inStock: boolean, quantity?: number) =>
  !inStock ? "Stokta yok" : quantity !== undefined && quantity >= 1 && quantity <= 5 ? `Son ${quantity} adet` : "Stokta";

test.beforeAll(async ({ request }) => {
  const list = await request.get(`${api}/products?pageSize=50`);
  expect(list.ok()).toBeTruthy();
  items = (await list.json()).items;
  expect(items.length).toBeGreaterThan(0);
  for (const item of items) expect(typeof item.inStock, item.slug).toBe("boolean");
  const response = await request.get(`${api}/products/${items[0].slug}`);
  expect(response.ok()).toBeTruthy();
  detail = await response.json();
  expect(detail.variants.length).toBeGreaterThan(0);
});

// Katalog bölümleri Suspense ile akar; görünür bir öğeyi beklemeden evaluate/evaluateAll okunmaz.
async function gotoHome(page: Page) {
  await page.goto("/");
  await expect(page.locator("#urunler")).toBeVisible();
  await expect(page.locator("main article:has(h3):visible").first()).toBeVisible();
}

async function gotoProduct(page: Page) {
  const response = await page.goto(`/urun/${detail.slug}`);
  expect(response?.status()).toBe(200);
  await expect(page.getByRole("heading", { level: 1, name: detail.name })).toBeVisible();
  await expect(page.getByRole("heading", { level: 2, name: "Seçenekler" })).toBeVisible();
}

async function checks(page: Page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  const results = await new AxeBuilder({ page }).analyze();
  expect(results.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`)).toEqual([]);
}

for (const [width, height] of [[390, 844], [768, 1024], [1440, 900]]) {
  test.describe(`stok durumu ${width}px`, () => {
    test.use({ viewport: { width, height } });

    test("ana sayfa kartları API'deki stok durumunu gösterir", async ({ page }, info) => {
      await gotoHome(page);
      const cards = page.locator("main article:has(h3):visible");
      await expect(cards).toHaveCount(items.length);
      for (const item of items) {
        const card = cards.filter({ has: page.getByRole("heading", { level: 3, name: item.name, exact: true }) });
        await expect(card, item.slug).toHaveCount(1);
        const label = card.locator("[data-stock]");
        await expect(label).toHaveCount(1);
        await expect(label).toHaveText(expectedText(item.inStock));
        await expect(label).toHaveAttribute("data-stock", item.inStock ? "in" : "out");
        // Kartta sepete ekleme yok (varyant seçimi ürün sayfasında): seçenekleri gösteren bağlantı var, aktif sepet düğmesi yok.
        await expect(card.getByRole("link", { name: /için seçenekleri gör/i })).toHaveAttribute("href", `/urun/${item.slug}`);
        await expect(card.getByRole("button", { name: /sepete ekle/i })).toHaveCount(0);
      }
      await checks(page);
      const file = info.outputPath(`stok-kartlar-${width}.png`);
      await page.screenshot({ path: file, fullPage: true });
      await info.attach(`stok-kartlar-${width}`, { path: file, contentType: "image/png" });
    });

    test("kategori sayfası kartları stok durumunu gösterir", async ({ page }) => {
      const slug = items[0].category.slug;
      const inCategory = items.filter((i) => i.category.slug === slug);
      await page.goto(`/kategori/${slug}`);
      const cards = page.locator("main article:has(h3):visible");
      await expect(cards.first()).toBeVisible();
      await expect(cards).toHaveCount(inCategory.length);
      for (const item of inCategory) {
        const card = cards.filter({ has: page.getByRole("heading", { level: 3, name: item.name, exact: true }) });
        await expect(card.locator("[data-stock]")).toHaveText(expectedText(item.inStock));
      }
      await checks(page);
    });

    test("ürün sayfası ürün ve varyant stok durumunu gösterir; satış açık demez", async ({ page }, info) => {
      await gotoProduct(page);
      const main = page.locator("main");
      await expect(page.getByTestId("product-stock").locator("[data-stock]")).toHaveText(expectedText(detail.inStock));
      const rows = main.locator("section[aria-labelledby='varyantlar'] li");
      await expect(rows).toHaveCount(detail.variants.length);
      for (const [index, variant] of detail.variants.entries()) {
        const row = rows.nth(index);
        await expect(row).toContainText(variant.displayName);
        await expect(row.locator("[data-stock]")).toHaveText(expectedText(variant.inStock, variant.availableQuantity));
      }
      // Cap üstü (20) hiçbir yerde kesin miktar gibi yazılmaz; "Son N adet" yalnızca 1..5.
      const text = await main.innerText();
      for (const match of text.matchAll(/Son (\d+) adet/g)) expect(Number(match[1])).toBeLessThanOrEqual(5);
      expect(text).not.toMatch(/\b20\+?\s*adet|stok(ta)?\s*:?\s*\d+/i);
      // Ödeme yok: dürüst metin durur (sipariş verirken ödeme alınmaz); satın alma/ödeme kontrolü yok. Anonim kullanıcı için sepete ekleme yerine
      // giriş gerekliliği gösterilir (hesap olmadan sepet yok) ve bağlantı bu sayfaya döner.
      await expect(main).toContainText("Sipariş verirken ödeme alınmaz");
      await expect(page.getByRole("button", { name: /satın al|ödeme yap|sepete ekle/i })).toHaveCount(0);
      await expect(page.getByTestId("signin-required")).toBeVisible();
      await expect(page.getByTestId("signin-required").getByRole("link", { name: "Giriş yap" })).toHaveAttribute("href", `/giris?next=${encodeURIComponent(`/urun/${detail.slug}`)}`);
      await checks(page);
      const file = info.outputPath(`stok-urun-${width}.png`);
      await page.screenshot({ path: file, fullPage: true });
      await info.attach(`stok-urun-${width}`, { path: file, contentType: "image/png" });
    });
  });
}

// Fixture kuralı canlı E2E API'ye karşı doğrulanır (örnekten varsayılmaz): tüm SKU'lar ordinal sıralı, sıra mod 4 → 0/3/12/50;
// herkese açık adet 20 ile sınırlıdır (50 → 20).
test("fixture kuralı: SKU sırası mod 4 stoğu belirler ve herkese açık adet 20'de kesilir", async ({ request }) => {
  const variants = await loadVariants(request);
  expect(variants.length).toBeGreaterThanOrEqual(8);
  for (const v of variants) {
    expect(v.availableQuantity, `${v.sku} (#${v.index})`).toBe(Math.min(v.stock, PUBLIC_CAP));
    expect(v.inStock, v.sku).toBe(v.stock > 0);
  }
  for (const stock of [0, 3, 12, 50]) expect(variants.some((v) => v.stock === stock), `stok ${stock}`).toBe(true);
  expect(Math.max(...variants.map((v) => v.availableQuantity))).toBe(PUBLIC_CAP);
});

test.describe("belirlenimli stok görünümü (1440px)", () => {
  test.use({ viewport: { width: 1440, height: 900 } });
  test("Stokta yok, Son 3 adet ve yalın Stokta (cap üstünde sayı yok) ürün sayfasında görünür", async ({ page, request }) => {
    const variants = await loadVariants(request);
    const expectations: [number, string][] = [[0, "Stokta yok"], [3, "Son 3 adet"], [12, "Stokta"], [50, "Stokta"]];
    for (const [stock, text] of expectations) {
      const variant = withStock(variants, stock);
      await page.goto(`/urun/${variant.productSlug}`);
      await expect(page.getByRole("heading", { level: 2, name: "Seçenekler" })).toBeVisible();
      const row = variantRow(page, variant);
      await expect(row.locator("[data-stock]"), `${variant.sku}`).toHaveText(text);
      const radio = row.getByRole("radio");
      if (stock === 0) await expect(radio).toBeDisabled(); else await expect(radio).toBeEnabled();
      // Etiket metni tam eşleşir: cap (20) ve depo (50) sayıları etikette asla yer almaz, "Son N adet" yalnız stok 3 iken.
    }
  });
});

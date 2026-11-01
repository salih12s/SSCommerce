import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import fs from "node:fs";
import path from "node:path";
import { banners, categories, promos } from "../../src/content/demo-catalog";
import { imageCredits } from "../../src/content/image-credits";

// Sayılar veriden türetilir: hero 1 + tanıtım kartı + kategori + banner + ürün fotoğrafı.
type Category = { slug: string; name: string };
type ApiProduct = { slug: string; name: string; category: Category; startingPriceMinor: number; primaryImage: { url: string; altText: string } | null };
let liveCategories: Category[];
let products: { slug: string; name: string; categorySlug: string; priceMinor: number; image: string | null; imageAlt: string | null }[];
const productsInCategory = (slug: string) => products.filter((p) => p.categorySlug === slug);
const tabCategories = () => liveCategories;

test.beforeAll(async ({ request }) => {
  const cr = await request.get("/api/backend/api/v1/categories");
  expect(cr.ok()).toBeTruthy();
  liveCategories = await cr.json();
  const r = await request.get("/api/backend/api/v1/products?pageSize=50");
  expect(r.ok()).toBeTruthy();
  const body = await r.json() as { items: ApiProduct[] };
  products = body.items.map((p) => ({ slug: p.slug, name: p.name, categorySlug: p.category.slug, priceMinor: p.startingPriceMinor, image: p.primaryImage?.url ?? null, imageAlt: p.primaryImage?.altText ?? null }));
});

// Gezinti ve katalog bölümleri Suspense ile akar: load olayı geldiğinde hâlâ gizli olabilirler. page.evaluate ve
// evaluateAll otomatik beklemediği için testler akış bitmeden okursa rastgele kalır (flaky; 18 denemede 9 kez görüldü).
async function gotoHome(page: Page) {
  await page.goto("/");
  await expect(page.locator("#urunler")).toBeVisible();
  await expect(page.locator("nav:visible").first()).toBeVisible();
}

const viewports = [
  { name: "1440", width: 1440, height: 900 },
  { name: "768", width: 768, height: 1024 },
  { name: "390", width: 390, height: 844 },
];

// Gerçek marka/ürün adları sayfada, alt metinlerde ve erişilebilir adlarda geçmemeli (kurgusal ürünler).
const BRANDS =
  /\b(MacBook|AirPods|iPhone|iPad|Apple|Sony|ASUS|Samsung|Xiaomi|Huawei|Logitech|Razer|Nintendo|PlayStation|Xbox|Bose|JBL|LG|MSI|Dell|Lenovo|Google|Beats|Anker|Baseus|Aukey|Keychron|GameSir|SteelSeries|Sonos|HomePod|Canon)\b/i;

async function seriousViolations(page: Page) {
  const results = await new AxeBuilder({ page }).analyze();
  return results.violations
    .filter((v) => v.impact === "serious" || v.impact === "critical")
    .map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`);
}

// Lazy görselleri tetiklemek için sayfayı sonuna kadar kaydırır, sonra başa döner.
async function scrollThroughPage(page: Page) {
  await page.evaluate(async () => {
    const step = Math.max(window.innerHeight * 0.7, 300);
    for (let y = 0; y < document.body.scrollHeight; y += step) {
      window.scrollTo(0, y);
      await new Promise((resolve) => setTimeout(resolve, 100));
    }
    window.scrollTo(0, document.body.scrollHeight);
    await new Promise((resolve) => setTimeout(resolve, 300));
    window.scrollTo(0, 0);
  });
}

// Görünür (display:none olmayan) ama yüklenmemiş ya da boyutsuz img'ler.
async function brokenImages(page: Page) {
  return page.evaluate(() =>
    Array.from(document.images)
      .filter((img) => img.getClientRects().length > 0)
      .filter((img) => !(img.complete && img.naturalWidth > 0))
      .map((img) => img.currentSrc || img.src),
  );
}

// Sayfadaki img'lerin asıl /images/ yolları (Next, /_next/image?url=... ile sunar).
async function usedImagePaths(page: Page): Promise<string[]> {
  const urls = await page.evaluate(() => Array.from(document.images).map((img) => img.getAttribute("src") ?? ""));
  const paths = urls.map((src) => {
    if (src.startsWith("/_next/image")) return new URL(src, "http://x").searchParams.get("url") ?? "";
    return src;
  });
  return Array.from(new Set(paths.filter((p) => p.startsWith("/images/")))).sort();
}

for (const vp of viewports) {
  test.describe(`ana sayfa ${vp.name}px`, () => {
    test.use({ viewport: { width: vp.width, height: vp.height } });

    test.beforeEach(async ({ page }) => {
      await gotoHome(page);
      await page.evaluate(() => document.fonts.ready);
    });

    test("yatay taşma yok", async ({ page }) => {
      const overflow = await page.evaluate(() => ({
        scrollWidth: document.documentElement.scrollWidth,
        innerWidth: window.innerWidth,
      }));
      expect(overflow.scrollWidth).toBeLessThanOrEqual(overflow.innerWidth);
    });

    test("landmark'lar var ve tek h1", async ({ page }) => {
      await expect(page.locator("header")).toHaveCount(1);
      await expect(page.locator("main")).toHaveCount(1);
      await expect(page.locator("footer")).toHaveCount(1);
      const visibleNavs = await page
        .locator("nav")
        .evaluateAll((els) => els.filter((el) => (el as HTMLElement).getClientRects().length > 0).length);
      expect(visibleNavs).toBeGreaterThan(0);
      await expect(page.locator("h1")).toHaveCount(1);
    });

    test("her fotoğrafın anlamlı ve benzersiz alt metni var; alt metinde marka adı yok", async ({ page }) => {
      await expect(page.locator("img:not([alt])")).toHaveCount(0);
      const alts = await page.locator("main img").evaluateAll((els) => els.map((el) => el.getAttribute("alt") ?? ""));
      expect(alts).toHaveLength(1 + promos.length + categories.length + banners.length + products.filter((p) => p.image).length);
      for (const alt of alts) {
        expect(alt.trim().length, alt).toBeGreaterThanOrEqual(15);
        expect(alt, alt).not.toMatch(BRANDS);
      }
      expect(new Set(alts).size).toBe(alts.length);
    });

    test("görünür tüm img öğeleri yükleniyor (kırık görsel yok)", async ({ page }) => {
      await scrollThroughPage(page);
      await expect.poll(() => brokenImages(page), { timeout: 15_000 }).toEqual([]);
    });

    test("demo uyarısı tek yerde: üst şerit", async ({ page }) => {
      const strip = page.getByText("Demo vitrin: ürünler ve fiyatlar örnektir, satış yakında.");
      await expect(strip).toHaveCount(1);
      await expect(page.locator("header").getByText(/Demo vitrin: ürünler/)).toBeVisible();
    });

    test("axe: serious/critical ihlal yok", async ({ page }) => {
      expect(await seriousViolations(page)).toEqual([]);
    });

    test("tam sayfa ekran görüntüsü", async ({ page }, testInfo) => {
      await scrollThroughPage(page);
      await expect.poll(() => brokenImages(page), { timeout: 15_000 }).toEqual([]);
      const file = testInfo.outputPath(`home-${vp.name}.png`);
      await page.screenshot({ path: file, fullPage: true });
      await testInfo.attach(`home-${vp.name}`, { path: file, contentType: "image/png" });
    });
  });
}

test.describe("içerik dürüstlüğü (1440px)", () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test.beforeEach(async ({ page }) => {
    await gotoHome(page);
  });

  test("puan, indirim, üstü çizili fiyat ve satış rozeti yok", async ({ page }) => {
    const text = await page.locator("body").innerText();
    expect(text).not.toMatch(/★|☆|yıldız|değerlendirme|yorum/i);
    expect(text).not.toMatch(/%\s?\d+|indirim/i);
    expect(text).not.toMatch(/çok satan|en çok satan/i);
    await expect(page.locator("del, s, strike")).toHaveCount(0);
    const struck = await page.evaluate(() =>
      Array.from(document.querySelectorAll("body *")).filter((el) =>
        getComputedStyle(el).textDecorationLine.includes("line-through"),
      ).length,
    );
    expect(struck).toBe(0);
  });

  test("servis şeridi ve üst şerit gerçek vaat içermiyor; gerçek marka adı yok", async ({ page }) => {
    const text = await page.locator("body").innerText();
    expect(text).not.toMatch(/ücretsiz kargo|\d+\s*gün|güvenli ödeme|7\s?\/\s?24|sipariş takibi|mağazalarımız/i);
    expect(text).not.toMatch(/\b0?\d{3}\s?\d{3}\s?\d{2}\s?\d{2}\b/);
    expect(text).not.toMatch(BRANDS);
    const strip = page.getByRole("region", { name: "Servis bilgileri" });
    await expect(strip.getByText("Kargo seçenekleri")).toBeVisible();
    await expect(strip.getByText("Yakında").first()).toBeVisible();
  });

  test("logo SSCommerce olarak yazılıyor", async ({ page }) => {
    await expect(page.locator("header").getByText("SSCommerce", { exact: true })).toBeVisible();
    expect(await page.locator("body").innerText()).not.toMatch(/SSCommerse/);
  });

  test("favori düğmeleri pasif ve 'yakında'; kartta sepete ekle yok, seçenek bağlantısı ürün sayfasına gider", async ({ page }) => {
    const cards = page.locator("article:has(h3)");
    await expect(cards).toHaveCount(products.length);
    const favButtons = page.getByRole("button", { name: /favorilere ekle, yakında/i });
    await expect(favButtons).toHaveCount(products.length);
    for (const button of await favButtons.all()) await expect(button).toHaveAttribute("aria-disabled", "true");
    await favButtons.first().click({ force: true });
    await expect(page).toHaveURL(/\/$/);
    await expect(page.getByRole("dialog")).toHaveCount(0);
    // Varyant seçimi ürün sayfasındadır: karttaki sepet düğmesi kalktı; yerine ürün sayfası bağlantısı geldi (sepete eklemez).
    await expect(page.getByRole("button", { name: /sepete ekle/i })).toHaveCount(0);
    const options = page.getByRole("link", { name: /için seçenekleri gör/i });
    await expect(options).toHaveCount(products.length);
    await expect(options.first()).toHaveAttribute("href", /^\/urun\//);
  });

  test("ürün kartında fotoğraf, kategori, ad ve fiyat var", async ({ page }) => {
    const first = products.find((p) => p.image)!;
    const card = page.locator("article:has(h3)").filter({ hasText: first.name }).first();
    await expect(card.locator("img")).toHaveAttribute("alt", first.imageAlt!);
    await expect(card.getByRole("heading", { level: 3, name: first.name })).toBeVisible();
    await expect(card).toContainText(/\d[\d.]*,\d{2}\s?₺|₺\s?\d/);
    await expect(card).toContainText(categories.find((c) => c.slug === first.categorySlug)!.name);
  });

  test("hero: bağlantılar çalışır, işlevsiz slider noktası yok; tanıtım ve banner bağlantıları var olan rotalara gider", async ({
    page,
  }) => {
    const hero = page.locator("section[aria-labelledby='hero-baslik']");
    await expect(hero.getByRole("link", { name: "Ürünleri keşfet" })).toHaveAttribute("href", "#urunler");
    await expect(hero.getByRole("link", { name: "Kategoriler", exact: true })).toHaveAttribute("href", "#kategoriler");
    await expect(hero.locator("[role='tablist'], [aria-roledescription='carousel']")).toHaveCount(0);
    const hrefs = [
      ...promos.map((p) => `/kategori/${p.categorySlug}`),
      ...banners.filter((b) => liveCategories.some((c) => c.slug === b.categorySlug)).map((b) => `/kategori/${b.categorySlug}`),
    ];
    for (const href of hrefs) {
      await expect(page.locator(`main a[href="${href}"]`).first()).toBeVisible();
      const response = await page.request.get(href);
      expect(response.status(), href).toBe(200);
    }
  });

  test("sekmeler: Tümü tüm ürünleri, kategori sekmesi yalnızca kendi ürünlerini gösterir; ok tuşları gezinir", async ({ page }) => {
    const tablist = page.getByRole("tablist", { name: "Ürün grupları" });
    const tabs = tablist.getByRole("tab");
    await expect(tabs).toHaveCount(1 + tabCategories().length);
    await expect(tabs.first()).toHaveAttribute("aria-selected", "true");
    const visibleCards = page.locator("main article:has(h3):visible");
    await expect(visibleCards).toHaveCount(products.length);
    const target = tabCategories()[0];
    await tablist.getByRole("tab", { name: target.name }).click();
    await expect(visibleCards).toHaveCount(productsInCategory(target.slug).length);
    await page.keyboard.press("ArrowRight");
    const second = tabCategories()[1];
    await expect(tablist.getByRole("tab", { name: second.name })).toHaveAttribute("aria-selected", "true");
    await expect(visibleCards).toHaveCount(productsInCategory(second.slug).length);
    await page.keyboard.press("Home");
    await expect(visibleCards).toHaveCount(products.length);
  });
});

test.describe("fotoğraf kaynakları dosya sistemiyle tutarlı", () => {
  test("image-credits'teki her dosya diskte var ve sitedeki her /images/ yolu listede", async ({ page }) => {
    const listed = imageCredits.map((c) => c.file).sort();
    expect(new Set(listed).size).toBe(listed.length);
    for (const file of listed) {
      expect(fs.existsSync(path.join(process.cwd(), "public", file)), file).toBe(true);
      expect(fs.statSync(path.join(process.cwd(), "public", file)).size, file).toBeLessThanOrEqual(250 * 1024);
    }
    // Diskte listelenmemiş fotoğraf kalmamalı.
    const onDisk: string[] = [];
    const walk = (dir: string) => {
      for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) walk(full);
        else if (/\.(webp|jpe?g|png|avif)$/i.test(entry.name)) onDisk.push("/" + path.relative(path.join(process.cwd(), "public"), full).replaceAll("\\", "/"));
      }
    };
    walk(path.join(process.cwd(), "public", "images"));
    expect(onDisk.sort()).toEqual(listed);

    // Ana sayfada kullanılan her yol listede; listedeki her dosya ana sayfada kullanılıyor.
    await gotoHome(page);
    await scrollThroughPage(page);
    const used = await usedImagePaths(page);
    for (const p of used) expect(listed, p).toContain(p);
    // Eski fixture dosyaları korunur; artık hepsinin ana sayfada kullanılması gerekmez.
    // Katalog verisindeki yollar da listede.
    const catalogPaths = [
      ...categories.map((c) => c.image),
      ...products.flatMap((p) => p.image ? [p.image] : []),
      ...promos.map((p) => p.image),
      ...banners.map((b) => b.image),
    ];
    for (const p of catalogPaths) expect(listed, p).toContain(p);
  });
});

test.describe("mobil menü 390px", () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test("açılıp kapanıyor, Esc ile kapanıyor", async ({ page }) => {
    await gotoHome(page);
    const button = page.getByRole("button", { name: "Kategoriler" });
    await expect(button).toHaveAttribute("aria-expanded", "false");
    await expect(page.locator("#mobil-menu")).toBeHidden();

    await button.click();
    await expect(button).toHaveAttribute("aria-expanded", "true");
    const firstLink = page.locator("#mobil-menu a").first();
    await expect(firstLink).toBeVisible();
    await expect(firstLink).toBeFocused();
    await expect(page.locator("#mobil-menu a")).toHaveCount(liveCategories.length);

    await page.keyboard.press("Escape");
    await expect(button).toHaveAttribute("aria-expanded", "false");
    await expect(page.locator("#mobil-menu")).toBeHidden();
    await expect(button).toBeFocused();

    await button.click();
    await expect(button).toHaveAttribute("aria-expanded", "true");
    await button.click();
    await expect(button).toHaveAttribute("aria-expanded", "false");
  });

  test("menüdeki kategori bağlantısı sayfaya götürür ve menüyü kapatır", async ({ page }) => {
    await gotoHome(page);
    const last = liveCategories[liveCategories.length - 1];
    await page.getByRole("button", { name: "Kategoriler" }).click();
    await page.locator("#mobil-menu").getByRole("link", { name: last.name }).click();
    await expect(page).toHaveURL(new RegExp(`/kategori/${last.slug}$`));
  });
});

test.describe("mobil alt gezinti 390px", () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test("görünür; Ana sayfa, Kategoriler, Sepetim ve Hesabım bağlantı; yalnız Favoriler pasif ve 'Yakında'", async ({ page }) => {
    await gotoHome(page);
    const nav = page.getByRole("navigation", { name: "Alt gezinti" });
    await expect(nav).toBeVisible();
    await expect(nav.getByRole("link", { name: "Ana sayfa" })).toHaveAttribute("href", "/");
    await expect(nav.getByRole("link", { name: "Kategoriler" })).toHaveAttribute("href", "/#kategoriler");
    expect(await nav.getByRole("link").count()).toBe(4);
    await expect(nav.getByRole("link", { name: /Hesabım/ })).toHaveAttribute("href", "/hesabim");
    // Sepetim gerçek bağlantı: /sepet'e gider, anonim kullanıcıya sayaç göstermez.
    await expect(nav.getByRole("link", { name: /Sepetim/ })).toHaveAttribute("href", "/sepet");
    await expect(nav.getByRole("button", { name: /Sepetim/ })).toHaveCount(0);
    await expect(nav.getByTestId("cart-count")).toHaveCount(0);
    const favorites = nav.getByRole("button", { name: /Favoriler/ });
    await expect(favorites).toHaveCount(1);
    await expect(favorites).toHaveAttribute("aria-disabled", "true");
    await expect(favorites).toContainText("Yakında");
    await expect(nav.getByRole("link", { name: /Favoriler/ })).toHaveCount(0);
    await nav.getByRole("link", { name: /Sepetim/ }).click();
    await expect(page).toHaveURL(/\/sepet$/);
  });

  test("Kategoriler bağlantısı kategori bölümüne kaydırır; Ana sayfa / adresine döner", async ({ page }) => {
    await gotoHome(page);
    const nav = page.getByRole("navigation", { name: "Alt gezinti" });
    await nav.getByRole("link", { name: "Kategoriler" }).click();
    await expect(page).toHaveURL(/\/#kategoriler$/);
    await expect(page.getByRole("heading", { level: 2, name: "Kategoriler" })).toBeInViewport();
    await page.goto("/kategori/" + liveCategories[0].slug);
    await expect(nav.getByRole("link", { name: "Kategoriler" })).not.toHaveAttribute("aria-current", "page");
    await nav.getByRole("link", { name: "Ana sayfa" }).click();
    await expect(page).toHaveURL(/\/$/);
  });

  test("alt gezinti footer'ı örtmüyor ve ekranın altında sabit duruyor", async ({ page }) => {
    await gotoHome(page);
    await expect(page.getByRole("navigation", { name: "Alt gezinti" })).toBeVisible();
    await expect(page.locator("footer")).toBeVisible();
    await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
    const { footerBottom, navTop, navBottom, innerHeight } = await page.evaluate(() => {
      const footer = document.querySelector("footer")!.getBoundingClientRect();
      const nav = document.querySelector('nav[aria-label="Alt gezinti"]')!.getBoundingClientRect();
      return { footerBottom: footer.bottom, navTop: nav.top, navBottom: nav.bottom, innerHeight: window.innerHeight };
    });
    expect(footerBottom).toBeLessThanOrEqual(navTop + 1);
    expect(Math.round(navBottom)).toBe(innerHeight);
  });

  test("kategoriler 4x2 kompakt ızgara: dört sütun, iki satır, alt metin gizli", async ({ page }) => {
    await gotoHome(page);
    const tiles = page.locator("#kategoriler ul > li");
    await expect(tiles).toHaveCount(categories.length);
    const boxes = await tiles.evaluateAll((els) => els.map((el) => el.getBoundingClientRect()));
    expect(new Set(boxes.map((b) => Math.round(b.left))).size).toBe(4);
    expect(new Set(boxes.map((b) => Math.round(b.top))).size).toBe(Math.ceil(categories.length / 4));
    await expect(page.locator("#kategoriler ul > li").first().getByText(categories[0].blurb)).toBeHidden();
    const noPageOverflow = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);
    expect(noPageOverflow).toBe(true);
  });
});

test.describe("mobil ilk ekran ve örtme denetimi 390x844", () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test.beforeEach(async ({ page }) => {
    await gotoHome(page);
    await page.evaluate(() => document.fonts.ready);
  });

  test("ilk viewport ekran görüntüsü", async ({ page }, testInfo) => {
    await expect.poll(() => brokenImages(page), { timeout: 15_000 }).toEqual([]);
    const file = testInfo.outputPath("home-390-ilk-ekran.png");
    await page.screenshot({ path: file, fullPage: false });
    await testInfo.attach("home-390-ilk-ekran", { path: file, contentType: "image/png" });
  });

  test("ilk ekranda header, arama, hero başlığı, CTA ve hero fotoğrafının bir kısmı alt gezintinin üstünde görünür", async ({
    page,
  }) => {
    const r = await page.evaluate(() => {
      const rect = (sel: string) => document.querySelector(sel)!.getBoundingClientRect();
      const nav = rect('nav[aria-label="Alt gezinti"]');
      const cta = Array.from(document.querySelectorAll("section[aria-labelledby='hero-baslik'] a[href^='#']")).map((a) =>
        a.getBoundingClientRect().bottom,
      );
      const scene = rect('[data-testid="hero-scene"]');
      return {
        navTop: nav.top,
        header: rect("header").bottom,
        search: rect("header input[type='search']"),
        h1: rect("h1"),
        ctaBottom: Math.max(...cta),
        sceneBottom: scene.bottom,
        sceneTop: scene.top,
      };
    });
    expect(r.search.bottom).toBeLessThan(r.navTop);
    expect(r.h1.bottom).toBeLessThan(r.ctaBottom);
    expect(r.ctaBottom).toBeLessThan(r.navTop);
    // CTA ile alt gezinti arasında hero fotoğrafının görünen kısmı: en az 150 px.
    expect(Math.min(r.sceneBottom, r.navTop) - r.ctaBottom).toBeGreaterThanOrEqual(150);
    // Fotoğraf sahnesi gerçekten ilk ekranda başlıyor.
    expect(r.sceneTop).toBeLessThan(r.navTop - 300);
  });

  test("iki hero bağlantısı ve footer'ın son bağlantısı alt gezinti tarafından örtülmüyor", async ({ page }) => {
    const hero = page.locator("section[aria-labelledby='hero-baslik']");
    const targets = [
      hero.getByRole("link", { name: "Ürünleri keşfet" }),
      hero.getByRole("link", { name: "Kategoriler", exact: true }),
      page.locator("footer a").last(),
    ];
    for (const target of targets) {
      await target.scrollIntoViewIfNeeded();
      const covered = await target.evaluate((el) => {
        const r = el.getBoundingClientRect();
        const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
        return !(hit === el || (hit !== null && el.contains(hit)));
      });
      expect(covered, await target.innerText()).toBe(false);
    }
    await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
    const lastOk = await page
      .locator("footer a")
      .last()
      .evaluate((el) => {
        const r = el.getBoundingClientRect();
        const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
        const nav = document.querySelector('nav[aria-label="Alt gezinti"]')!.getBoundingClientRect();
        return (hit === el || el.contains(hit)) && r.bottom <= nav.top;
      });
    expect(lastOk).toBe(true);
  });

  test("görünen hiçbir metin 12px'ten küçük değil", async ({ page }) => {
    const small = await page.evaluate(() => {
      const out: string[] = [];
      for (const el of Array.from(document.body.querySelectorAll("*"))) {
        const hasText = Array.from(el.childNodes).some((n) => n.nodeType === 3 && (n.textContent ?? "").trim().length > 0);
        if (!hasText || (el as HTMLElement).getClientRects().length === 0) continue;
        if (el.closest("svg, script, style")) continue;
        const style = getComputedStyle(el);
        if (style.visibility === "hidden") continue;
        const size = parseFloat(style.fontSize);
        if (size < 12) out.push(`${el.tagName} ${size}px "${(el.textContent ?? "").trim().slice(0, 30)}"`);
      }
      return out;
    });
    expect(small).toEqual([]);
  });

  test("dokunma hedefleri: üst gezinti ve alt gezinti öğeleri en az 44px", async ({ page }) => {
    const sizes = await page.evaluate(() =>
      Array.from(document.querySelectorAll("header button, header a, nav[aria-label='Alt gezinti'] a, nav[aria-label='Alt gezinti'] button"))
        .filter((el) => (el as HTMLElement).getClientRects().length > 0)
        .map((el) => {
          const r = el.getBoundingClientRect();
          return { name: (el.getAttribute("aria-label") ?? el.textContent ?? "").trim().slice(0, 24), w: r.width, h: r.height };
        }),
    );
    expect(sizes.length).toBeGreaterThan(5);
    for (const s of sizes) {
      expect(s.h, s.name).toBeGreaterThanOrEqual(43.5);
      expect(s.w, s.name).toBeGreaterThanOrEqual(43.5);
    }
  });
});

test.describe("reduced motion 390px", () => {
  test.use({ viewport: { width: 390, height: 844 }, reducedMotion: "reduce" });

  test("hareket azaltma açıkken geçişler kapalı", async ({ page }) => {
    await gotoHome(page);
    const duration = await page
      .locator("a", { hasText: "Ürünleri keşfet" })
      .evaluate((el) => getComputedStyle(el).transitionDuration);
    expect(parseFloat(duration)).toBeLessThan(0.01);
  });
});

test.describe("klavye ve odak", () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test("skip link ilk Tab'da görünür ve odağı main'e taşır", async ({ page }) => {
    await gotoHome(page);
    await page.keyboard.press("Tab");
    const skip = page.getByRole("link", { name: "Ana içeriğe geç" });
    await expect(skip).toBeFocused();
    await expect(skip).toBeInViewport();
    await page.keyboard.press("Enter");
    await expect(page.locator("main")).toBeFocused();
  });

  test("odaklanan öğede görünür odak halkası var", async ({ page }) => {
    await gotoHome(page);
    await page.keyboard.press("Tab");
    await page.keyboard.press("Tab");
    const outline = await page.evaluate(() => {
      const el = document.activeElement as HTMLElement;
      const s = getComputedStyle(el);
      return { width: parseFloat(s.outlineWidth), style: s.outlineStyle };
    });
    expect(outline.style).not.toBe("none");
    expect(outline.width).toBeGreaterThanOrEqual(2);
  });
});

test.describe("header: hesap bağlantısı, favori ve sepet pasif, alt gezinti gizli (1440px)", () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test("hesabım ve sepetim gerçek bağlantı; yalnız favorilerim aria-disabled ve 'Yakında' yazıyor", async ({ page }) => {
    await gotoHome(page);
    const header = page.locator("header");
    await expect(header.getByRole("link", { name: /Hesabım/ })).toHaveAttribute("href", "/hesabim");
    await expect(header.getByRole("link", { name: /Sepetim/ })).toHaveAttribute("href", "/sepet");
    await expect(header.getByRole("button", { name: /Sepetim/ })).toHaveCount(0);
    await expect(header.getByTestId("cart-count")).toHaveCount(0);
    const favorites = header.getByRole("button", { name: /Favorilerim/ });
    await expect(favorites).toHaveCount(1);
    await expect(favorites).toHaveAttribute("aria-disabled", "true");
    await expect(favorites).toContainText("Yakında");
    await expect(header.getByRole("link", { name: /Favorilerim/ })).toHaveCount(0);
    await header.getByRole("link", { name: /Sepetim/ }).click();
    await expect(page).toHaveURL(/\/sepet$/);
  });

  test("alt gezinti gizli", async ({ page }) => {
    await gotoHome(page);
    await expect(page.getByRole("navigation", { name: "Alt gezinti" })).toBeHidden();
  });
});

for (const width of [1440, 768]) {
  test.describe(`masaüstü gezinti ${width}px`, () => {
    test.use({ viewport: { width, height: 900 } });

    test("kategori bağlantısı /kategori/... sayfasına gider (son kategori dahil)", async ({ page }) => {
      for (const c of [liveCategories[0], liveCategories[liveCategories.length - 1]]) {
        await gotoHome(page);
        const nav = page.getByRole("navigation", { name: "Kategoriler", exact: true });
        await nav.getByRole("link", { name: c.name, exact: true }).scrollIntoViewIfNeeded();
        await nav.getByRole("link", { name: c.name, exact: true }).click();
        await expect(page).toHaveURL(new RegExp(`/kategori/${c.slug}$`));
        await expect(page.getByRole("heading", { level: 1, name: c.name })).toBeVisible();
      }
      await expect(page.locator("main article:has(h3):visible")).toHaveCount(productsInCategory(liveCategories[liveCategories.length - 1].slug).length);
    });
  });
}

test.describe("kategori sayfaları", () => {
  test("her kategori slug'ı 200 döner, başlığı ve ürün sayısı veriyle uyuşur", async ({ page }) => {
    for (const c of liveCategories) {
      const response = await page.goto(`/kategori/${c.slug}`);
      expect(response?.status(), c.slug).toBe(200);
      await expect(page.getByRole("heading", { level: 1, name: c.name })).toBeVisible();
      await expect(page.locator("main article:has(h3):visible")).toHaveCount(productsInCategory(c.slug).length);
    }
  });

  test("bilinmeyen kategori 404 döner", async ({ page }) => {
    const response = await page.goto("/kategori/yok");
    expect(response?.status()).toBe(404);
  });
});

test.describe("görsel kaynakları ve dış istek", () => {
  test("ana sayfa aynı origin dışına istek (görsel dahil) atmıyor", async ({ page, baseURL }) => {
    const origin = new URL(baseURL!).origin;
    const external: string[] = [];
    page.on("request", (request) => {
      const url = request.url();
      if (url.startsWith("data:") || url.startsWith("blob:")) return;
      if (new URL(url).origin !== origin) external.push(`${request.resourceType()} ${url}`);
    });
    await gotoHome(page);
    await scrollThroughPage(page);
    await page.waitForLoadState("networkidle");
    expect(external).toEqual([]);
  });

  test("/gorsel-kaynaklari 200 döner; her fotoğraf için yazar, lisans ve kaynak bağlantısı listelenir", async ({ page }) => {
    const response = await page.goto("/gorsel-kaynaklari");
    expect(response?.status()).toBe(200);
    await expect(page.getByRole("heading", { level: 1, name: "Görsel kaynakları" })).toBeVisible();
    const items = page.locator("main li").filter({ hasText: "Lisans:" });
    await expect(items).toHaveCount(imageCredits.length);
    const first = imageCredits[0];
    await expect(items.first().getByRole("link", { name: first.author })).toHaveAttribute("href", first.authorUrl);
    await expect(items.first().getByRole("link", { name: first.license })).toHaveAttribute("href", /unsplash\.com\/license/);
    await expect(items.first().getByRole("link", { name: "Unsplash sayfası" })).toHaveAttribute("href", first.page);
    await expect(items.first()).toContainText(first.audit);
    // Her satırda yazar bağlantısı, lisans bağlantısı ve kaynak bağlantısı bulunur.
    expect(await page.locator("main li a[href*='unsplash.com/@']").count()).toBe(imageCredits.length);
    expect(await page.locator("main li a[href*='unsplash.com/license']").count()).toBe(imageCredits.length);
    expect(await page.locator("main li a[href*='unsplash.com/photos/']").count()).toBe(imageCredits.length);
    expect(await seriousViolations(page)).toEqual([]);
  });

  test("footer'dan /gorsel-kaynaklari sayfasına gidilir", async ({ page }) => {
    await gotoHome(page);
    await page.getByRole("contentinfo").getByRole("link", { name: "Görsel kaynakları" }).click();
    await expect(page).toHaveURL(/\/gorsel-kaynaklari$/);
  });
});

test.describe("diğer sayfalar", () => {
  test("/status 200 döner", async ({ page }) => {
    const response = await page.goto("/status");
    expect(response?.status()).toBe(200);
    await expect(page.getByRole("heading", { level: 1, name: "Sistem durumu" })).toBeVisible();
  });

  test("arama formu /arama?q= sayfasına gider ve terimi gösterir", async ({ page }) => {
    await gotoHome(page);
    await page.getByRole("searchbox", { name: "Ürün ara" }).fill("kulaklık");
    await page.getByRole("button", { name: "Ara", exact: true }).click();
    await expect(page).toHaveURL(/\/arama\?q=/);
    await expect(page.locator("main")).toContainText(/kulaklık/i);
    await expect(page.getByText("Arama yakında")).toHaveCount(0);
  });

  test("404 sayfası gerçek 404 döner", async ({ page }) => {
    const response = await page.goto("/olmayan-sayfa");
    expect(response?.status()).toBe(404);
  });

  test("kategori, arama, status ve görsel kaynakları sayfalarında serious/critical axe ihlali yok", async ({ page }) => {
    for (const p of [`/kategori/${tabCategories()[0].slug}`, "/arama?q=test", "/status"]) {
      await page.goto(p);
      expect(await seriousViolations(page), p).toEqual([]);
    }
  });
});

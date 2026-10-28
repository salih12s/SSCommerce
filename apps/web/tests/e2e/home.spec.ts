import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

const viewports = [
  { name: "1440", width: 1440, height: 900 },
  { name: "768", width: 768, height: 1024 },
  { name: "390", width: 390, height: 844 },
];

async function seriousViolations(page: Page) {
  const results = await new AxeBuilder({ page }).analyze();
  return results.violations
    .filter((v) => v.impact === "serious" || v.impact === "critical")
    .map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`);
}

for (const vp of viewports) {
  test.describe(`ana sayfa ${vp.name}px`, () => {
    test.use({ viewport: { width: vp.width, height: vp.height } });

    test.beforeEach(async ({ page }) => {
      await page.goto("/");
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
      const visibleNavs = await page.locator("nav").evaluateAll(
        (els) => els.filter((el) => (el as HTMLElement).getClientRects().length > 0).length,
      );
      expect(visibleNavs).toBeGreaterThan(0);
      await expect(page.locator("h1")).toHaveCount(1);
    });

    test("görsellerde alt metni var", async ({ page }) => {
      const imgsWithoutAlt = await page.locator("img:not([alt])").count();
      expect(imgsWithoutAlt).toBe(0);
      const svgsWithoutLabel = await page
        .locator("svg[role='img']:not([aria-label]):not([aria-labelledby])")
        .count();
      expect(svgsWithoutLabel).toBe(0);
    });

    test("axe: serious/critical ihlal yok", async ({ page }) => {
      expect(await seriousViolations(page)).toEqual([]);
    });

    test("tam sayfa ekran görüntüsü", async ({ page }, testInfo) => {
      // test-results/ altına yazılır (Git dışı); makineye özel yol yok.
      const path = testInfo.outputPath(`home-${vp.name}.png`);
      await page.screenshot({ path, fullPage: true });
      await testInfo.attach(`home-${vp.name}`, { path, contentType: "image/png" });
    });
  });
}

test.describe("mobil menü 390px", () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test("açılıp kapanıyor, Esc ile kapanıyor", async ({ page }) => {
    await page.goto("/");
    const button = page.getByRole("button", { name: "Kategoriler" });
    await expect(button).toHaveAttribute("aria-expanded", "false");
    await expect(page.locator("#mobil-menu")).toBeHidden();

    await button.click();
    await expect(button).toHaveAttribute("aria-expanded", "true");
    const firstLink = page.locator("#mobil-menu a").first();
    await expect(firstLink).toBeVisible();
    await expect(firstLink).toBeFocused();

    await page.keyboard.press("Escape");
    await expect(button).toHaveAttribute("aria-expanded", "false");
    await expect(page.locator("#mobil-menu")).toBeHidden();
    await expect(button).toBeFocused();

    // düğmeyle de kapanır
    await button.click();
    await expect(button).toHaveAttribute("aria-expanded", "true");
    await button.click();
    await expect(button).toHaveAttribute("aria-expanded", "false");
  });

  test("menüdeki kategori bağlantısı sayfaya götürür ve menüyü kapatır", async ({ page }) => {
    await page.goto("/");
    await page.getByRole("button", { name: "Kategoriler" }).click();
    await page.locator("#mobil-menu").getByRole("link", { name: "Ses & Görüntü" }).click();
    await expect(page).toHaveURL(/\/kategori\/ses-goruntu$/);
  });
});

for (const width of [1440, 768]) {
  test.describe(`masaüstü gezinti ${width}px`, () => {
    test.use({ viewport: { width, height: 900 } });

    test("kategori bağlantısı /kategori/... sayfasına gider", async ({ page }) => {
      await page.goto("/");
      await page.getByRole("navigation", { name: "Kategoriler", exact: true }).getByRole("link", { name: "Bilgisayar & Oyun" }).click();
      await expect(page).toHaveURL(/\/kategori\/bilgisayar-oyun$/);
      await expect(page.getByRole("heading", { level: 1, name: "Bilgisayar & Oyun" })).toBeVisible();
      await expect(page.getByText("Bu kategori Faz 3'te açılacak")).toBeVisible();
    });
  });
}

test.describe("diğer sayfalar", () => {
  test("/status 200 döner", async ({ page }) => {
    const response = await page.goto("/status");
    expect(response?.status()).toBe(200);
    await expect(page.getByRole("heading", { level: 1, name: "Sistem durumu" })).toBeVisible();
  });

  test("arama formu /arama?q= sayfasına gider ve terimi gösterir", async ({ page }) => {
    await page.goto("/");
    await page.getByRole("searchbox", { name: "Ürün ara" }).fill("kulaklık");
    await page.getByRole("button", { name: "Ara" }).click();
    await expect(page).toHaveURL(/\/arama\?q=/);
    await expect(page.getByText("kulaklık", { exact: true })).toBeVisible();
    await expect(page.getByText("Arama yakında")).toBeVisible();
  });

  test("bilinmeyen kategori 404 döner", async ({ page }) => {
    const response = await page.goto("/kategori/yok");
    expect(response?.status()).toBe(404);
  });

  test("kategori ve arama sayfalarında serious/critical axe ihlali yok", async ({ page }) => {
    for (const path of ["/kategori/ses-goruntu", "/arama?q=test", "/status"]) {
      await page.goto(path);
      expect(await seriousViolations(page), path).toEqual([]);
    }
  });
});

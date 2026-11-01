import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

for (const width of [390, 768, 1440]) {
  test.describe(`catalog ${width}px`, () => {
    test.use({ viewport: { width, height: width === 390 ? 844 : 1024 } });
    test("category, filters and product detail fit the approved design", async ({ page }, info) => {
      for (const [name, route] of [
        ["category", "/kategori/ses-sistemleri?sort=price_asc"],
        ["search", "/arama?category=ses-sistemleri&sort=price_asc"],
        ["product", "/urun/ton-kulakustu-kulaklik"],
      ]) {
        const response = await page.goto(route);
        expect(response?.status()).toBe(200);
        await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
        if (name === "product") {
          await expect(page.getByRole("heading", { level: 2, name: "Seçenekler" })).toBeVisible();
        } else {
          await expect(page.locator("main article:visible")).toHaveCount(3);
        }
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        const a11y = await new AxeBuilder({ page }).analyze();
        expect(a11y.violations.filter((v) => v.impact === "serious" || v.impact === "critical")).toEqual([]);
        const file = info.outputPath(`${name}-${width}.png`);
        await page.screenshot({ path: file, fullPage: true });
        await info.attach(name, { path: file, contentType: "image/png" });
      }
    });
  });
}

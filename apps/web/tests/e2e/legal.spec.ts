import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import fs from "node:fs";
import path from "node:path";

// MD-4: kurumsal ve yasal sayfalar (örnek metin). Yalnız okur; paylaşılan veriyi değiştirmez ("main" projesi).
const NOTICE = "Bu metin bir portföy projesi için hazırlanmış örnektir; gerçek bir işletmeye ait değildir ve hukuki danışmanlık yerine geçmez.";
const pages = [
  { path: "/hakkimizda", link: "Hakkımızda", h1: "Hakkımızda", toc: false },
  { path: "/iletisim", link: "İletişim", h1: "İletişim", toc: false },
  { path: "/gizlilik-kvkk", link: "Gizlilik ve KVKK", h1: "Gizlilik ve KVKK aydınlatma metni", toc: true },
  { path: "/mesafeli-satis", link: "Mesafeli satış sözleşmesi", h1: "Mesafeli satış sözleşmesi", toc: true },
  { path: "/cerez-politikasi", link: "Çerez politikası", h1: "Çerez politikası", toc: false },
] as const;

async function axeViolations(page: Page) {
  const results = await new AxeBuilder({ page }).analyze();
  return results.violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(" ")).join(" | ")}`);
}
const noOverflow = (page: Page) =>
  page.evaluate(() => ({ scrollWidth: document.documentElement.scrollWidth, innerWidth: window.innerWidth }));

test.describe("alt bilgiden gezinti", () => {
  test("Kurumsal sütunu beş gerçek bağlantı; 'Yakında' rozeti hiçbir yerde yok", async ({ page }) => {
    await page.goto("/");
    const footer = page.locator("footer");
    await expect(footer).toBeVisible();
    const corporate = footer.getByRole("navigation", { name: "Kurumsal" });
    await expect(corporate.getByRole("link")).toHaveText(pages.map((p) => p.link));
    await expect(footer.getByText("Yakında")).toHaveCount(0);
  });

  for (const p of pages) {
    test(`${p.link}: footer bağlantısı sayfayı açar; tek h1, örnek uyarısı ve güncelleme tarihi var`, async ({ page }) => {
      await page.goto("/");
      await page.locator("footer").getByRole("navigation", { name: "Kurumsal" }).getByRole("link", { name: p.link, exact: true }).click();
      await expect(page).toHaveURL(new RegExp(`${p.path}$`));
      // Görünür h1: istemci gezintisinde önceki sayfa gizli olarak DOM’da kalabilir, o yüzden rol sorgusu (gizlileri saymaz).
      await expect(page.getByRole("heading", { level: 1 })).toHaveCount(1);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(p.h1);
      await expect(page.getByRole("note", { name: "Örnek metin uyarısı" })).toContainText(NOTICE);
      await expect(page.locator("main")).toContainText("Son güncelleme: 8 Kasım 2026");
      await expect(page.locator("main time")).toHaveAttribute("datetime", "2026-11-08");
      await expect(page.locator("footer:visible")).toHaveCount(1);
    });
  }
});

test.describe("her sayfa", () => {
  for (const p of pages) {
    test(`${p.path}: başlık, açıklama ve canonical`, async ({ page }) => {
      const response = await page.goto(p.path);
      expect(response?.status()).toBe(200);
      await expect(page).toHaveTitle(/ \| SSCommerce$/);
      expect((await page.locator('meta[name="description"]').getAttribute("content"))?.length ?? 0).toBeGreaterThan(40);
      await expect(page.locator('link[rel="canonical"]')).toHaveAttribute("href", new RegExp(`${p.path}$`));
    });

    test(`${p.path}: axe ihlalsiz (masaüstü) ve başlık sırası atlamıyor`, async ({ page }) => {
      await page.goto(p.path);
      await expect(page.locator("h1")).toBeVisible();
      expect(await axeViolations(page)).toEqual([]);
      const levels = await page.locator("main h1, main h2, main h3").evaluateAll((els) => els.map((el) => Number(el.tagName[1])));
      levels.forEach((level, i) => {
        if (i > 0) expect(level - levels[i - 1], `başlık sırası ${levels.join(",")}`).toBeLessThanOrEqual(1);
      });
    });
  }
});

test.describe("390px", () => {
  test.use({ viewport: { width: 390, height: 844 } });

  for (const p of pages) {
    test(`${p.path}: yatay taşma yok ve axe ihlalsiz`, async ({ page }) => {
      await page.goto(p.path);
      await expect(page.locator("h1")).toBeVisible();
      const o = await noOverflow(page);
      expect(o.scrollWidth).toBeLessThanOrEqual(o.innerWidth);
      expect(await axeViolations(page)).toEqual([]);
      // Hiçbir içerik öğesi görünür alanın sağına taşmaz (tablo, uzun e-posta/tanımlayıcı).
      const wide = await page.evaluate(() =>
        Array.from(document.querySelectorAll("main *"))
          .filter((el) => el.getBoundingClientRect().right > window.innerWidth + 1)
          .map((el) => `${el.tagName}.${(el as HTMLElement).className}`.slice(0, 80)),
      );
      expect(wide).toEqual([]);
    });
  }

  test("çerez tablosu mobilde kart olarak yığılır ve sütun adları her hücrede görünür", async ({ page }) => {
    await page.goto("/cerez-politikasi");
    const table = page.getByRole("table", { name: "Zorunlu çerezler" });
    await expect(table).toBeVisible();
    await expect(table.getByRole("row")).toHaveCount(2);
    await expect(table.getByRole("cell").first()).toContainText("Ad");
    await expect(table.getByRole("cell").first()).toContainText("__Host-ssc.auth");
  });
});

test.describe("içindekiler", () => {
  for (const p of pages.filter((x) => x.toc)) {
    test(`${p.path}: içindekiler tüm bölümlere gider`, async ({ page }) => {
      await page.goto(p.path);
      const toc = page.getByRole("navigation", { name: "İçindekiler" });
      await expect(toc).toBeVisible();
      const links = toc.getByRole("link");
      const sectionCount = await page.locator("main section").count();
      await expect(links).toHaveCount(sectionCount);
      expect(sectionCount).toBeGreaterThanOrEqual(6);

      const last = links.last();
      const targetId = (await last.getAttribute("href"))!.slice(1);
      await last.click();
      await expect(page).toHaveURL(new RegExp(`#${targetId}$`));
      const heading = page.locator(`#${targetId}`);
      await expect(heading).toBeInViewport();
      const top = await heading.evaluate((el) => el.getBoundingClientRect().top);
      expect(top).toBeGreaterThanOrEqual(0);
    });
  }

  test("içindekisi olmayan sayfalarda içindekiler yok", async ({ page }) => {
    for (const p of pages.filter((x) => !x.toc)) {
      await page.goto(p.path);
      await expect(page.locator("h1")).toBeVisible();
      await expect(page.getByRole("navigation", { name: "İçindekiler" })).toHaveCount(0);
    }
  });
});

test.describe("içerik doğruluğu", () => {
  test("iletişim: yalnız açıkça kurgusal bilgiler; form ve giriş alanı yok", async ({ page }) => {
    await page.goto("/iletisim");
    const main = page.locator("main");
    await expect(main).toContainText("destek@sscommerce.example");
    await expect(main).toContainText("0850 000 00 00");
    await expect(main).toContainText("Örnek Mah. Demo Sok. No:1, İstanbul");
    await expect(main).toContainText("kurgusal");
    await expect(main.locator("form, input, textarea, select, button")).toHaveCount(0);
  });

  test("gizlilik/KVKK: veri grupları, m.11 hakları; ödeme verisi ve izleyici yok denir", async ({ page }) => {
    await page.goto("/gizlilik-kvkk");
    const table = page.getByRole("table", { name: "İşlenen kişisel veri grupları" });
    for (const group of ["Hesap", "Teslimat adresleri", "Sepet", "Favoriler", "Siparişler", "Güvenlik kayıtları"]) {
      await expect(table.getByRole("cell", { name: group, exact: true })).toBeVisible();
    }
    await expect(page.locator("main")).toContainText("KVKK m.11");
    await expect(page.locator("main")).toContainText("Ödeme verisi");
    await expect(page.locator("main")).toContainText("Verileriniz üçüncü kişilere aktarılmaz");
    await expect(page.getByRole("listitem").filter({ hasText: "Kişisel verinizin işlenip işlenmediğini öğrenmek" })).toBeVisible();
  });

  test("mesafeli satış: demo uyarısı, 14 gün cayma ve istisnalar", async ({ page }) => {
    await page.goto("/mesafeli-satis");
    const main = page.locator("main");
    await expect(main).toContainText("ödeme alınmaz, kargo hesaplanmaz");
    await expect(main).toContainText("demo kaydıdır");
    await expect(main).toContainText("14 gün");
    await expect(page.getByRole("heading", { name: /Cayma hakkı/ })).toBeVisible();
  });

  test("çerez politikası: kodun gerçekten kullandığı çerez adları ve depo anahtarları sayfada yazar", async ({ page }) => {
    // Sapma koruması: ad kaynak dosyalarda değişirse bu test düşer ve metin güncellenir.
    const web = process.cwd();
    const root = path.resolve(web, "../..");
    const cs = fs.readFileSync(path.join(root, "backend/src/SSCommerce.Api/Security/AuthCookieSettings.cs"), "utf8");
    const cookieNames = Array.from(cs.matchAll(/CookieName = "([^"]+)"/g), (m) => m[1]);
    expect(cookieNames.sort()).toEqual(["__Host-ssc.auth", "__Host-ssc.csrf", "ssc.auth", "ssc.csrf"]);
    const keys = [
      fs.readFileSync(path.join(web, "src/features/checkout/order-key.ts"), "utf8"),
      fs.readFileSync(path.join(web, "src/lib/stock-intent.ts"), "utf8"),
    ].map((src) => /STORAGE_KEY = "([^"]+)"/.exec(src)![1]);
    expect(keys).toEqual(["ssc.order-intent", "ssc.stock-intent"]);

    await page.goto("/cerez-politikasi");
    const main = page.locator("main");
    for (const name of [...cookieNames, ...keys]) await expect(main).toContainText(name);
    await expect(main).toContainText("HttpOnly");
    await expect(main).toContainText("Analitik");
    await expect(main).toContainText("Neden çerez onay bandı yok?");
    // Gerçekten onay bandı yok.
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect(page.getByRole("button", { name: /kabul|onayla|reddet/i })).toHaveCount(0);
  });
});

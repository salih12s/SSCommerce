import { expect, test } from "@playwright/test";
import type { Paged, ProductSummary } from "../../src/features/admin/types";
import { actor, aGet, settled, type Actor } from "./admin-support";
import { axeSerious, noHorizontalOverflow, viewports } from "./support";

// Faz 8 duyarlılık + erişilebilirlik: yönetim sayfaları 1440/768/390 genişlikte axe (ciddi/kritik) ihlalsiz ve yatay kaydırmasız.
// Salt okur (kendi hesaplarını kurar, katalog/stok yazmaz): "main" projesinde paralel koşar. Dolu veri: demo katalog ve fixture stoğu.
let admin: Actor;
let wh: Actor;
let productId = "";
test.beforeAll(async ({ browser, baseURL }) => {
  admin = await actor(browser, baseURL!, "Admin");
  wh = await actor(browser, baseURL!, "Warehouse");
  productId = (await aGet<Paged<ProductSummary>>(admin.request, "/products?pageSize=1")).items[0].id;
});
test.afterAll(async () => { await admin?.context.close(); await wh?.context.close(); });

const PAGES = [
  ["/admin", "Özet"], ["/admin/urunler", "Ürünler"], ["/admin/urunler/yeni", "Yeni ürün"], ["/admin/kategoriler", "Kategoriler"], ["/admin/stok", "Stok"],
  ["/admin/depolar", "Depolar"], ["/admin/siparisler", "Siparişler"], ["/admin/raporlar", "Raporlar"], ["/admin/kullanicilar", "Kullanıcılar"],
] as const;

for (const viewport of viewports) {
  test.describe(`${viewport.width}px`, () => {
    test.use({ viewport });

    test("Admin: tüm yönetim sayfaları axe ihlalsiz, yatay kaydırma yok, klavyeyle gezinilir", async () => {
      await admin.page.setViewportSize(viewport);
      for (const [path, title] of [...PAGES, [`/admin/urunler/${productId}`, null] as const]) {
        await admin.page.goto(path);
        await settled(admin.page);
        if (title) await expect(admin.page.getByRole("heading", { level: 1 }).first()).toContainText(title);
        expect(await axeSerious(admin.page), `${path} axe`).toEqual([]);
        expect(await noHorizontalOverflow(admin.page), `${path} yatay taşma`).toBe(true);
      }
      // Dolu durum: ürün listesi ve stok araması satırlarla
      await admin.page.goto("/admin/urunler");
      await settled(admin.page);
      await expect(admin.page.getByRole("table", { name: "Ürünler" }).getByRole("row").nth(1)).toBeVisible();
      // Klavye: ilk Tab "içeriğe geç" ya da menü bağlantısına düşer; menü bağlantıları odak halkasını gösterir
      await admin.page.keyboard.press("Tab");
      const focused = await admin.page.evaluate(() => document.activeElement?.tagName);
      expect(["A", "BUTTON"]).toContain(focused);
      const nav = admin.page.getByRole("navigation", { name: "Yönetim bölümleri" });
      await nav.getByRole("link", { name: "Kategoriler", exact: true }).focus();
      await admin.page.keyboard.press("Enter");
      await expect(admin.page).toHaveURL(/\/admin\/kategoriler$/);
      // Dar ekranda sekme şeridi kendi içinde kayar, sayfa kaymaz
      expect(await noHorizontalOverflow(admin.page)).toBe(true);
    });

    test("Depo: sipariş listesi axe ihlalsiz ve taşmasız; yetkisiz ekran da erişilebilir", async ({ browser, baseURL }) => {
      await wh.page.setViewportSize(viewport);
      await wh.page.goto("/admin/siparisler");
      await settled(wh.page);
      expect(await axeSerious(wh.page), "depo sipariş listesi").toEqual([]);
      expect(await noHorizontalOverflow(wh.page)).toBe(true);
      // Customer: HTTP 403 sayfası (proxy'nin kendi HTML'i)
      const customer = await actor(browser, baseURL!, null);
      try {
        await customer.page.setViewportSize(viewport);
        const response = await customer.page.goto("/admin");
        expect(response?.status()).toBe(403);
        await expect(customer.page.getByRole("heading", { name: "Bu alana erişimin yok" })).toBeVisible();
        expect(await axeSerious(customer.page), "403 sayfası").toEqual([]);
        expect(await noHorizontalOverflow(customer.page)).toBe(true);
      } finally { await customer.context.close(); }
    });
  });
}

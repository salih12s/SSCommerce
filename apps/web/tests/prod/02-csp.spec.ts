import type { Page } from "@playwright/test";
import { addAddress, addToCart, credentialsPause, customer, expect, newCtx, placeOrder, ready, test, variantRow, viewports, watch, type Watcher } from "./support";

// P2: CSP / hidrasyon. Üç genişlikte tüm sayfalarda sıfır securitypolicyviolation ve sıfır konsol/sayfa hatası; başlıklar; hidrasyon etkileşimleri.
// Hatalar test sonunda toplu raporlanır (hangi sayfa/genişlik).

async function visit(page: Page, watcher: Watcher, issues: string[], path: string, status = 200) {
  const label = `${page.viewportSize()?.width}px ${path}`;
  const response = await page.goto(path);
  if (response?.status() !== status) issues.push(`${label}: durum ${response?.status()} (beklenen ${status})`);
  const policy = response?.headers()["content-security-policy"] ?? "";
  const script = /script-src ([^;]*)/.exec(policy)?.[1] ?? "";
  if (!script.includes("'nonce-")) issues.push(`${label}: script-src nonce'suz`);
  if (/unsafe-inline|unsafe-eval/.test(script)) issues.push(`${label}: script-src unsafe-* içeriyor`);
  await ready(page);
  flush(page, watcher, issues, label);
}
function flush(page: Page, watcher: Watcher, issues: string[], label: string) {
  for (const v of watcher.violations.splice(0)) issues.push(`${label}: CSP ihlali ${v}`);
  for (const p of watcher.problems.splice(0)) issues.push(`${label}: ${p}`);
  void page;
}

test("P2 başlıklar: HTML yanıtında HSTS, CSP (script-src unsafe-* yok), nosniff, frame-ancestors 'none'", async ({ browser }, testInfo) => {
  const context = await newCtx(browser);
  for (const path of ["/", "/giris", "/olmayan-sayfa-prod"]) {
    const response = await context.request.get(path);
    const h = response.headers();
    const policy = h["content-security-policy"] ?? "";
    const script = /script-src ([^;]*)/.exec(policy)?.[1] ?? "";
    expect(h["strict-transport-security"], `${path} HSTS`).toMatch(/max-age=\d+/);
    expect(script, `${path} script-src`).toContain("'self'");
    expect(script, `${path} script-src`).not.toContain("unsafe-inline");
    expect(script, `${path} script-src`).not.toContain("unsafe-eval");
    expect(policy, `${path} frame-ancestors`).toContain("frame-ancestors 'none'");
    expect(policy, `${path} default-src`).toContain("default-src 'self'");
    expect(h["x-content-type-options"], `${path} nosniff`).toBe("nosniff");
    expect(h["x-frame-options"], `${path} XFO`).toBe("DENY");
  }
  const sts = (await context.request.get("/")).headers()["strict-transport-security"];
  testInfo.annotations.push({ type: "HSTS", description: sts ?? "yok" }); // max-age üretimde uzatılmalı mı: sahibin kararı
  await context.close();
});

for (const viewport of viewports) {
  test.describe(`P2 ${viewport.width}px`, () => {
    test("anonim vitrin sayfaları temiz; hidrasyon (menü, form) çalışır", async ({ browser, sellable }) => {
      const context = await newCtx(browser, { viewport });
      const page = await context.newPage();
      const watcher = await watch(page);
      const issues: string[] = [];
      const categories = await (await context.request.get("/api/backend/api/v1/categories")).json() as { slug: string }[];
      await visit(page, watcher, issues, "/");
      if (viewport.width < 768) {
        const toggle = page.getByRole("button", { name: "Kategoriler" });
        await expect(toggle).toBeVisible();
        await toggle.click();
        await expect(toggle).toHaveAttribute("aria-expanded", "true");
        await page.keyboard.press("Escape");
      }
      for (const path of [`/kategori/${categories[0].slug}`, `/urun/${sellable.slug}`, "/arama", "/giris", "/kayit", "/sepet", "/checkout"]) await visit(page, watcher, issues, path);
      await visit(page, watcher, issues, "/olmayan-sayfa-prod", 404);

      // Form etkileşimi: gönder düğmesi yalnız hidrasyondan sonra etkin; yanlış parola hata gösterir (istemci kodu çalışıyor).
      await page.goto("/giris");
      await expect(page.getByRole("button", { name: "Giriş yap", exact: true })).toBeEnabled();
      await page.getByLabel("E-posta", { exact: true }).fill("kimse-yok@example.test");
      await page.getByLabel("Parola", { exact: true }).fill("Yanlis-parola-42!");
      await credentialsPause();
      await page.getByRole("button", { name: "Giriş yap", exact: true }).click();
      await expect(page.getByRole("alert")).toBeVisible();
      await ready(page);
      flush(page, watcher, issues, `${viewport.width}px giriş formu`);
      expect(issues).toEqual([]);
      await context.close();
    });

    test("müşteri sayfaları temiz; sepete ekle hidrasyonu çalışır", async ({ browser, sellable }) => {
      const actor = await customer(browser, { viewport });
      const page = await actor.context.newPage();
      const watcher = await watch(page);
      const issues: string[] = [];
      await visit(page, watcher, issues, "/hesabim");
      await page.goto(`/urun/${sellable.slug}`);
      await ready(page);
      await variantRow(page, sellable.variantId).getByRole("radio").check();
      await page.getByRole("button", { name: "Sepete ekle" }).click();
      await expect(page.getByRole("status").filter({ hasText: "Sepete eklendi" })).toBeVisible();
      await expect(page.locator("header").getByTestId("cart-count")).toBeVisible();
      flush(page, watcher, issues, `${viewport.width}px sepete ekle`);
      await addAddress(actor.context.request);
      for (const path of ["/sepet", "/hesabim/adresler", "/siparisler", "/checkout"]) await visit(page, watcher, issues, path);
      await expect(page.getByTestId("preview")).toBeVisible();
      flush(page, watcher, issues, `${viewport.width}px checkout önizleme`);
      expect(issues).toEqual([]);
      await actor.context.close();
    });

    test("Admin: tüm /admin sayfaları temiz; müşteri 403 sayfası temiz", async ({ browser, admin, sellable }) => {
      const page = await admin.context.newPage();
      await page.setViewportSize(viewport);
      const watcher = await watch(page);
      const issues: string[] = [];
      // Dinamik sayfalar için gerçek kimlikler: bir ürün ve bir sipariş.
      const products = await (await admin.context.request.get("/api/backend/api/v1/admin/products?pageSize=1")).json() as { items: { id: string }[] };
      const buyer = await customer(browser);
      await addToCart(buyer.context.request, sellable.variantId);
      const { id: addressId } = await addAddress(buyer.context.request);
      const placed = await placeOrder(buyer.context.request, addressId);
      await buyer.context.close();
      const routes = ["/admin", "/admin/urunler", "/admin/urunler/yeni", `/admin/urunler/${products.items[0].id}`, "/admin/kategoriler", "/admin/stok", "/admin/depolar", "/admin/siparisler", `/admin/siparisler/${placed.order.id}`, "/admin/raporlar", "/admin/kullanicilar"];
      for (const route of routes) {
        await visit(page, watcher, issues, route);
        if (!(await page.locator("main h1").first().isVisible())) issues.push(`${viewport.width}px ${route}: h1 görünmüyor`);
      }
      // Hidrasyon: yönetim gezintisi istemci tarafında çalışır.
      await page.goto("/admin");
      await ready(page);
      await page.getByRole("link", { name: "Siparişler" }).first().click();
      await expect(page).toHaveURL(/\/admin\/siparisler/);
      await ready(page);
      flush(page, watcher, issues, `${viewport.width}px admin gezinti`);

      const plain = await customer(browser, { viewport });
      const plainPage = await plain.context.newPage();
      const plainWatcher = await watch(plainPage);
      await visit(plainPage, plainWatcher, issues, "/admin", 403);
      await expect(plainPage.getByRole("heading", { name: "Bu alana erişimin yok" })).toBeVisible();
      await plain.context.close();
      await page.close();
      expect(issues).toEqual([]);
    });
  });
}

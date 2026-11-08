import type { Page } from "@playwright/test";
import { BASE, expect, newCtx, test } from "./support";

// P6: sağlık / metrik / openapi yolları istemciden çağrılmaz ve kenarda (Caddy) 404 verir.
const FORBIDDEN = /\/(api\/backend\/health|api\/backend\/metrics|api\/backend\/openapi|metrics|openapi)(\/|$|\?|\.)/;

test("P6 tarayıcı ağ günlüğünde health/metrics/openapi çağrısı yok", async ({ browser, admin, sellable }) => {
  const seen: string[] = [];
  const watchPage = (page: Page) => page.on("request", (r) => { const path = new URL(r.url()).pathname; seen.push(path); });

  const anon = await newCtx(browser);
  const anonPage = await anon.newPage();
  watchPage(anonPage);
  for (const path of ["/", "/arama", `/urun/${sellable.slug}`, "/sepet", "/giris", "/kayit", "/checkout", "/olmayan-sayfa-sizinti"]) {
    await anonPage.goto(path);
    await anonPage.waitForLoadState("networkidle");
  }
  await anon.close();

  const adminPage = await admin.context.newPage();
  watchPage(adminPage);
  for (const path of ["/admin", "/admin/urunler", "/admin/stok", "/admin/siparisler", "/admin/raporlar", "/hesabim"]) {
    await adminPage.goto(path);
    await adminPage.waitForLoadState("networkidle");
  }
  await adminPage.close();

  expect(seen.length, "istek yakalandı").toBeGreaterThan(20);
  expect(seen.filter((p) => FORBIDDEN.test(p)), "istemciden çağrılan yasaklı yollar").toEqual([]);
});

test("P6 kenar bu yollara 404 verir", async ({ playwright }) => {
  const request = await playwright.request.newContext({ baseURL: BASE, ignoreHTTPSErrors: true });
  const paths = ["/api/backend/health", "/api/backend/health/ready", "/api/backend/health/live", "/api/backend/metrics", "/api/backend/openapi/v1.json", "/api/backend/openapi", "/openapi", "/openapi/v1.json", "/metrics"];
  const results: string[] = [];
  for (const path of paths) {
    const response = await request.get(path);
    if (response.status() !== 404) results.push(`${path} -> ${response.status()}`);
  }
  await request.dispose();
  expect(results, "404 olmayan yollar").toEqual([]);
});

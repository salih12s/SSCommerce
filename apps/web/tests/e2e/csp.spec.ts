import { expect, test, type Page } from "@playwright/test";
import { actor } from "./admin-support";
import { addressApi, loadVariants, newEmail, password, variantRow, withStock } from "./support";

// Faz 9 / F4: üretim sunucusu (server.mjs) sıkı bir CSP verir. Bu dosya gerçek tarayıcıda kanıtlar ki ana sayfa, katalog, sepet, hesap, checkout ve yönetim
// sayfalarında SIFIR securitypolicyviolation + SIFIR konsol/sayfa hatası vardır ve hidrasyon (etkileşim) çalışır.
// Tek amaç ihlal yakalamak olduğundan veri değiştirmez: yalnız kendi yeni kullanıcılarının sepet/adres verisine yazar (main projesi).

type Report = { directive: string; blocked: string; sample: string; source: string };
type Watcher = Awaited<ReturnType<typeof watch>>;

async function watch(page: Page) {
  const violations: string[] = [];
  const problems: string[] = [];
  await page.exposeFunction("__reportCsp", (report: Report) => { violations.push(`${report.directive} blocked=${report.blocked} sample=${report.sample.slice(0, 80)} at ${report.source}`); });
  await page.addInitScript(() => {
    document.addEventListener("securitypolicyviolation", (event) => {
      (window as unknown as { __reportCsp: (r: Report) => void }).__reportCsp({ directive: event.violatedDirective, blocked: event.blockedURI, sample: event.sample, source: `${event.sourceFile}:${event.lineNumber}` });
    });
  });
  // Beklenen 401/403/404 yanıtları (anonim /auth/me ve /cart yoklaması, 403 yönetim sayfası, 404 sayfası) tarayıcıda "Failed to load resource" konsol hatası üretir; bu CSP ihlali değildir.
  // Geri kalan her konsol hatası (CSP ihlali iletisi, hidrasyon uyuşmazlığı, betik hatası) başarısızlıktır.
  page.on("console", (message) => {
    if (message.type() === "error" && !/^Failed to load resource: the server responded with a status of (401|403|404)/.test(message.text())) problems.push(`console: ${message.text()}`);
  });
  page.on("pageerror", (error) => problems.push(`pageerror: ${error.message}`));
  return {
    violations,
    problems,
    /** Sayfa oturduktan sonra: ihlal ve hata yok. */
    async expectClean(label: string) {
      await page.waitForLoadState("networkidle");
      expect(violations, `${label}: CSP ihlalleri`).toEqual([]);
      expect(problems, `${label}: konsol/sayfa hataları`).toEqual([]);
    },
  };
}

/** Sayfaya git, belge başlığındaki politikayı doğrula, temiz oturmasını bekle. */
async function visit(page: Page, watcher: Watcher, path: string, status = 200) {
  const response = await page.goto(path);
  expect(response?.status(), path).toBe(status);
  const policy = response?.headers()["content-security-policy"] ?? "";
  expect(policy, `${path} politika`).toContain("script-src 'self' 'nonce-");
  const script = /script-src ([^;]*)/.exec(policy)?.[1] ?? "";
  expect(script, `${path} script-src`).not.toContain("unsafe-inline");
  expect(script, `${path} script-src`).not.toContain("unsafe-eval");
  expect(policy).toContain("frame-ancestors 'none'");
  expect(response?.headers()["x-frame-options"]).toBe("DENY");
  await watcher.expectClean(path);
}

test("vitrin ve hesap sayfaları: CSP ihlali yok, hidrasyon çalışır", async ({ page, request, baseURL }) => {
  const watcher = await watch(page);
  const variants = await loadVariants(request);
  const product = withStock(variants, 12);
  await visit(page, watcher, "/");
  // Hidrasyon kanıtı: bir kontrol etkileşimi (mobil menü) ve istemci tarafı form gönderimi (aşağıda).
  await expect(page.locator("main article").first()).toBeVisible();
  const menuToggle = page.getByRole("button", { name: "Kategoriler" });
  if (await menuToggle.isVisible()) {
    await menuToggle.click();
    await expect(menuToggle).toHaveAttribute("aria-expanded", "true");
  }
  const categories = await (await request.get("/api/backend/api/v1/categories")).json() as { slug: string }[];
  await visit(page, watcher, `/kategori/${categories[0].slug}`);
  await visit(page, watcher, "/arama");
  await visit(page, watcher, `/urun/${product.productSlug}`);
  await visit(page, watcher, "/giris");

  // Kayıt formu: istemci kodu çalışmadan gönderilemez (fetch + yönlendirme).
  await visit(page, watcher, "/kayit");
  await page.getByLabel("E-posta", { exact: true }).fill(newEmail());
  await page.getByLabel("Parola", { exact: true }).fill(password);
  await page.getByRole("button", { name: "Hesap oluştur", exact: true }).click();
  await expect(page).toHaveURL(/\/hesabim$/);
  await watcher.expectClean("kayıt sonrası /hesabim");

  // Sepete ekle (hidrasyon) — giriş yapılmış kullanıcıyla.
  await page.goto(`/urun/${product.productSlug}`);
  await variantRow(page, product).getByRole("radio").check();
  await page.getByRole("button", { name: "Sepete ekle" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Sepete eklendi" })).toBeVisible();
  await expect(page.locator("header").getByTestId("cart-count")).toBeVisible();
  await watcher.expectClean("sepete ekle");

  await visit(page, watcher, "/sepet");
  await expect(page.getByTestId("cart-line")).toHaveCount(1);
  await visit(page, watcher, "/hesabim");
  await visit(page, watcher, "/hesabim/adresler");
  await visit(page, watcher, "/siparisler");
  await addressApi(page.request, baseURL!);
  await visit(page, watcher, "/checkout");
  await expect(page.getByTestId("preview")).toBeVisible();
  await watcher.expectClean("checkout önizleme");
  // 404 sayfası da aynı politikayla temiz oturur.
  await visit(page, watcher, "/olmayan-sayfa-csp", 404);
});

test("yönetim: Admin sayfaları ve müşteri 403 sayfası (satır içi <style> nonce'lu) ihlalsiz", async ({ browser, baseURL }) => {
  const admin = await actor(browser, baseURL!, "Admin");
  try {
    const watcher = await watch(admin.page);
    for (const route of ["/admin", "/admin/urunler", "/admin/urunler/yeni", "/admin/kategoriler", "/admin/stok", "/admin/depolar", "/admin/siparisler", "/admin/raporlar", "/admin/kullanicilar"]) {
      await visit(admin.page, watcher, route);
      await expect(admin.page.locator("main h1").first()).toBeVisible();
    }
    // Hidrasyon: yönetim kabuğundaki gezinti istemci tarafında çalışır.
    await admin.page.goto("/admin");
    await admin.page.getByRole("link", { name: "Ürünler" }).first().click();
    await expect(admin.page).toHaveURL(/\/admin\/urunler$/);
    await watcher.expectClean("admin gezinti");
  } finally {
    await admin.context.close();
  }

  const customer = await actor(browser, baseURL!, null);
  try {
    const watcher = await watch(customer.page);
    await visit(customer.page, watcher, "/admin", 403);
    await expect(customer.page.getByRole("heading", { name: "Bu alana erişimin yok" })).toBeVisible();
    // <style> nonce'suz olsaydı sayfa stilsiz kalırdı: damga rengi uygulanmış olmalı.
    await expect(customer.page.locator(".stamp")).toHaveCSS("color", "rgb(166, 52, 0)");
  } finally {
    await customer.context.close();
  }
});

test("güvenlik başlıkları: sayfa, statik dosya ve sağlık uçlarında; API yanıtının kendi CSP'si ezilmez", async ({ request }) => {
  for (const path of ["/", "/healthz", "/readyz", "/favicon.ico", "/olmayan-sayfa-csp"]) {
    const response = await request.get(path);
    const headers = response.headers();
    expect(headers["x-content-type-options"], path).toBe("nosniff");
    expect(headers["referrer-policy"], path).toBe("strict-origin-when-cross-origin");
    expect(headers["x-frame-options"], path).toBe("DENY");
    expect(headers["permissions-policy"], path).toBe("camera=(), microphone=(), geolocation=(), payment=()");
    expect(headers["cross-origin-opener-policy"], path).toBe("same-origin");
    expect(headers["strict-transport-security"], path).toBeUndefined();
    expect(headers["x-request-id"], path).toMatch(/^[A-Za-z0-9._-]{8,64}$/);
  }
  const ready = await request.get("/readyz");
  expect(ready.status()).toBe(200);
  expect(await ready.json()).toEqual({ status: "Healthy" });
  const live = await request.get("/healthz", { headers: { "X-Request-Id": "abc-12345678" } });
  expect(live.headers()["x-request-id"]).toBe("abc-12345678");
  expect(live.headers()["cache-control"]).toBe("no-store");
  // JSON API yanıtı HTML değil: web politikası (nonce'lu) eklenmez; API'nin kendi katı politikası olduğu gibi geçer.
  const json = await request.get("/api/backend/api/v1/products?pageSize=1");
  expect(json.headers()["content-type"]).toContain("application/json");
  expect(json.headers()["content-security-policy"]).toBe("default-src 'none'; frame-ancestors 'none'; base-uri 'none'");
});

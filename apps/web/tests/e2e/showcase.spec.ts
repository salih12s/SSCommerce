import { expect, test, type Page } from "@playwright/test";
import { axeSerious, noHorizontalOverflow } from "./support";
import { seedShowcase } from "./showcase-seed";

// VT-3 vitrin: giriş sayfasındaki demo düğmeleri. Sözleşme: docs/PLAN_CUSTOMER_EXPERIENCE.md "Vitrin paketi".
// Bu dosya "showcase" projesinde, tüm projelerden SONRA ve tek worker'da koşar: `--seed-showcase` her aktif varyanta stok girişi ekler ve 5 örnek sipariş verir,
// bu yüzden paylaşılan katalog/stok verisini okuyan başka hiçbir spec bunu görmemelidir. API'de Showcase__Enabled start-api.mjs ile açıktır.
test.describe.configure({ mode: "serial" });

test.beforeAll(async ({ request }) => {
  test.setTimeout(240_000); // dotnet başlangıcı + 5 gerçek sipariş
  // API'nin veritabanı bağlantısı canlı olsun (seed, veritabanını bağlı oturumdan bulur).
  expect(
    (await request.get("/api/backend/api/v1/products?pageSize=1")).ok(),
  ).toBeTruthy();
  await seedShowcase();
});

const demoBox = (page: Page) => page.getByTestId("demo-login");
const customerButton = (page: Page) =>
  page.getByRole("button", { name: "Demo müşteri olarak gir", exact: true });
const adminButton = (page: Page) =>
  page.getByRole("button", { name: "Demo yönetici", exact: true });
const warehouseButton = (page: Page) =>
  page.getByRole("button", { name: "Demo depocu", exact: true });

test.describe("giriş sayfasında demo düğmeleri", () => {
  test("üç düğme ve açıklama görünür; dokunma hedefi 44px ve üstü", async ({
    page,
  }) => {
    await page.goto("/giris");
    await expect(demoBox(page)).toBeVisible();
    await expect(
      demoBox(page).getByRole("heading", {
        level: 2,
        name: "Hesap açmadan dene",
      }),
    ).toBeVisible();
    await expect(
      demoBox(page).getByText(
        "Vitrin hesapları ortaktır; örnek veriyle doludur.",
      ),
    ).toBeVisible();
    for (const button of [
      customerButton(page),
      adminButton(page),
      warehouseButton(page),
    ]) {
      await expect(button).toBeVisible();
      await expect(button).toBeEnabled();
      expect((await button.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    }
    // Normal giriş formu yerinde ve düğmelerden önce gelir (tek kolon).
    await expect(
      page.getByRole("button", { name: "Giriş yap", exact: true }),
    ).toBeVisible();
    expect(await noHorizontalOverflow(page)).toBe(true);
  });

  test("kayıt sayfasında görünmez", async ({ page }) => {
    await page.goto("/kayit");
    await expect(
      page.getByRole("button", { name: "Hesap oluştur", exact: true }),
    ).toBeVisible();
    await expect(demoBox(page)).toHaveCount(0);
  });

  test("360px genişlikte taşma yok ve ciddi erişilebilirlik ihlali yok", async ({
    page,
  }) => {
    await page.setViewportSize({ width: 360, height: 800 });
    await page.goto("/giris");
    await expect(demoBox(page)).toBeVisible();
    expect(await noHorizontalOverflow(page)).toBe(true);
    expect(await axeSerious(page)).toEqual([]);
  });

  test("klavyeyle odaklanır; odak halkası görünür", async ({ page }) => {
    await page.goto("/giris");
    await customerButton(page).focus();
    await expect(customerButton(page)).toBeFocused();
    expect(
      await customerButton(page).evaluate(
        (el) => getComputedStyle(el).outlineStyle,
      ),
    ).not.toBe("none");
  });

  test("GET /auth/demo 404 ise hiçbir şey görünmez ve hata gösterilmez", async ({
    page,
  }) => {
    await page.route("**/api/backend/api/v1/auth/demo", (route) =>
      route.request().method() === "GET"
        ? route.fulfill({
            status: 404,
            contentType: "application/problem+json",
            body: "{}",
          })
        : route.continue(),
    );
    await page.goto("/giris");
    await expect(
      page.getByRole("button", { name: "Giriş yap", exact: true }),
    ).toBeEnabled();
    await expect(demoBox(page)).toHaveCount(0);
    // main içinde aranır: Next.js'in boş rota duyurucusu (body sonunda role="alert") sayılmasın (cart.spec ile aynı kalıp).
    await expect(page.locator("main").getByRole("alert")).toHaveCount(0);
  });

  test("409 showcase_not_seeded anlaşılır iletiyle gösterilir; düğmeler yeniden açılır", async ({
    page,
  }) => {
    await page.route("**/api/backend/api/v1/auth/demo", (route) =>
      route.request().method() === "POST"
        ? route.fulfill({
            status: 409,
            contentType: "application/problem+json",
            body: JSON.stringify({ title: "x", code: "showcase_not_seeded" }),
          })
        : route.continue(),
    );
    await page.goto("/giris");
    await adminButton(page).click();
    await expect(page.locator("main").getByRole("alert")).toContainText(
      "Vitrin hesapları henüz hazırlanmamış",
    );
    await expect(page).toHaveURL(/\/giris$/);
    await expect(adminButton(page)).toBeEnabled();
  });

  test("429 hız sınırı anlaşılır iletiyle gösterilir", async ({ page }) => {
    await page.route("**/api/backend/api/v1/auth/demo", (route) =>
      route.request().method() === "POST"
        ? route.fulfill({
            status: 429,
            contentType: "application/problem+json",
            body: JSON.stringify({ title: "x", code: "rate_limited" }),
          })
        : route.continue(),
    );
    await page.goto("/giris");
    await warehouseButton(page).click();
    await expect(page.locator("main").getByRole("alert")).toContainText(
      "Çok fazla istek",
    );
  });
});

test.describe("demo ile giriş (gerçek vitrin verisi)", () => {
  test("demo müşteri: hesabıma gider, siparişlerimde 5 sipariş görünür", async ({
    page,
  }) => {
    await page.goto("/giris");
    await customerButton(page).click();
    await expect(page).toHaveURL(/\/hesabim$/);
    await page.goto("/siparisler");
    await expect(page.getByTestId("orders-list")).toBeVisible();
    await expect(page.getByTestId("order-row")).toHaveCount(5);
    // Durum dağılımı: teslim edildi, kargoda, hazırlanıyor, alındı, iptal (VT-2 sözleşmesi) — beş durum birbirinden farklıdır.
    const statuses = await page
      .getByTestId("order-row")
      .evaluateAll((rows) => rows.map((r) => r.getAttribute("data-status")));
    expect(new Set(statuses).size).toBe(5);
  });

  test("demo yönetici: yönetim paneli açılır", async ({ page }) => {
    await page.goto("/giris");
    await adminButton(page).click();
    await expect(page).toHaveURL(/\/admin$/);
    await expect(
      page.getByRole("heading", { level: 1, name: "Özet" }),
    ).toBeVisible();
    await expect(
      page.getByText("Yönetici", { exact: true }).first(),
    ).toBeVisible();
  });

  test("demo depocu: yalnız sipariş ekranları açılır", async ({ page }) => {
    await page.goto("/giris");
    await warehouseButton(page).click();
    await expect(page).toHaveURL(/\/admin\/siparisler$/);
    await expect(page.getByText("Depo", { exact: true }).first()).toBeVisible();
  });

  test("açık next adresi varsa demo müşteri oraya döner", async ({ page }) => {
    await page.goto("/giris?next=%2Fsiparisler");
    await customerButton(page).click();
    await expect(page).toHaveURL(/\/siparisler$/);
  });
});

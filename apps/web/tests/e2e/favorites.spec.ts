import { expect, test, type Page } from "@playwright/test";
import { api, axeSerious, newUserContext, noHorizontalOverflow, password, registerApi } from "./support";

// MD-1 Favoriler. Bu dosya yalnızca KENDİ kullanıcılarının favorilerini değiştirir; katalog fiyat/stok/aktiflik fixture'ına dokunmaz,
// bu yüzden "main" projesinde paralel koşar. Sözleşme: docs/PLAN_CUSTOMER_EXPERIENCE.md "MD-1".
// "Satışta değil" satırı ürün seviyesinde pasife alma fixture'ı ister (mevcut fixture yalnız varyant SKU'su kapatır): bu dosyada yok.
const heart = (page: Page) => page.getByTestId("favorite-button");
const headerCount = (page: Page) => page.locator("header").getByTestId("favorites-count");
const rows = (page: Page) => page.getByTestId("favorite-row");
const idsOf = async (page: Page) => ((await (await page.request.get(`${api}/favorites/ids`)).json()) as { productIds: string[] }).productIds;
const nameOf = async (button: ReturnType<typeof heart>) => ((await button.first().getAttribute("aria-label")) ?? "").replace(/ favorilerde$/, "");

test.describe("masaüstü: giriş yapmış kullanıcı", () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test("kalbe bas → sayaç 1 → /favorilerim'de görünür → kaldır → boş durum; sunucuda da kalıcı", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await page.goto("/");
    const first = heart(page).first();
    const name = await nameOf(heart(page));
    expect(name.length).toBeGreaterThan(0);
    await expect(first).toHaveAttribute("aria-pressed", "false");
    await expect(headerCount(page)).toHaveCount(0);

    await first.click();
    await expect(first).toHaveAttribute("aria-pressed", "true");
    // Ad sabit kalır; durum yalnız aria-pressed ile bildirilir.
    await expect(first).toHaveAccessibleName(`${name} favorilerde`);
    await expect(headerCount(page)).toHaveText("1");
    await expect.poll(async () => (await idsOf(page)).length).toBe(1);

    // Sayfa yenilenince kalp sunucudaki durumdan geri gelir.
    await page.reload();
    await expect(heart(page).first()).toHaveAttribute("aria-pressed", "true");
    await expect(headerCount(page)).toHaveText("1");

    await page.locator("header").getByRole("link", { name: /Favorilerim/ }).click();
    await expect(page).toHaveURL(/\/favorilerim$/);
    await expect(page.getByRole("heading", { level: 1, name: "Favorilerim" })).toBeVisible();
    await expect(rows(page)).toHaveCount(1);
    const row = rows(page).first();
    await expect(row).toContainText(name);
    await expect(row).toHaveAttribute("data-available", "true");
    await expect(row.getByTestId("favorite-price")).toContainText(/₺|TL/);
    await expect(row.getByRole("link", { name })).toHaveAttribute("href", /^\/urun\//);

    await row.getByRole("button", { name: `${name} favorilerden kaldır` }).click();
    await expect(page.getByTestId("favorites-empty")).toBeVisible();
    await expect(page.getByTestId("favorites-announce")).toContainText("favorilerden kaldırıldı");
    await expect(rows(page)).toHaveCount(0);
    await expect(headerCount(page)).toHaveCount(0);
    expect(await idsOf(page)).toEqual([]);
  });

  test("kalpten çıkarma: kart kalbi tekrar basınca kalkar; klavye (Boşluk/Enter) çalışır", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await page.goto("/");
    const first = heart(page).first();
    await expect(first).toBeEnabled(); // hidrasyon bitene dek düğme devre dışıdır
    await first.focus();
    await page.keyboard.press("Space");
    await expect(first).toHaveAttribute("aria-pressed", "true");
    await expect(headerCount(page)).toHaveText("1");
    // Yazma sürerken (aria-busy) basışlar bilerek yok sayılır; ikinci basış istek bitince yapılır.
    await expect(first).not.toHaveAttribute("aria-busy", "true");
    await page.keyboard.press("Enter");
    await expect(first).toHaveAttribute("aria-pressed", "false");
    await expect(headerCount(page)).toHaveCount(0);
    // Arayüz iyimser güncellenir; sunucudaki durum istek bitince yakınsar.
    await expect.poll(() => idsOf(page)).toEqual([]);
  });

  test("sunucu reddederse (409 favorite_limit / 404) kalp geri alınır ve görünür hata çıkar", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await page.goto("/");
    const first = heart(page).first();
    await expect(first).toHaveAttribute("aria-pressed", "false");
    for (const [status, code, text] of [[409, "favorite_limit", /en çok 100/i], [404, "product_not_found", /artık satışta değil/i]] as const) {
      await page.route(`**${api}/favorites/*`, (route) => route.request().method() === "PUT"
        ? route.fulfill({ status, contentType: "application/json", body: JSON.stringify({ code }) })
        : route.continue());
      await first.click();
      await expect(page.getByTestId("favorite-error").first()).toContainText(text);
      await expect(first).toHaveAttribute("aria-pressed", "false");
      await expect(headerCount(page)).toHaveCount(0);
      await page.unroute(`**${api}/favorites/*`);
    }
    expect(await idsOf(page)).toEqual([]);
  });

  test("başka kullanıcının favorisi görünmez: A'nın kalbi B'nin hesabında yok", async ({ page, browser, baseURL }) => {
    const other = await newUserContext(browser, baseURL!);
    try {
      await registerApi(page.request, baseURL!);
      await page.goto("/");
      await heart(page).first().click();
      await expect(headerCount(page)).toHaveText("1");
      const otherPage = other.page;
      await otherPage.goto("/favorilerim");
      await expect(otherPage.getByTestId("favorites-empty")).toBeVisible();
      expect(await idsOf(otherPage)).toEqual([]);
    } finally { await other.context.close(); }
  });

  test("çıkış yapıp başka kullanıcıyla giriş (aynı sekme, istemci içi gezinti): önceki kullanıcının kalpleri ne kartta ne sayaçta ne listede görünür", async ({ page, browser, baseURL }) => {
    const other = await newUserContext(browser, baseURL!);
    try {
      await registerApi(page.request, baseURL!);
      await page.goto("/");
      const first = heart(page).first();
      await first.click();
      await expect(first).toHaveAttribute("aria-pressed", "true");
      await expect(headerCount(page)).toHaveText("1");
      // Sayfa yenilenmez: depo bellekte yaşar, sıfırlama gerçekten sınanır.
      await page.locator("header").getByRole("link", { name: /Hesabım/ }).click();
      await page.getByRole("button", { name: "Çıkış yap", exact: true }).click();
      await expect(page).toHaveURL(/\/giris$/);
      await expect(headerCount(page)).toHaveCount(0);
      await page.getByLabel("E-posta", { exact: true }).fill(other.email);
      await page.getByLabel("Parola", { exact: true }).fill(password);
      await page.getByRole("button", { name: "Giriş yap", exact: true }).click();
      await expect(page).toHaveURL(/\/hesabim$/);
      await expect(page.locator("main")).toContainText(other.email);
      await expect(headerCount(page)).toHaveCount(0);
      await page.locator("header").getByRole("link", { name: /^SSCommerce/ }).click();
      await expect(page).toHaveURL(/\/$/);
      await expect(heart(page).first()).toHaveAttribute("aria-pressed", "false");
      await expect(page.locator("[data-testid='favorite-button'][aria-pressed='true']")).toHaveCount(0);
      await expect(headerCount(page)).toHaveCount(0);
      await page.locator("header").getByRole("link", { name: /Favorilerim/ }).click();
      await expect(page.getByTestId("favorites-empty")).toBeVisible();
      await expect(rows(page)).toHaveCount(0);
      expect(await idsOf(page)).toEqual([]);
    } finally { await other.context.close(); }
  });

  test("boş durum ve dolu liste erişilebilirlik (axe) ihlalsiz", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await page.goto("/favorilerim");
    await expect(page.getByTestId("favorites-empty")).toBeVisible();
    expect(await axeSerious(page)).toEqual([]);
    await page.goto("/");
    await heart(page).first().click();
    await expect(headerCount(page)).toHaveText("1");
    expect(await axeSerious(page)).toEqual([]);
    await page.goto("/favorilerim");
    await expect(rows(page)).toHaveCount(1);
    expect(await axeSerious(page)).toEqual([]);
  });
});

test.describe("misafir", () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test("kalbe basınca /giris?next=<bulunduğu yol>; sayaç yok; /favorilerim giriş ister; API 401", async ({ page }) => {
    await page.goto("/");
    await expect(headerCount(page)).toHaveCount(0);
    await heart(page).first().click();
    await expect(page).toHaveURL(/\/giris\?next=%2F$/);

    const response = await page.goto("/favorilerim");
    expect(response?.status()).toBe(200);
    const block = page.getByTestId("favorites-anonymous");
    await expect(block).toBeVisible();
    await expect(block.getByRole("link", { name: "Giriş yap" })).toHaveAttribute("href", `/giris?next=${encodeURIComponent("/favorilerim")}`);
    await expect(headerCount(page)).toHaveCount(0);
    expect((await page.request.get(`${api}/favorites/ids`)).status()).toBe(401);
    expect((await page.request.get(`${api}/favorites`)).status()).toBe(401);
  });
});

test.describe("mobil 390px", () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test("kalp ve alt gezinti sayacı erişilebilir; 44px dokunma alanı; yatay taşma yok; axe ihlalsiz", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await page.goto("/");
    const first = heart(page).first();
    await first.scrollIntoViewIfNeeded();
    const box = await first.boundingBox();
    expect(box!.width).toBeGreaterThanOrEqual(44);
    expect(box!.height).toBeGreaterThanOrEqual(44);
    const nav = page.getByRole("navigation", { name: "Alt gezinti" });
    const link = nav.getByRole("link", { name: /Favoriler/ });
    await expect(link).toHaveAttribute("href", "/favorilerim");
    await expect(nav.getByRole("button", { name: /Favoriler/ })).toHaveCount(0);
    await expect(nav.getByTestId("favorites-count")).toHaveCount(0);
    await first.click();
    await expect(first).toHaveAttribute("aria-pressed", "true");
    await expect(nav.getByTestId("favorites-count")).toHaveText("1");
    await expect(link).toHaveAccessibleName(/Favoriler\s*,\s*1 ürün/);
    expect(await noHorizontalOverflow(page)).toBe(true);
    expect(await axeSerious(page)).toEqual([]);
    const linkBox = await link.boundingBox();
    expect(linkBox!.width).toBeGreaterThanOrEqual(44);
    expect(linkBox!.height).toBeGreaterThanOrEqual(44);

    await link.click();
    await expect(page).toHaveURL(/\/favorilerim$/);
    await expect(link).toHaveAttribute("aria-current", "page");
    await expect(rows(page)).toHaveCount(1);
    await expect(rows(page).first().getByRole("button", { name: /favorilerden kaldır/ })).toBeVisible();
    expect(await noHorizontalOverflow(page)).toBe(true);
    expect(await axeSerious(page)).toEqual([]);
  });
});

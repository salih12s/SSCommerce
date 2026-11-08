import type { Page } from "@playwright/test";
import { axeSerious, credentialsPause, customer, customerPassword, customerWithCartAndAddress, expect, newCtx, ready, test, viewports } from "./support";

// P5: erişilebilirlik (axe: ciddi/kritik ihlal 0) ve klavye (Tab/Enter) akışı.

for (const viewport of viewports) {
  test(`P5 axe ${viewport.width}px: vitrin + checkout + 3 admin sayfası`, async ({ browser, admin, sellable }) => {
    const found: string[] = [];
    const check = async (page: Page, path: string) => {
      await page.goto(path);
      await ready(page);
      for (const v of await axeSerious(page)) found.push(`${viewport.width}px ${path}: ${v}`);
    };
    const anon = await newCtx(browser, { viewport });
    const anonPage = await anon.newPage();
    for (const path of ["/", `/urun/${sellable.slug}`, "/sepet", "/giris"]) await check(anonPage, path);
    await anon.close();

    const buyer = await customerWithCartAndAddress(browser, sellable, { viewport });
    const buyerPage = await buyer.context.newPage();
    await check(buyerPage, "/sepet (dolu)");
    await buyerPage.goto("/checkout");
    await ready(buyerPage);
    await expect(buyerPage.getByTestId("preview")).toBeVisible();
    for (const v of await axeSerious(buyerPage)) found.push(`${viewport.width}px /checkout: ${v}`);
    await buyer.context.close();

    const adminPage = await admin.context.newPage();
    await adminPage.setViewportSize(viewport);
    for (const path of ["/admin", "/admin/urunler", "/admin/siparisler"]) await check(adminPage, path);
    await adminPage.close();
    expect(found).toEqual([]);
  });
}

/** Odaktaki öğenin görünür bir odak göstergesi var mı (outline ya da box-shadow). */
async function focusIndicator(page: Page) {
  return await page.evaluate(() => {
    const el = document.activeElement as HTMLElement | null;
    if (!el || el === document.body) return { ok: false, what: "body" };
    const style = getComputedStyle(el);
    const outline = style.outlineStyle !== "none" && parseFloat(style.outlineWidth) > 0;
    const shadow = style.boxShadow !== "none";
    return { ok: el.matches(":focus-visible") && (outline || shadow), what: `${el.tagName.toLowerCase()}${el.id ? `#${el.id}` : ""} ${(el.textContent ?? "").trim().slice(0, 24)}` };
  });
}

test("P5 klavye: giriş formu Tab/Enter ile doldurulup gönderilir, odak görünür; ürün klavyeyle sepete eklenir", async ({ browser, sellable }) => {
  // Hesap API ile kurulur (bu test klavye akışını sınar), çıkış yapılmış bağlamda klavyeyle giriş yapılır.
  const seed = await customer(browser);
  const email = seed.email;
  await seed.context.close();

  const context = await newCtx(browser);
  const page = await context.newPage();
  await page.goto("/giris");
  await expect(page.getByRole("button", { name: "Giriş yap", exact: true })).toBeEnabled();
  const missingFocus: string[] = [];
  let reachedEmail = false;
  for (let i = 0; i < 40 && !reachedEmail; i++) {
    await page.keyboard.press("Tab");
    const indicator = await focusIndicator(page);
    if (!indicator.ok) missingFocus.push(indicator.what);
    reachedEmail = await page.evaluate(() => document.activeElement?.id === "email");
  }
  expect(reachedEmail, "Tab ile e-posta alanına ulaşıldı").toBe(true);
  await page.keyboard.type(email);
  await page.keyboard.press("Tab");
  expect(await page.evaluate(() => (document.activeElement as HTMLInputElement | null)?.id), "Tab ile parola alanı").toBe("password");
  await page.keyboard.type(customerPassword);
  expect((await focusIndicator(page)).ok, "parola alanında odak göstergesi").toBe(true);
  await credentialsPause();
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/\/hesabim$/);
  expect(missingFocus, "Tab ile geçilen öğelerde görünür odak göstergesi yok").toEqual([]);

  // Ürün: Tab ile varyant radyosuna, Space ile seç, Tab ile "Sepete ekle" düğmesine, Enter.
  await page.goto(`/urun/${sellable.slug}`);
  await ready(page);
  let added = false;
  const noIndicator: string[] = [];
  for (let i = 0; i < 80 && !added; i++) {
    await page.keyboard.press("Tab");
    const info = await page.evaluate(() => {
      const el = document.activeElement as HTMLInputElement | HTMLButtonElement | null;
      return { tag: el?.tagName ?? "", type: (el as HTMLInputElement | null)?.type ?? "", checked: (el as HTMLInputElement | null)?.checked ?? false, text: (el?.textContent ?? "").trim() };
    });
    if (info.tag === "INPUT" && info.type === "radio") {
      if (!(await focusIndicator(page)).ok) noIndicator.push("radio");
      if (!info.checked) await page.keyboard.press("Space");
    }
    if (info.tag === "BUTTON" && info.text === "Sepete ekle") {
      if (!(await focusIndicator(page)).ok) noIndicator.push("Sepete ekle");
      await page.keyboard.press("Enter");
      added = true;
    }
  }
  expect(added, "klavyeyle 'Sepete ekle' düğmesine ulaşıldı").toBe(true);
  await expect(page.getByRole("status").filter({ hasText: "Sepete eklendi" })).toBeVisible();
  await page.goto("/sepet");
  await ready(page);
  await expect(page.getByTestId("cart-line")).toHaveCount(1);
  expect(noIndicator, "odak göstergesi eksik").toEqual([]);
  await context.close();
});

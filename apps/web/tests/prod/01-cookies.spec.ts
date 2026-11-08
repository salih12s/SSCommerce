import { request as pwRequest } from "@playwright/test";
import { API, BASE, credentialsPause, customerPassword, expect, newCtx, newEmail, test } from "./support";

// P1: HTTPS'te çerez öznitelikleri (yerel CA'lı üretim-eşdeğeri yığın). Kayıt ve çıkış arayüzden yapılır.
test("P1 çerezler: __Host- önekli, Secure, HttpOnly, doğru SameSite; çıkış oturumu geçersiz kılar; JS çerezi görmez", async ({ browser }) => {
  const context = await newCtx(browser);
  const page = await context.newPage();
  await page.goto("/kayit");
  await expect(page.getByRole("button", { name: "Hesap oluştur", exact: true })).toBeEnabled(); // hidrasyon
  await page.getByLabel("E-posta", { exact: true }).fill(newEmail());
  await page.getByLabel("Parola", { exact: true }).fill(customerPassword);
  await credentialsPause(); // yığının kimlik bilgisi hız sınırı: 10/dk/IP
  await page.getByRole("button", { name: "Hesap oluştur", exact: true }).click();
  await expect(page).toHaveURL(/\/hesabim$/);
  await expect(page.getByRole("button", { name: "Çıkış yap" })).toBeVisible();

  const cookies = await context.cookies();
  const auth = cookies.find((c) => c.name === "__Host-ssc.auth");
  const csrf = cookies.find((c) => c.name === "__Host-ssc.csrf");
  expect(auth, "__Host-ssc.auth çerezi").toBeTruthy();
  expect(csrf, "__Host-ssc.csrf çerezi").toBeTruthy();
  // Oturum çerezi
  expect(auth!.secure, "auth secure").toBe(true);
  expect(auth!.httpOnly, "auth httpOnly").toBe(true);
  expect(auth!.path, "auth path").toBe("/");
  expect(auth!.domain, "auth domain host-only (nokta öneksiz)").toBe(new URL(BASE).hostname);
  expect(auth!.sameSite, "auth sameSite").toBe("Lax");
  // CSRF çerezi
  expect(csrf!.secure, "csrf secure").toBe(true);
  expect(csrf!.httpOnly, "csrf httpOnly").toBe(true);
  expect(csrf!.path, "csrf path").toBe("/");
  expect(csrf!.domain, "csrf domain host-only").toBe(new URL(BASE).hostname);
  expect(csrf!.sameSite, "csrf sameSite").toBe("Strict");

  // JS'ten document.cookie bu çerezleri görmemeli (HttpOnly).
  const visible = await page.evaluate(() => document.cookie);
  expect(visible, "document.cookie").not.toContain("ssc");
  expect(visible).not.toContain("__Host");

  // Geçerli oturum çerezi ile /auth/me 200 (testin anlamlı olması için), çıkıştan sonra aynı değerle 401. Değer hiçbir yere yazdırılmaz.
  const probe = await pwRequest.newContext({ baseURL: BASE, ignoreHTTPSErrors: true, extraHTTPHeaders: { Cookie: `__Host-ssc.auth=${auth!.value}` } });
  try {
    expect((await probe.get(`${API}/auth/me`)).status(), "çıkıştan önce eski çerez geçerli").toBe(200);
    await page.getByRole("button", { name: "Çıkış yap" }).click();
    await expect(page.getByRole("button", { name: "Çıkış yap" })).toHaveCount(0);
    expect((await probe.get(`${API}/auth/me`)).status(), "çıkıştan sonra eski çerez değeriyle /auth/me").toBe(401);
  } finally {
    await probe.dispose();
  }
  // Tarayıcının kendi bağlamı da anonim görünür.
  expect((await context.request.get(`${API}/auth/me`)).status()).toBe(401);
  await context.close();
});

import { expect, test, type Page } from "@playwright/test";
import { addressApi, api, axeSerious, newUserContext, noHorizontalOverflow, password, registerApi, send } from "./support";

// MD-2 Profil: ortak hesap menüsü, /hesabim özeti, /hesabim/guvenlik parola değiştirme. Sözleşme: docs/PLAN_CUSTOMER_EXPERIENCE.md "MD-2 sözleşme".
// Bu dosya yalnız KENDİ kullanıcılarının verisini değiştirir; katalog/stok yazmaz (sipariş gerektiren gerçek özet testi orders.spec.ts'te, "ordering" projesinde),
// bu yüzden "main" projesinde paralel koşar. Giriş hız sınırı E2E ana makinesinde 1000/dk'dır (E2eHost); 429 arayüzü route ile taklit edilir.
const newPassword = "Yeni-parola-77-Only";
const accountNav = (page: Page) => page.getByRole("navigation", { name: "Hesap menüsü" });
const FIVE = [
  ["/hesabim", "Özet"],
  ["/siparisler", "Siparişlerim"],
  ["/favorilerim", "Favorilerim"],
  ["/hesabim/adresler", "Adreslerim"],
  ["/hesabim/guvenlik", "Güvenlik"],
] as const;
const formatIstanbul = (iso: string) => new Date(iso).toLocaleDateString("tr-TR", { dateStyle: "long", timeZone: "Europe/Istanbul" });

/** Formun hidrasyonu bitene dek gönder düğmesi pasiftir (AuthForm kalıbı). */
async function openSecurity(page: Page) {
  await page.goto("/hesabim/guvenlik");
  await expect(page.getByTestId("password-form")).toBeVisible();
  await expect(page.getByRole("button", { name: "Parolayı değiştir", exact: true })).toBeEnabled();
}
async function fillPasswords(page: Page, current: string, next: string, again = next) {
  await page.getByLabel("Mevcut parola", { exact: true }).fill(current);
  await page.getByLabel("Yeni parola", { exact: true }).fill(next);
  await page.getByLabel("Yeni parola (tekrar)", { exact: true }).fill(again);
}
const submitPassword = (page: Page) => page.getByRole("button", { name: "Parolayı değiştir", exact: true }).click();
async function login(page: Page, email: string, pass: string) {
  await page.getByLabel("E-posta", { exact: true }).fill(email);
  await page.getByLabel("Parola", { exact: true }).fill(pass);
  await page.getByRole("button", { name: "Giriş yap", exact: true }).click();
}

test.describe("masaüstü", () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test("hesap menüsü beş sayfada görünür; yalnız bulunulan sayfa aria-current=page; bağlantılar doğru", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    for (const [path, label] of FIVE) {
      await page.goto(path);
      const nav = accountNav(page);
      await expect(nav, path).toBeVisible();
      await expect(nav.getByRole("link")).toHaveCount(5);
      await expect(nav.locator("[aria-current]")).toHaveCount(1);
      await expect(nav.getByRole("link", { name: label, exact: true }), path).toHaveAttribute("aria-current", "page");
      for (const [href, other] of FIVE) await expect(nav.getByRole("link", { name: other, exact: true })).toHaveAttribute("href", href);
    }
    // Menüden gezinme: tıklanan sayfa seçili olur.
    await page.goto("/hesabim");
    await accountNav(page).getByRole("link", { name: "Güvenlik", exact: true }).click();
    await expect(page).toHaveURL(/\/hesabim\/guvenlik$/);
    await expect(accountNav(page).getByRole("link", { name: "Güvenlik", exact: true })).toHaveAttribute("aria-current", "page");
    await expect(accountNav(page).getByRole("link", { name: "Özet", exact: true })).not.toHaveAttribute("aria-current", "page");
  });

  test("vitrin, sepet ve ödeme sayfalarında hesap menüsü yok", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    for (const path of ["/", "/sepet", "/checkout"]) {
      await page.goto(path);
      await expect(accountNav(page), path).toHaveCount(0);
    }
  });

  test("özet: yeni kullanıcı 0 sipariş / 0 adres / 0 favori; e-posta, doğrulama, üyelik tarihi (İstanbul, Türkçe), hesap kimliği; sonra adres ve favori sayıları", async ({ page, baseURL }) => {
    const email = await registerApi(page.request, baseURL!);
    const me = await (await page.request.get(`${api}/auth/me`)).json() as { id: string; createdAt?: string };
    expect(me.createdAt, "sözleşme: auth/me createdAt (ISO-8601 UTC)").toMatch(/^\d{4}-\d\d-\d\dT[\d:.]+Z$/);
    await page.goto("/hesabim");
    await expect(page.getByTestId("account-email")).toHaveText(email);
    await expect(page.getByTestId("account-verified")).toContainText("Doğrulanmadı");
    await expect(page.getByTestId("account-since")).toHaveText(formatIstanbul(me.createdAt!));
    await expect(page.getByTestId("account-id")).toHaveText(me.id);
    await expect(page.getByRole("button", { name: "Kopyala" })).toBeVisible();
    await expect(page.getByTestId("tally-orders")).toHaveText("0");
    await expect(page.getByTestId("tally-addresses")).toHaveText("0");
    await expect(page.getByTestId("tally-favorites")).toHaveText("0");
    await expect(page.getByTestId("recent-orders-empty")).toContainText("Henüz siparişin yok");
    await expect(page.getByTestId("recent-orders-empty").getByRole("link", { name: "Mağazaya göz at" })).toHaveAttribute("href", "/#urunler");
    await expect(page.getByRole("link", { name: "Yönetim" })).toHaveCount(0); // müşteri rolü
    await expect(page.getByRole("button", { name: "Çıkış yap", exact: true })).toBeVisible();

    // Adres API'den eklenir; favori arayüzden (kalp) eklenir ve özet sayısı 1 olur.
    await addressApi(page.request, baseURL!);
    await page.goto("/");
    const heart = page.getByTestId("favorite-button").first();
    await expect(heart).toBeEnabled();
    await heart.click();
    await expect(heart).toHaveAttribute("aria-pressed", "true");
    // Kalp iyimser güncellenir: gezinmeden önce yazmanın sunucuda tamamlanmasını bekle (yoksa özet henüz yazılmamış listeyi okur).
    await expect.poll(async () => ((await (await page.request.get(`${api}/favorites/ids`)).json()) as { productIds: string[] }).productIds.length).toBe(1);
    await page.locator("header").getByRole("link", { name: /Hesabım/ }).click();
    await expect(page).toHaveURL(/\/hesabim$/);
    await expect(page.getByTestId("tally-addresses")).toHaveText("1");
    await expect(page.getByTestId("tally-favorites")).toHaveText("1");
    await expect(page.getByTestId("tally-orders")).toHaveText("0");
  });

  test("özet: son 3 sipariş en çok 3 satır ve sipariş sayısı toplamdan (GET /orders?page=1&pageSize=3); durum etiketleri; tümünü gör (taklit yanıt)", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    const seen: string[] = [];
    const order = (n: number, status: string) => ({ id: `0192b5a0-0000-7000-8000-00000000000${n}`, orderNumber: `SS-20261108-000${n}`, status, createdAt: "2026-11-08T09:00:00Z", currency: "TRY", itemCount: n, subtotalMinor: 1000 * n, shippingStatus: "pending", canCancel: false, previewItems: [] });
    await page.route("**/api/backend/api/v1/orders?*", async (route) => {
      seen.push(new URL(route.request().url()).search);
      await route.fulfill({ json: { items: [order(1, "placed"), order(2, "shipped"), order(3, "cancelled")], page: 1, pageSize: 3, totalCount: 5, totalPages: 2 } });
    });
    await page.goto("/hesabim");
    await expect(page.getByTestId("recent-order-row")).toHaveCount(3);
    expect(seen[0]).toBe("?page=1&pageSize=3");
    await expect(page.getByTestId("tally-orders")).toHaveText("5");
    await expect(page.getByTestId("recent-order-row").nth(0).getByTestId("order-status")).toHaveText("Alındı");
    await expect(page.getByTestId("recent-order-row").nth(1).getByTestId("order-status")).toHaveText("Kargoda");
    await expect(page.getByTestId("recent-order-row").nth(2).getByTestId("order-status")).toHaveText("İptal edildi");
    await expect(page.getByTestId("recent-order-row").first().getByRole("link")).toHaveAttribute("href", "/siparisler/0192b5a0-0000-7000-8000-000000000001");
    await expect(page.getByRole("link", { name: /Tüm siparişleri gör \(5\)/ })).toHaveAttribute("href", "/siparisler");
  });

  test("bloklar bağımsızdır: siparişler düşse de e-posta, adres ve favori çalışır; Tekrar dene düzeltir", async ({ page, baseURL }) => {
    const email = await registerApi(page.request, baseURL!);
    let fail = true;
    await page.route("**/api/backend/api/v1/orders?*", (route) => fail ? route.fulfill({ status: 500, json: { code: "internal" } }) : route.continue());
    await page.goto("/hesabim");
    await expect(page.getByTestId("account-email")).toHaveText(email);
    await expect(page.getByTestId("tally-addresses")).toHaveText("0");
    await expect(page.getByTestId("tally-favorites")).toHaveText("0");
    await expect(page.getByText("Siparişler yüklenemedi.")).toBeVisible();
    await expect(page.getByTestId("tally-orders")).toContainText("yüklenemedi");
    fail = false;
    await page.getByRole("button", { name: "Tekrar dene" }).first().click();
    await expect(page.getByTestId("recent-orders-empty")).toBeVisible();
    await expect(page.getByTestId("tally-orders")).toHaveText("0");
  });

  test("eski API: createdAt yoksa üyelik tarihi satırı gizlenir; Admin rolü Yönetim bağlantısını gösterir (taklit yanıt)", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await page.route("**/api/backend/api/v1/auth/me", async (route) => {
      const body = await (await route.fetch()).json() as Record<string, unknown>;
      delete body.createdAt;
      await route.fulfill({ json: { ...body, roles: ["Admin"] } });
    });
    await page.goto("/hesabim");
    await expect(page.getByTestId("account-email")).toBeVisible();
    await expect(page.getByText("Üyelik tarihi")).toHaveCount(0);
    await expect(page.getByTestId("account-since")).toHaveCount(0);
    await expect(page.getByRole("link", { name: "Yönetim", exact: true })).toHaveAttribute("href", "/admin");
  });

  test("anonim: /hesabim ve /hesabim/guvenlik giriş ister; yanıt no-store", async ({ page }) => {
    for (const [path, testId] of [["/hesabim", null], ["/hesabim/guvenlik", "security-anonymous"]] as const) {
      const response = await page.goto(path);
      expect(response?.headers()["cache-control"], path).toContain("no-store");
      await expect(page.locator("main"), path).toContainText("giriş yap");
      if (testId) await expect(page.getByTestId(testId)).toBeVisible();
    }
  });

  test("parola formu: tekrar eşleşmezse istek gitmez; alanlar autocomplete, göster/gizle; parola URL/depoda kalmaz", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    const posts: string[] = [];
    page.on("request", (r) => { if (r.method() === "POST" && r.url().endsWith("/auth/password")) posts.push(r.url()); });
    await openSecurity(page);
    await expect(page.getByLabel("Mevcut parola", { exact: true })).toHaveAttribute("autocomplete", "current-password");
    await expect(page.getByLabel("Yeni parola", { exact: true })).toHaveAttribute("autocomplete", "new-password");
    await expect(page.getByLabel("Yeni parola (tekrar)", { exact: true })).toHaveAttribute("autocomplete", "new-password");
    for (const name of ["Mevcut parola", "Yeni parola", "Yeni parola (tekrar)"]) await expect(page.getByLabel(name, { exact: true })).toHaveAttribute("type", "password");
    await page.getByLabel("Parolaları göster").check();
    for (const name of ["Mevcut parola", "Yeni parola", "Yeni parola (tekrar)"]) await expect(page.getByLabel(name, { exact: true })).toHaveAttribute("type", "text");
    await page.getByLabel("Parolaları göster").uncheck();

    await fillPasswords(page, password, newPassword, `${newPassword}x`);
    await submitPassword(page);
    await expect(page.getByText("Yeni parola ile tekrarı aynı değil.")).toBeVisible();
    await expect(page.getByLabel("Yeni parola (tekrar)", { exact: true })).toBeFocused();
    await expect(page.getByLabel("Yeni parola (tekrar)", { exact: true })).toHaveAttribute("aria-invalid", "true");
    expect(posts).toEqual([]);

    expect(page.url()).not.toContain(newPassword);
    expect(await page.evaluate(() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }))).not.toContain(newPassword);
  });

  test("yanlış mevcut parola: alan bazlı hata, odak alanda, parola değişmez (eski parola hâlâ geçerli)", async ({ page, baseURL }) => {
    const email = await registerApi(page.request, baseURL!);
    await openSecurity(page);
    await fillPasswords(page, "Yanlis-parola-1", newPassword);
    await submitPassword(page);
    const current = page.getByLabel("Mevcut parola", { exact: true });
    await expect(page.getByText("Mevcut parola hatalı.")).toBeVisible();
    await expect(current).toHaveAttribute("aria-invalid", "true");
    await expect(current).toBeFocused();
    await expect(page.getByTestId("password-done")).toBeEmpty();
    // Eski parola geçerli kalır.
    const again = await send(page.request, "POST", "/auth/login", baseURL!, { email, password });
    expect(again.status()).toBe(200);
  });

  test("zayıf yeni parola: newPassword alanında sunucu mesajı", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await openSecurity(page);
    await fillPasswords(page, password, "abc");
    await submitPassword(page);
    const field = page.getByLabel("Yeni parola", { exact: true });
    await expect(field).toHaveAttribute("aria-invalid", "true");
    await expect(page.locator("#yeni-parola-hata")).not.toBeEmpty();
    await expect(field).toBeFocused();
  });

  test("429: Retry-After ile Türkçe mesaj, alanlar korunur (taklit yanıt)", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await page.route("**/api/backend/api/v1/auth/password", (route) => route.fulfill({ status: 429, headers: { "Retry-After": "30" }, json: { code: "rate_limited" } }));
    await openSecurity(page);
    await fillPasswords(page, password, newPassword);
    await submitPassword(page);
    await expect(page.getByTestId("password-error")).toContainText("Çok fazla deneme");
    await expect(page.getByTestId("password-error")).toContainText("30 saniye");
    await expect(page.getByLabel("Mevcut parola", { exact: true })).toHaveValue(password);
  });

  test("parola değiştir → diğer cihaz oturumu düşer, bu oturum sürer → çıkış → eski parola reddedilir → yeni parolayla giriş", async ({ browser, baseURL }) => {
    const { context, page, email } = await newUserContext(browser, baseURL!);
    // İkinci tarayıcı bağlamı: aynı hesaba ayrı oturum.
    const other = await browser.newContext({ baseURL: baseURL! });
    const otherPage = await other.newPage();
    try {
      expect((await send(other.request, "POST", "/auth/login", baseURL!, { email, password })).status()).toBe(200);
      await otherPage.goto("/hesabim/guvenlik");
      await expect(otherPage.getByTestId("password-form")).toBeVisible();
      expect((await other.request.get(`${api}/auth/me`)).status()).toBe(200);

      await openSecurity(page);
      await fillPasswords(page, password, newPassword);
      await submitPassword(page);
      await expect(page.getByTestId("password-done")).toContainText("Parolan değişti. Diğer cihazlardaki oturumların kapatıldı.");
      // Alanlar temizlenir; yeni parola ekranda ve depoda kalmaz.
      for (const name of ["Mevcut parola", "Yeni parola", "Yeni parola (tekrar)"]) await expect(page.getByLabel(name, { exact: true })).toHaveValue("");
      expect(await page.evaluate(() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }) + location.href)).not.toContain(newPassword);
      // Bu oturum açık kalır.
      expect((await context.request.get(`${api}/auth/me`)).status()).toBe(200);
      await page.reload();
      await expect(page.getByTestId("password-form")).toBeVisible();

      // Diğer bağlam artık oturumsuz: API 401, sayfa yenilenince giriş ister.
      expect((await other.request.get(`${api}/auth/me`)).status()).toBe(401);
      await otherPage.reload();
      await expect(otherPage.getByTestId("security-anonymous")).toBeVisible();

      // Çıkış → eski parola reddedilir → yeni parola kabul edilir.
      await page.goto("/hesabim");
      await page.getByRole("button", { name: "Çıkış yap", exact: true }).click();
      await expect(page).toHaveURL(/\/giris$/);
      await login(page, email, password);
      await expect(page.locator("main").getByRole("alert")).toContainText("E-posta veya parola hatalı");
      await expect(page).toHaveURL(/\/giris$/);
      await login(page, email, newPassword);
      await expect(page).toHaveURL(/\/hesabim$/);
      await expect(page.getByTestId("account-email")).toHaveText(email);
    } finally { await other.close(); await context.close(); }
  });
});

for (const width of [390, 1440]) {
  test.describe(`${width}px`, () => {
    test.use({ viewport: { width, height: width === 390 ? 844 : 900 } });

    test("hesap sayfaları yatay taşmaz, menü hedefleri en az 44px, seçili etiket görünür, axe ciddi/kritik yok", async ({ page, baseURL }) => {
      await registerApi(page.request, baseURL!);
      for (const [path, label] of FIVE) {
        await page.goto(path);
        const nav = accountNav(page);
        await expect(nav).toBeVisible();
        if (path === "/hesabim") await expect(page.getByTestId("tally-orders")).toHaveText("0");
        if (path === "/hesabim/guvenlik") await expect(page.getByTestId("password-form")).toBeVisible();
        for (const link of await nav.getByRole("link").all()) expect((await link.boundingBox())!.height, `${path} hedef`).toBeGreaterThanOrEqual(44);
        const current = nav.getByRole("link", { name: label, exact: true });
        await expect(current).toBeInViewport();
        expect(await noHorizontalOverflow(page), `${path} sayfa yatay taşma`).toBe(true);
        if (width === 390) {
          // Menü kendi içinde yatay kaydırılır (5 etiket 390px'e sığmaz), sayfa kaymaz.
          expect(await nav.evaluate((el) => el.scrollWidth > el.clientWidth), `${path} menü kaydırılabilir`).toBe(true);
        }
        expect(await axeSerious(page), path).toEqual([]);
      }
    });

    test("güvenlik formu hata ve başarı durumunda da taşmaz, axe temiz", async ({ page, baseURL }) => {
      await registerApi(page.request, baseURL!);
      await openSecurity(page);
      await fillPasswords(page, "Yanlis-parola-1", newPassword);
      await submitPassword(page);
      await expect(page.getByText("Mevcut parola hatalı.")).toBeVisible();
      expect(await noHorizontalOverflow(page)).toBe(true);
      expect(await axeSerious(page)).toEqual([]);
      await fillPasswords(page, password, newPassword);
      await submitPassword(page);
      await expect(page.getByTestId("password-done")).toContainText("Parolan değişti");
      expect(await noHorizontalOverflow(page)).toBe(true);
      expect(await axeSerious(page)).toEqual([]);
    });
  });
}

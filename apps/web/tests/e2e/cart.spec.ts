import { expect, test, type Page, type Request } from "@playwright/test";
import { formatPriceMinor } from "../../src/lib/format";
import { safeNext } from "../../src/lib/safe-next";
import {
  addressApi, api, axeSerious, cartApi, getCart, goodAddress, loadVariants, newEmail, newUserContext, noHorizontalOverflow,
  password, registerApi, send, variantRow, viewports, withStock, type Variant,
} from "./support";

// Bu dosya yalnızca KENDİ kullanıcılarının sepet/adres verisini değiştirir; katalog fiyat/stok/aktiflik fixture'ına dokunmaz
// (o senaryolar mutations.spec.ts'te, ayrı ve tek worker'lı projede koşar).
let variants: Variant[];
let stock12: Variant;
let stock3: Variant;
test.beforeAll(async ({ request }) => {
  variants = await loadVariants(request);
  stock12 = withStock(variants, 12);
  stock3 = withStock(variants, 3);
});

const line = (page: Page) => page.getByTestId("cart-line");
const headerCount = (page: Page) => page.locator("header").getByTestId("cart-count");
async function signupUi(page: Page, next?: string) {
  const email = newEmail();
  await page.goto(next ? `/kayit?next=${encodeURIComponent(next)}` : "/kayit");
  await page.getByLabel("E-posta", { exact: true }).fill(email);
  await page.getByLabel("Parola", { exact: true }).fill(password);
  await page.getByRole("button", { name: "Hesap oluştur", exact: true }).click();
  return email;
}
async function selectVariant(page: Page, variant: Variant) {
  await page.goto(`/urun/${variant.productSlug}`);
  await expect(page.getByRole("heading", { level: 2, name: "Seçenekler" })).toBeVisible();
  await variantRow(page, variant).getByRole("radio").check();
}

test.describe("anonim kullanıcı", () => {
  test("ürün sayfası giriş ister; /sepet, /checkout ve adresler giriş gerektirir; sayaç yok; önbelleğe alınmaz", async ({ page }) => {
    await selectVariantAnonymous(page);
    for (const [route, testId] of [["/sepet", "cart-anonymous"], ["/checkout", "checkout-anonymous"], ["/hesabim/adresler", "addresses-anonymous"]] as const) {
      const response = await page.goto(route);
      expect(response?.status()).toBe(200);
      expect(response?.headers()["cache-control"], route).toContain("no-store");
      const block = page.getByTestId(testId);
      await expect(block).toBeVisible();
      await expect(block.getByRole("link", { name: "Giriş yap" })).toHaveAttribute("href", `/giris?next=${encodeURIComponent(route)}`);
      await expect(page.getByTestId("cart-count")).toHaveCount(0);
    }
    expect((await page.request.get(`${api}/cart`)).status()).toBe(401);
    expect((await page.request.get(`${api}/addresses`)).status()).toBe(401);
  });
  async function selectVariantAnonymous(page: Page) {
    await page.goto(`/urun/${stock12.productSlug}`);
    const box = page.getByTestId("signin-required");
    await expect(box).toBeVisible();
    await expect(box.getByRole("link", { name: "Giriş yap" })).toHaveAttribute("href", `/giris?next=${encodeURIComponent(`/urun/${stock12.productSlug}`)}`);
    await expect(page.getByRole("button", { name: "Sepete ekle" })).toHaveCount(0);
  }
});

test.describe("açık yönlendirme (next)", () => {
  const bad = ["/%09/evil", "/%2F%2F", "/%2f/evil.example", "/%5Cevil.example", "/%0d%0a/evil", "／／evil.example", "/／evil.example", "/giris?next=/giris", "/giris", "/kayit?next=/x", "//evil.example", "https://evil.example/x", "/\\evil.example", "\\\\evil.example", "/\t/evil.example", "javascript:alert(1)", "evil.example", "", "/ iç boşluk", "http://localhost:3200.evil.example"];
  test("safeNext yalnızca aynı site içi göreli yolları kabul eder", () => {
    for (const value of bad) expect(safeNext(value), JSON.stringify(value)).toBe("/hesabim");
    expect(safeNext(null)).toBe("/hesabim");
    expect(safeNext(undefined)).toBe("/hesabim");
    expect(safeNext("/sepet")).toBe("/sepet");
    expect(safeNext("/urun/ornek-urun")).toBe("/urun/ornek-urun");
    expect(safeNext("/arama?q=a&b=c#x")).toBe("/arama?q=a&b=c#x");
  });
  for (const next of ["//evil.example", "https://evil.example/x", "/\\evil.example"]) {
    test(`giriş sonrası ${JSON.stringify(next)} /hesabim'e düşer`, async ({ page, baseURL }) => {
      await signupUi(page, next);
      await expect(page).toHaveURL(/\/hesabim$/);
      expect(new URL(page.url()).origin).toBe(new URL(baseURL!).origin);
    });
  }
  test("güvenli next kabul edilir; giriş ve kayıt bağlantıları next'i korur", async ({ page }) => {
    await page.goto(`/giris?next=${encodeURIComponent("/sepet")}`);
    await expect(page.getByRole("link", { name: "Kayıt ol" })).toHaveAttribute("href", `/kayit?next=${encodeURIComponent("/sepet")}`);
    await signupUi(page, "/sepet");
    await expect(page).toHaveURL(/\/sepet$/);
    await expect(page.getByTestId("cart-empty")).toBeVisible();
  });
});

test.describe("boş sepet", () => {
  test("yeni kullanıcının sepeti boş; ürünlere yönlendirir; /checkout adres ekletir", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await page.goto("/sepet");
    await expect(page.getByTestId("cart-empty")).toContainText("Sepetin boş");
    await expect(page.getByRole("link", { name: "Ürünlere göz at" })).toHaveAttribute("href", "/#urunler");
    await expect(headerCount(page)).toHaveCount(0);
    await page.goto("/checkout");
    await expect(page.getByRole("heading", { level: 2, name: "Teslimat adresi ekle" })).toBeVisible();
    await expect(page.getByTestId("order-disabled")).toHaveAttribute("aria-disabled", "true");
  });
});

test.describe("gerçek akış: kayıt, ekle, adet, kaldır, yeniden ekle, adres, özet", () => {
  test.use({ viewport: { width: 1440, height: 900 } });
  test("tarayıcı → same-origin proxy → ASP.NET → PostgreSQL", async ({ page, context, baseURL }, info) => {
    const requests: Request[] = [];
    page.on("request", (r) => { if (r.url().includes("/api/backend/")) requests.push(r); });
    const origin = new URL(baseURL!).origin;
    const email = await signupUi(page);
    await expect(page).toHaveURL(/\/hesabim$/);
    await expect(page.locator("main")).toContainText(email);

    // Ekle
    await selectVariant(page, stock12);
    await expect(headerCount(page)).toHaveCount(0);
    const stepper = page.getByRole("group", { name: "Adet" });
    await stepper.getByRole("button", { name: "Adet: artır" }).click();
    await expect(stepper).toContainText("2");
    await page.getByRole("button", { name: "Sepete ekle" }).click();
    await expect(page.getByRole("status").filter({ hasText: "Sepete eklendi" })).toContainText("2 adet");
    await expect(headerCount(page)).toHaveText("2");
    await expect(page.locator("header").getByRole("link", { name: /^Sepetim ?, 2 ürün$/ })).toBeVisible();

    // Sepette: sunucu tutarları
    await page.getByRole("link", { name: "Sepete git" }).click();
    await expect(page).toHaveURL(/\/sepet$/);
    await expect(line(page)).toHaveCount(1);
    expect((await getCart(page.request)).subtotalMinor).toBe(stock12.priceMinor * 2);
    await expect(page.getByTestId("subtotal")).toHaveText(formatPriceMinor(stock12.priceMinor * 2));
    await expect(page.getByText("Henüz hesaplanmadı")).toBeVisible();
    await expect(page.getByTestId("cart-announce")).toHaveText("");

    // Adet değiştir
    await line(page).getByRole("button", { name: /adedi: artır/ }).click();
    await expect(page.getByTestId("subtotal")).toHaveText(formatPriceMinor(stock12.priceMinor * 3));
    await expect(page.getByTestId("cart-announce")).toContainText("olarak güncellendi");
    await expect(headerCount(page)).toHaveText("3");

    // Kaldır ve yeniden ekle
    await line(page).getByRole("button", { name: /satırını kaldır/ }).click();
    await expect(page.getByTestId("cart-empty")).toBeVisible();
    await expect(page.getByRole("heading", { level: 1, name: "Sepetim" })).toBeFocused();
    await expect(headerCount(page)).toHaveCount(0);
    await selectVariant(page, stock12);
    await page.getByRole("button", { name: "Sepete ekle" }).click();
    await expect(headerCount(page)).toHaveText("1");

    // Adres: ödeme öncesi sayfada satır içi kayıt
    await page.goto("/checkout");
    await expect(page.getByRole("heading", { level: 2, name: "Teslimat adresi ekle" })).toBeVisible();
    const form = page.locator("main form");
    await form.getByLabel("Adres adı").fill(goodAddress.label);
    await form.getByLabel("Alıcı adı soyadı").fill(goodAddress.recipientName);
    await form.getByLabel("Cep telefonu").fill("0532 123 45 67");
    await form.getByLabel("İl", { exact: true }).selectOption("İstanbul");
    await form.getByLabel("İlçe").fill(goodAddress.district);
    await form.getByLabel("Açık adres").fill(goodAddress.streetAddress);
    await form.getByLabel("Posta kodu (isteğe bağlı)").fill(goodAddress.postalCode);
    await form.getByRole("button", { name: "Adresi kaydet ve seç" }).click();

    // Özet: sunucu tutarları; ödeme/sipariş yok
    const preview = page.getByTestId("preview");
    await expect(preview).toBeVisible();
    await expect(page.getByRole("radio", { name: /Ev/ })).toBeChecked();
    await expect(page.getByTestId("preview-subtotal")).toHaveText(formatPriceMinor(stock12.priceMinor));
    await expect(page.getByTestId("preview-shipping")).toHaveText("Kargo henüz hesaplanmadı");
    await expect(preview).toHaveAttribute("data-can-proceed", "true");
    await expect(page.getByTestId("order-disabled")).toHaveAttribute("aria-disabled", "true");
    await expect(page.getByTestId("order-disabled")).toHaveText("Sipariş verme yakında");
    const body = (await page.locator("main").innerText()).toLowerCase();
    for (const word of ["kart numarası", "cvv", "son kullanma", "ödeme yap", "ödemeyi tamamla", "sipariş verildi", "sipariş alındı", "ödeme başarılı", "toplam tutar:", "ödenecek"]) expect(body, word).not.toContain(word);
    await expect(page.locator("main input[autocomplete^='cc-']")).toHaveCount(0);
    await expect(page.getByText("Kargo hesaplanmadığı için toplam tutar henüz belli değil.")).toBeVisible();

    // Ağ: yalnızca same-origin /api/backend, yazmalarda taze CSRF başlığı, kimlik çerezi HttpOnly
    expect(requests.length).toBeGreaterThan(5);
    for (const r of requests) expect(new URL(r.url()).origin, r.url()).toBe(origin);
    const writes = requests.filter((r) => ["POST", "PUT", "PATCH", "DELETE"].includes(r.method()) && !r.url().endsWith("/auth/csrf"));
    expect(writes.length).toBeGreaterThanOrEqual(6);
    for (const r of writes) expect(await r.headerValue("x-csrf-token"), `${r.method()} ${r.url()}`).toBeTruthy();
    expect(writes.some((r) => r.url().endsWith("/checkout/preview"))).toBe(true);
    const cookies = await context.cookies();
    const auth = cookies.find((c) => c.name === "ssc.auth");
    expect(auth?.httpOnly).toBe(true);
    expect(auth?.sameSite).toBe("Lax");
    expect(auth?.path).toBe("/");
    // Maskelenmeden kaydedilir: test HTTP üzerinde koşar, bu yüzden Secure/__Host- davranışı bu ortamda doğrulanamaz.
    info.annotations.push({ type: "cookie", description: `ssc.auth secure=${auth?.secure} domain=${auth?.domain} (http ortamı; Secure/__Host- doğrulanmadı)` });
    expect(await page.evaluate(() => document.cookie)).not.toMatch(/ssc\./);
    // Depolama gerçek anahtarlarıyla denetlenir: hiçbir anahtar ya da değer kişisel veri/kimlik/sepet içeriği taşımaz.
    const dump = await page.evaluate(() => ({ local: Object.entries(localStorage), session: Object.entries(sessionStorage) }));
    info.annotations.push({ type: "storage", description: `local keys=[${dump.local.map(([k]) => k)}] session keys=[${dump.session.map(([k]) => k)}]` });
    expect(JSON.stringify(dump)).not.toMatch(/example\.test|Kadıköy|Ayşe|Moda|5321234567|Browser-test|ssc\.|csrf|token|cart|sepet|adres/i);
    // Kişisel veri URL'de yok
    for (const r of requests) expect(decodeURIComponent(r.url())).not.toMatch(/Kadıköy|Ayşe|Moda|5321234567|@example\.test/);
    expect(page.url()).not.toMatch(/Kadıköy|Ayşe|Moda|5321234567/);
    await page.screenshot({ path: info.outputPath("akis-checkout-1440.png"), fullPage: true });
  });
});

test.describe("hata durumları", () => {
  test.use({ viewport: { width: 1440, height: 900 } });
  test("API hatası: /sepet yeniden dene ile toparlanır (rota iptali)", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await cartApi(page.request, baseURL!, stock12.id, 1);
    await page.route("**/api/backend/api/v1/cart", (route) => route.abort());
    await page.goto("/sepet");
    const error = page.getByTestId("cart-error");
    await expect(error).toBeVisible();
    await expect(error).toContainText("Sepet yüklenemedi");
    await page.unroute("**/api/backend/api/v1/cart");
    await error.getByRole("button", { name: "Tekrar dene" }).click();
    await expect(line(page)).toHaveCount(1);
    await expect(page.getByTestId("cart-error")).toHaveCount(0);
  });

  test("kurtarılabilir yazma hatası: ileti görünür, satır ve adet doğru kalır, sonraki deneme çalışır", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await cartApi(page.request, baseURL!, stock12.id, 2);
    await page.goto("/sepet");
    await expect(line(page)).toHaveCount(1);
    await page.route("**/api/backend/api/v1/cart/items/*", (route) => route.request().method() === "PATCH"
      ? route.fulfill({ status: 503, contentType: "application/problem+json", body: JSON.stringify({ title: "Sistem meşgul", code: "busy" }) })
      : route.continue());
    await line(page).getByRole("button", { name: /adedi: artır/ }).click();
    // 5xx: sonuç doğrulanamaz; sepet yeniden okunur ve "değişmedi" denmez.
    const failure = page.getByTestId("line-failure");
    await expect(failure).toContainText("doğrulanamadı");
    await expect(failure).not.toContainText("değişmedi");
    await expect(line(page).getByRole("group")).toContainText("2");
    expect((await getCart(page.request)).itemCount).toBe(2);
    await page.unroute("**/api/backend/api/v1/cart/items/*");
    await line(page).getByRole("button", { name: /adedi: artır/ }).click();
    await expect(line(page).getByRole("group")).toContainText("3");
    await expect(page.locator("main").getByRole("alert")).toHaveCount(0);
  });

  test("stoktan fazla ekleme sunucuda reddedilir ve ileti gösterilir", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await cartApi(page.request, baseURL!, stock3.id, 3);
    await selectVariant(page, stock3);
    await page.getByRole("button", { name: "Sepete ekle" }).click();
    await expect(page.locator("main").getByRole("alert")).toContainText("3 adet");
    expect((await getCart(page.request)).itemCount).toBe(3);
  });

  test("geçersiz adres: alan hataları, ilk geçersiz alana odak, kayıt oluşmaz", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await page.goto("/hesabim/adresler");
    await page.getByRole("button", { name: "Yeni adres ekle" }).click();
    const form = page.locator("main form");
    await form.getByRole("button", { name: "Adresi kaydet" }).click();
    await expect(form.getByLabel("Adres adı")).toHaveAttribute("aria-invalid", "true");
    await expect(form.getByLabel("Adres adı")).toBeFocused();
    for (const name of ["Alıcı adı soyadı", "Cep telefonu", "İlçe", "Açık adres"]) await expect(form.getByLabel(name)).toHaveAttribute("aria-invalid", "true");
    await expect(form.getByLabel("İl", { exact: true })).toHaveAttribute("aria-invalid", "true");
    await expect(page.locator("#yeni-label-error")).not.toBeEmpty();
    // Biçim hataları
    await form.getByLabel("Adres adı").fill("Ev");
    await form.getByLabel("Alıcı adı soyadı").fill("Ali Veli");
    await form.getByLabel("Cep telefonu").fill("12345");
    await form.getByLabel("İl", { exact: true }).selectOption("Ankara");
    await form.getByLabel("İlçe").fill("Çankaya");
    await form.getByLabel("Açık adres").fill("Bir sokak 1");
    await form.getByLabel("Posta kodu (isteğe bağlı)").fill("99999");
    await form.getByRole("button", { name: "Adresi kaydet" }).click();
    await expect(form.getByLabel("Cep telefonu")).toHaveAttribute("aria-invalid", "true");
    await expect(form.getByLabel("Cep telefonu")).toBeFocused();
    await expect(form.getByLabel("Posta kodu (isteğe bağlı)")).toHaveAttribute("aria-invalid", "true");
    await expect(form.getByLabel("Alıcı adı soyadı")).not.toHaveAttribute("aria-invalid", "true");
    expect(await (await page.request.get(`${api}/addresses`)).json()).toEqual([]);
    // Düzeltince kaydolur; ilk adres otomatik varsayılan
    await form.getByLabel("Cep telefonu").fill("+90 532 123 45 67");
    await form.getByLabel("Posta kodu (isteğe bağlı)").fill("06100");
    await form.getByRole("button", { name: "Adresi kaydet" }).click();
    await expect(page.getByTestId("address-item")).toHaveCount(1);
    await expect(page.getByTestId("address-item")).toContainText("Varsayılan");
    await expect(page.getByTestId("address-item")).toContainText("5321234567");
  });
});

test.describe("adres yönetimi ve kullanıcı yalıtımı", () => {
  test.use({ viewport: { width: 1440, height: 900 } });
  test("ekle, düzenle, varsayılan yap, sil (onay penceresi ve odak)", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await addressApi(page.request, baseURL!, { label: "Ev" });
    await addressApi(page.request, baseURL!, { label: "İş", district: "Çankaya", province: "Ankara", postalCode: "06100" });
    await page.goto("/hesabim/adresler");
    const items = page.getByTestId("address-item");
    await expect(items).toHaveCount(2);
    await expect(items.first()).toContainText("Ev");
    await expect(items.first()).toContainText("Varsayılan");
    // Varsayılan yap
    await page.getByRole("button", { name: "İş adresini varsayılan yap" }).click();
    await expect(items.first()).toContainText("İş");
    await expect(items.first()).toContainText("Varsayılan");
    // Düzenle
    await page.getByRole("button", { name: "Ev adresini düzenle" }).click();
    await page.getByLabel("İlçe").fill("Üsküdar");
    await page.getByRole("button", { name: "Değişiklikleri kaydet" }).click();
    await expect(items.filter({ hasText: "Ev" })).toContainText("Üsküdar");
    // Sil: Esc ile vazgeç, sonra onayla
    await page.getByRole("button", { name: "Ev adresini sil" }).click();
    const dialog = page.getByRole("dialog", { name: "Adres silinsin mi?" });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole("button", { name: "Vazgeç" })).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(items).toHaveCount(2);
    await page.getByRole("button", { name: "Ev adresini sil" }).click();
    await dialog.getByRole("button", { name: "Adresi sil" }).click();
    await expect(items).toHaveCount(1);
    await expect(page.getByRole("heading", { level: 1, name: "Adreslerim" })).toBeFocused();
    await expect(page.getByTestId("address-announce")).toContainText("silindi");
  });

  test("ikinci kullanıcı birincinin sepetini ve adreslerini göremez ve değiştiremez", async ({ browser, baseURL }) => {
    const a = await newUserContext(browser, baseURL!);
    const b = await newUserContext(browser, baseURL!);
    try {
      const added = await cartApi(a.context.request, baseURL!, stock12.id, 2);
      const address = await addressApi(a.context.request, baseURL!, { label: "Gizli ev" });
      const itemId = added.items[0].id;
      await a.page.goto("/sepet");
      await expect(line(a.page)).toHaveCount(1);

      await b.page.goto("/sepet");
      await expect(b.page.getByTestId("cart-empty")).toBeVisible();
      await expect(headerCount(b.page)).toHaveCount(0);
      await b.page.goto("/hesabim/adresler");
      await expect(b.page.getByTestId("addresses-empty")).toBeVisible();
      await expect(b.page.locator("main")).not.toContainText("Gizli ev");
      await b.page.goto("/checkout");
      await expect(b.page.getByRole("heading", { level: 2, name: "Teslimat adresi ekle" })).toBeVisible();

      expect((await getCart(b.context.request)).items).toEqual([]);
      expect((await send(b.context.request, "PATCH", `/cart/items/${itemId}`, baseURL!, { quantity: 5 })).status()).toBe(404);
      expect((await send(b.context.request, "DELETE", `/cart/items/${itemId}`, baseURL!)).status()).toBe(404);
      expect((await send(b.context.request, "PUT", `/addresses/${address.id}`, baseURL!, goodAddress)).status()).toBe(404);
      expect((await send(b.context.request, "POST", `/addresses/${address.id}/default`, baseURL!)).status()).toBe(404);
      expect((await send(b.context.request, "DELETE", `/addresses/${address.id}`, baseURL!)).status()).toBe(404);
      await cartApi(b.context.request, baseURL!, stock12.id, 1); // boş sepet 409 cart_empty verirdi; adres kontrolü ayrı sınanır
      expect((await send(b.context.request, "POST", "/checkout/preview", baseURL!, { addressId: address.id })).status()).toBe(404);
      // Birincinin verisi bozulmadı
      expect((await getCart(a.context.request)).itemCount).toBe(2);
      expect((await (await a.context.request.get(`${api}/addresses`)).json()).length).toBe(1);
    } finally { await a.context.close(); await b.context.close(); }
  });

  test("adres alanlarındaki HTML/XSS metinleri yalnızca metin olarak görünür", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await cartApi(page.request, baseURL!, stock12.id, 1);
    const evil = { label: "<img src=x onerror=\"window.__xss=1\">", recipientName: "<script>window.__xss=2</script>", district: "\"><svg onload=window.__xss=3>", streetAddress: "<b>kalın</b> &lt;i&gt; javascript:window.__xss=4" };
    await addressApi(page.request, baseURL!, evil);
    for (const route of ["/hesabim/adresler", "/checkout"]) {
      await page.goto(route);
      await expect(page.locator("main")).toContainText(evil.label);
      await expect(page.locator("main")).toContainText(evil.recipientName);
      await expect(page.locator("main")).toContainText(evil.streetAddress);
      expect(await page.evaluate(() => (window as unknown as { __xss?: number }).__xss)).toBeUndefined();
      await expect(page.locator("main img[src='x'], main svg[onload], main b")).toHaveCount(0);
    }
    await page.goto("/hesabim/adresler");
    await page.getByRole("button", { name: /adresini düzenle/ }).click();
    await expect(page.getByLabel("Adres adı")).toHaveValue(evil.label);
  });
});

test.describe("klavye ile sepet işlemi", () => {
  test.use({ viewport: { width: 1440, height: 900 } });
  test("yalnızca klavye: adedi artır, sonra satırı kaldır; odak başlığa döner", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await cartApi(page.request, baseURL!, stock12.id, 1);
    await page.goto("/sepet");
    await expect(line(page)).toHaveCount(1);
    const h1 = page.getByRole("heading", { level: 1, name: "Sepetim" });
    await h1.focus();
    const tabTo = async (matcher: RegExp) => {
      for (let i = 0; i < 40; i++) {
        await page.keyboard.press("Tab");
        const label = await page.evaluate(() => (document.activeElement as HTMLElement | null)?.getAttribute("aria-label") ?? "");
        if (matcher.test(label)) return;
      }
      throw new Error(`Tab ile ulaşılamadı: ${matcher}`);
    };
    await tabTo(/adedi: artır/);
    await page.keyboard.press("Enter");
    await expect(line(page).getByRole("group")).toContainText("2");
    await expect(page.getByTestId("cart-announce")).toContainText("2 olarak güncellendi");
    await tabTo(/satırını kaldır/);
    await page.keyboard.press("Space");
    await expect(page.getByTestId("cart-empty")).toBeVisible();
    await expect(h1).toBeFocused();
  });
});

for (const vp of viewports) {
  test.describe(`sepet, ödeme özeti ve adresler ${vp.width}px`, () => {
    test.use({ viewport: vp });
    test("taşma yok, axe serious/critical 0, ekran görüntüleri", async ({ page, baseURL }, info) => {
      await registerApi(page.request, baseURL!);
      await cartApi(page.request, baseURL!, stock12.id, 2);
      await cartApi(page.request, baseURL!, stock3.id, 1);
      await addressApi(page.request, baseURL!);
      for (const [name, route, ready] of [
        ["urun", `/urun/${stock12.productSlug}`, () => page.getByRole("radio").first()],
        ["sepet", "/sepet", () => line(page).first()],
        ["checkout", "/checkout", () => page.getByTestId("preview")],
        ["adresler", "/hesabim/adresler", () => page.getByTestId("address-item").first()],
      ] as const) {
        await page.goto(route);
        await expect(ready()).toBeVisible();
        await page.evaluate(() => document.fonts.ready);
        expect(await noHorizontalOverflow(page), `${name} taşma`).toBe(true);
        expect(await axeSerious(page), name).toEqual([]);
        const file = info.outputPath(`${name}-${vp.width}.png`);
        await page.screenshot({ path: file, fullPage: true });
        await info.attach(`${name}-${vp.width}`, { path: file, contentType: "image/png" });
      }
      // Mobilde sayaç hem üst çubukta hem alt gezintide, anlamlı adla
      await page.goto("/sepet");
      await expect(line(page).first()).toBeVisible();
      await expect(page.locator("header").getByRole("link", { name: /^Sepetim ?, 3 ürün$/ })).toBeVisible();
      if (vp.width < 768) {
        const nav = page.getByRole("navigation", { name: "Alt gezinti" });
        await expect(nav.getByRole("link", { name: /^Sepetim ?, 3 ürün$/ })).toBeVisible();
        const box = await nav.getByRole("link", { name: /Sepetim/ }).boundingBox();
        expect(box!.height).toBeGreaterThanOrEqual(44);
        expect(box!.width).toBeGreaterThanOrEqual(44);
        for (const button of await line(page).first().getByRole("button").all()) {
          const b = await button.boundingBox();
          expect(b!.height, await button.innerText()).toBeGreaterThanOrEqual(43.5);
          expect(b!.width).toBeGreaterThanOrEqual(43.5);
        }
      }
    });
  });
}

test.describe("teslimat adresi seçimi (iki adres)", () => {
  test.use({ viewport: { width: 1440, height: 900 } });
  test("varsayılan olmayan adres klavye ve tıklamayla seçilir; özet o adresle yeniden istenir; hata dürüst kalır", async ({ page, baseURL }) => {
    await registerApi(page.request, baseURL!);
    await cartApi(page.request, baseURL!, stock12.id, 1);
    const home = await addressApi(page.request, baseURL!, { label: "Ev" });
    const work = await addressApi(page.request, baseURL!, { label: "İş", recipientName: "Can Demir", province: "Ankara", district: "Çankaya", postalCode: "06100", streetAddress: "Gizli Sk. No 9" });
    const previews: { url: string; addressId: string }[] = [];
    const urls: string[] = [];
    page.on("request", (r) => {
      urls.push(r.url());
      if (r.method() === "POST" && r.url().endsWith("/checkout/preview")) previews.push({ url: r.url(), addressId: (r.postDataJSON() as { addressId: string }).addressId });
    });
    const summaryAddress = page.getByTestId("preview-address");
    const homeRadio = page.getByRole("radio", { name: /^Ev/ });
    const workRadio = page.getByRole("radio", { name: /^İş/ });

    await page.goto("/checkout");
    await expect(page.getByTestId("preview")).toBeVisible();
    await expect(homeRadio).toBeChecked();
    expect(previews.map((p) => p.addressId)).toEqual([home.id]); // varsayılan adres seçili gelir
    await expect(summaryAddress).toContainText("Teslimat: Ev");

    // Klavye: odak seçili radio'da, ok tuşu bir sonrakini seçer ve özet yeniden istenir
    await homeRadio.focus();
    await page.keyboard.press("ArrowDown");
    await expect(workRadio).toBeChecked();
    await expect(workRadio).toBeFocused();
    await expect(summaryAddress).toContainText("Teslimat: İş");
    await expect(summaryAddress).toContainText("Can Demir");
    await expect(summaryAddress).toContainText("Çankaya / Ankara");
    expect(previews.map((p) => p.addressId)).toEqual([home.id, work.id]);

    // Tıklama: ilk adrese dön, sonra tekrar ikinciye
    await homeRadio.check();
    await expect(summaryAddress).toContainText("Teslimat: Ev");
    await workRadio.click();
    await expect(summaryAddress).toContainText("Teslimat: İş");
    expect(previews.map((p) => p.addressId)).toEqual([home.id, work.id, home.id, work.id]);

    // Adres verisi URL'de yok: sayfa adresi sorgusuz, istek adresleri yalnızca sabit uç; kimlik gövdede
    expect(new URL(page.url()).search).toBe("");
    for (const p of previews) expect(new URL(p.url).pathname).toBe("/api/backend/api/v1/checkout/preview");
    for (const u of urls) expect(decodeURIComponent(u)).not.toMatch(/Can Demir|Çankaya|Gizli|06100|5321234567|Moda/);
    expect(urls.some((u) => u.includes(work.id) && u.includes("preview"))).toBe(false);

    // Başarısız özet: eski özet gösterilmez, hata dürüst, seçim korunur, sipariş kontrolü pasif; yeniden deneme aynı adresle gider
    await page.route("**/api/backend/api/v1/checkout/preview", (route) => route.fulfill({ status: 503, contentType: "application/problem+json", body: JSON.stringify({ title: "x", code: "service_unavailable" }) }));
    const before = previews.length;
    await homeRadio.check();
    const alert = page.locator("main").getByRole("alert");
    await expect(alert).toContainText("Hizmet şu an kullanılamıyor");
    await expect(page.getByTestId("preview")).toHaveCount(0);
    await expect(summaryAddress).toHaveCount(0);
    await expect(homeRadio).toBeChecked();
    await expect(page.getByTestId("order-disabled")).toHaveAttribute("aria-disabled", "true");
    expect(previews.slice(before).map((p) => p.addressId)).toEqual([home.id]);
    await page.unroute("**/api/backend/api/v1/checkout/preview");
    await alert.getByRole("button", { name: "Tekrar dene" }).click();
    await expect(summaryAddress).toContainText("Teslimat: Ev");
    expect(previews[previews.length - 1].addressId).toBe(home.id);
    await expect(homeRadio).toBeChecked();
  });
});

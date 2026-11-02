import { expect, test, type Page, type Request } from "@playwright/test";
import { formatPriceMinor } from "../../src/lib/format";
import {
  addressApi, api, availableOf, axeSerious, cartApi, fixture, getCart, goodAddress, INTENT_KEY, loadVariants, newUserContext, noHorizontalOverflow,
  orderSend, orderStatusFixture, ordersApi, password, placeOrderApi, registerApi, send, variantRow, viewports, type Variant,
} from "./support";

// FAZ 7: sipariş verme, listeleme, ayrıntı ve iptal. Gerçek tarayıcı -> Next proxy -> gerçek API -> gerçek PostgreSQL.
// Siparişler STOK TÜKETİR; bu yüzden dosya "ordering" projesinde (playwright.config.ts) çalışır: main ve mutating bittikten sonra, tek worker'da
// sırayla. Her test kullandığı varyantın stoğunu finally ile fixture kuralındaki ilk hâline döndürür, böylece paylaşılan katalog bozulmaz.
test.use({ viewport: { width: 1440, height: 900 } });

let variants: Variant[];
test.beforeAll(async ({ request }) => { variants = await loadVariants(request); });
// Her stok sınıfında az sayıda varyant var; testler sırayla koşup stoğu geri koyduğundan indeks sarılarak yeniden kullanılır.
const pick = (stock: number, i: number) => { const list = variants.filter((v) => v.stock === stock); return list[i % list.length]; };
const v12 = (i: number) => pick(12, i);
const v3 = (i: number) => pick(3, i);
const v50 = (i: number) => pick(50, i);
const restore = async (request: import("@playwright/test").APIRequestContext, ...list: Variant[]) => {
  for (const v of list) await fixture(request, "stock", { sku: v.sku, quantity: v.stock });
};

const UUID = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
const submit = (page: Page) => page.getByTestId("order-submit");
const ORDERS = "**/api/backend/api/v1/orders";
const isPlace = (r: Request) => r.method() === "POST" && new URL(r.url()).pathname.endsWith("/api/v1/orders");
const keyOf = (r: Request) => r.headers()["idempotency-key"];
function watchPlacements(page: Page) {
  const posts: Request[] = [];
  page.on("request", (r) => { if (isPlace(r)) posts.push(r); });
  return posts;
}
const stored = (page: Page) => page.evaluate((k) => window.sessionStorage.getItem(k), INTENT_KEY);
const orderId = (page: Page) => page.url().split("/").pop()!;

/** Yeni kullanıcı + sepet + adres. Sepet satırları [varyant, adet]. */
async function prepare(page: Page, baseURL: string, lines: [Variant, number][], addressOverrides: Partial<typeof goodAddress> = {}) {
  const email = await registerApi(page.request, baseURL);
  for (const [v, q] of lines) await cartApi(page.request, baseURL, v.id, q);
  const address = await addressApi(page.request, baseURL, addressOverrides);
  return { email, address };
}
async function openCheckout(page: Page) {
  await page.goto("/checkout");
  await expect(page.getByTestId("preview")).toBeVisible();
  await expect(submit(page)).toHaveAttribute("aria-disabled", "false");
}
async function openDetail(page: Page, id: string) {
  await page.goto(`/siparisler/${id}`);
  await expect(page.getByTestId("order-number")).toBeVisible();
}

test("sipariş akışı: checkout'tan sipariş ver, fiş ayrıntıda, sepet boşalır, stok düşer, kayıtlı anahtar temizlenir", async ({ page, request, baseURL }) => {
  const v = v12(0);
  try {
    const { email, address } = await prepare(page, baseURL!, [[v, 2]]);
    const posts = watchPlacements(page);
    await openCheckout(page);
    await expect(submit(page)).toHaveText("Sipariş oluştur — ödeme alınmaz");
    await expect(page.getByTestId("preview-payment")).toHaveText("Alınmaz");
    await expect(page.getByTestId("preview-shipping")).toHaveText("Kargo henüz hesaplanmadı");
    await expect(page.getByTestId("order-note")).toContainText("Ödeme alınmaz");
    await expect(page.locator("main")).not.toContainText("Sipariş verme ve ödeme henüz açık değil");
    expect(await stored(page)).toBeNull(); // önizleme anahtar üretmez: yalnız tıklama niyeti kaydeder

    const responsePromise = page.waitForResponse((r) => isPlace(r.request()));
    await submit(page).click();
    expect((await responsePromise).status()).toBe(201);
    await expect(page).toHaveURL(new RegExp(`/siparisler/${UUID}$`));
    await expect(page.getByTestId("order-number")).toHaveText(/^SS-\d{6}$/);

    // İstek: tek POST, anahtar biçimi, gövdede yalnız addressId + quote (fiyat/adet/kullanıcı/durum yok)
    expect(posts).toHaveLength(1);
    expect(keyOf(posts[0])).toMatch(/^[A-Za-z0-9_-]{16,128}$/);
    const body = posts[0].postDataJSON() as Record<string, string>;
    expect(Object.keys(body).sort()).toEqual(["addressId", "quote"]);
    expect(body.addressId).toBe(address.id);
    expect(body.quote).toMatch(/^[0-9a-f]{64}$/);
    expect(await stored(page)).toBeNull(); // başarıda anahtar silinir: sonraki niyet yeni anahtar alır

    // Fiş
    await expect(page.getByTestId("order-status")).toHaveText("Alındı");
    await expect(page.getByTestId("order-notice")).toHaveText("Siparişin alındı. Ödeme alınmadı.");
    await expect(page.getByTestId("order-unpaid")).toContainText("Ödeme alınmadı");
    await expect(page.getByTestId("order-unpaid")).toContainText("genel toplam belli değil");
    const item = page.getByTestId("order-item");
    await expect(item).toHaveCount(1);
    await expect(item).toContainText(v.productName);
    await expect(item).toContainText(v.sku);
    await expect(item).toContainText(`2 adet × ${formatPriceMinor(v.priceMinor)}`);
    await expect(page.getByTestId("order-subtotal")).toHaveText(formatPriceMinor(v.priceMinor * 2));
    await expect(page.getByTestId("order-shipping")).toHaveText("Henüz hesaplanmadı");
    await expect(page.getByTestId("order-address")).toContainText(goodAddress.recipientName);
    await expect(page.getByTestId("order-address")).toContainText(goodAddress.streetAddress);
    await expect(page.getByTestId("order-timeline").locator("li")).toHaveCount(1);
    await expect(page.getByTestId("order-cancel")).toBeVisible();
    await expect(page.locator("main")).not.toContainText(/genel toplam:|ödenecek tutar/i);

    // Sepet sunucuda boşaldı, üst sayaç güncellendi, stok yalnız bu siparişle düştü
    expect((await getCart(page.request)).itemCount).toBe(0);
    await expect(page.locator("header").getByTestId("cart-count")).toHaveCount(0);
    expect(await availableOf(request, v)).toBe(10);
    expect((await ordersApi(page.request)).totalCount).toBe(1);
    void email;

    // Değişmezlik: adres düzenlenir, fiyat artar; sipariş ilk hâliyle kalır
    const id = orderId(page);
    expect((await send(page.request, "PUT", `/addresses/${address.id}`, baseURL!, { ...goodAddress, recipientName: "Başka Kişi", streetAddress: "Değişen Sk. No 9" })).status()).toBe(200);
    await fixture(request, "price", { sku: v.sku, priceMinor: v.priceMinor + 5000 });
    await openDetail(page, id);
    await expect(page.getByTestId("order-address")).toContainText(goodAddress.recipientName);
    await expect(page.getByTestId("order-address")).not.toContainText("Başka Kişi");
    await expect(page.getByTestId("order-address")).toContainText(goodAddress.streetAddress);
    await expect(page.getByTestId("order-subtotal")).toHaveText(formatPriceMinor(v.priceMinor * 2));
    await expect(page.getByTestId("order-notice")).toHaveCount(0); // yenilemede "az önce verildi" bilgisi yok: yalnız sunucu verisi
  } finally {
    await fixture(request, "price", { sku: v.sku, priceMinor: v.priceMinor });
    await restore(request, v);
  }
});

test("liste: yeni kullanıcıda boş, 11 siparişte sayfalama, en yeni önce, ?page güvenilmez girdidir", async ({ page, browser, request, baseURL }) => {
  const v = v50(0);
  const fresh = await newUserContext(browser, baseURL!);
  try {
    // Boş durum ve anonim durum
    await fresh.page.goto("/siparisler");
    await expect(fresh.page.getByTestId("orders-empty")).toContainText("Henüz siparişin yok");
    const anon = await browser.newContext({ baseURL });
    const anonPage = await anon.newPage();
    await anonPage.goto("/siparisler?page=2");
    await expect(anonPage.getByTestId("orders-anonymous").getByRole("link", { name: "Giriş yap" })).toHaveAttribute("href", `/giris?next=${encodeURIComponent("/siparisler?page=2")}`);
    await anon.close();

    // 11 sipariş (sayfa boyutu 10)
    await registerApi(page.request, baseURL!);
    const address = await addressApi(page.request, baseURL!);
    const numbers: string[] = [];
    for (let i = 0; i < 11; i++) {
      await cartApi(page.request, baseURL!, v.id, 1);
      numbers.push((await placeOrderApi(page.request, baseURL!, address.id)).orderNumber);
    }
    await page.goto("/siparisler");
    const rows = page.getByTestId("order-row");
    await expect(rows).toHaveCount(10);
    await expect(rows.first().locator("h2")).toContainText(numbers[10]); // en yeni önce
    await expect(rows.first()).toContainText(v.productName);
    await expect(rows.first().getByTestId("order-status")).toHaveText("Alındı");
    await expect(rows.first()).toContainText(formatPriceMinor(v.priceMinor));
    await expect(page.getByTestId("orders-pager")).toContainText("Sayfa 1 / 2");
    await expect(page.getByRole("link", { name: "Önceki sayfa" })).toHaveCount(0);
    await page.getByRole("link", { name: "Sonraki sayfa" }).click();
    await expect(page).toHaveURL(/\/siparisler\?page=2$/);
    await expect(rows).toHaveCount(1);
    await expect(rows.first().locator("h2")).toContainText(numbers[0]);
    await expect(page.getByTestId("orders-pager")).toContainText("Sayfa 2 / 2");

    // Satıra tıklayınca ayrıntı açılır (kartın tamamı bağlantı)
    await rows.first().click({ position: { x: 20, y: 20 } });
    await expect(page).toHaveURL(new RegExp(`/siparisler/${UUID}$`));
    await expect(page.getByTestId("order-number")).toHaveText(numbers[0]);

    // Güvenilmez ?page=: bozuk değer 1. sayfa, ölçü dışı ve aşırı değer dürüst durum
    for (const bad of ["abc", "-1", "0", "1.5", "99999999999"]) {
      await page.goto(`/siparisler?page=${bad}`);
      await expect(rows).toHaveCount(10);
    }
    await page.goto("/siparisler?page=500");
    await expect(page.getByTestId("orders-past-end")).toContainText("Bu sayfada sipariş yok");
    await page.getByRole("link", { name: "İlk sayfaya dön" }).click();
    await expect(rows).toHaveCount(10);
  } finally {
    await fresh.context.close();
    await restore(request, v);
  }
});

test("iptal: onay penceresi, vazgeç, onayla; sipariş iptal olur ve ürün sayfasında stok geri döner; tekrar iptal stoğu iki kez iade etmez", async ({ page, request, baseURL }) => {
  const v = v3(0);
  try {
    const { address } = await prepare(page, baseURL!, [[v, 3]]);
    const order = await placeOrderApi(page.request, baseURL!, address.id);
    await page.goto(`/urun/${v.productSlug}`);
    await expect(variantRow(page, v).locator("[data-stock]")).toHaveText("Stokta yok"); // 3 adedin hepsi siparişte
    await openDetail(page, order.id);
    await expect(page.getByTestId("order-status")).toHaveText("Alındı");

    const cancel = page.getByTestId("order-cancel");
    await cancel.click();
    const dialog = page.getByRole("dialog", { name: "Sipariş iptal edilsin mi?" });
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText(order.orderNumber);
    await dialog.getByRole("button", { name: "Vazgeç" }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByTestId("order-status")).toHaveText("Alındı"); // vazgeçmek hiçbir şey değiştirmez
    expect(await availableOf(request, v)).toBe(0);

    await cancel.click();
    const cancels: string[] = [];
    page.on("request", (r) => { if (r.method() === "POST" && r.url().endsWith("/cancel")) cancels.push(r.url()); });
    await page.getByRole("dialog").getByRole("button", { name: "Siparişi iptal et" }).click();
    await expect(page.getByTestId("order-status")).toHaveText("İptal edildi");
    await expect(page.getByTestId("order-notice")).toHaveText("Siparişin iptal edildi. Ürünler stoğa geri döndü.");
    await expect(cancel).toHaveCount(0);
    await expect(page.getByTestId("order-timeline").locator("li")).toHaveCount(2);
    await expect(page.getByTestId("order-timeline").locator("li").last()).toContainText("İptal edildi");
    expect(cancels).toHaveLength(1);

    // Ürün sayfası: stok geri geldi
    await page.goto(`/urun/${v.productSlug}`);
    await expect(variantRow(page, v).locator("[data-stock]")).toHaveText("Son 3 adet");
    expect(await availableOf(request, v)).toBe(3);

    // Tekrar iptal (kayıp yanıt sonrası yeniden deneme gibi): 200 replayed, stok 3'te kalır (6 olmaz)
    const again = await orderSend(page.request, baseURL!, `/orders/${order.id}/cancel`);
    expect(again.status()).toBe(200);
    expect((await again.json()).replayed).toBe(true);
    expect(await availableOf(request, v)).toBe(3);
    // İptal edilmiş siparişte iptal seçeneği ve "iptal edilemez" notu yok
    await openDetail(page, order.id);
    await expect(page.getByTestId("order-cancel")).toHaveCount(0);
    await expect(page.getByTestId("order-no-cancel")).toHaveCount(0);
  } finally { await restore(request, v); }
});

test("iptal seçeneği yalnız sunucu canCancel dediğinde var; durum fixture ile ilerleyince gizlenir; eski sayfadan iptal 409 olur ve açıklanır", async ({ page, request, baseURL }) => {
  const v = v12(1);
  try {
    const { address } = await prepare(page, baseURL!, [[v, 1]]);
    const order = await placeOrderApi(page.request, baseURL!, address.id);
    await openDetail(page, order.id);
    await expect(page.getByTestId("order-cancel")).toBeVisible();

    // Sayfa açıkken sipariş işleme alınır: eski ekrandan iptal denenir
    await orderStatusFixture(request, order.id, "processing");
    await page.getByTestId("order-cancel").click();
    await page.getByRole("dialog").getByRole("button", { name: "Siparişi iptal et" }).click();
    await expect(page.getByTestId("order-notice")).toContainText("artık iptal edilemiyor");
    await expect(page.getByTestId("order-status")).toHaveText("Hazırlanıyor");
    await expect(page.getByTestId("order-cancel")).toHaveCount(0);
    await expect(page.getByTestId("order-no-cancel")).toBeVisible();
    expect(await availableOf(request, v)).toBe(11); // reddedilen iptal stok iade etmedi

    await orderStatusFixture(request, order.id, "shipped");
    await openDetail(page, order.id);
    await expect(page.getByTestId("order-status")).toHaveText("Kargoda");
    await expect(page.getByTestId("order-cancel")).toHaveCount(0);
    await orderStatusFixture(request, order.id, "completed");
    await openDetail(page, order.id);
    await expect(page.getByTestId("order-status")).toHaveText("Tamamlandı");
    const steps = page.getByTestId("order-timeline").locator("li");
    await expect(steps).toHaveCount(4);
    await expect(steps).toContainText(["Alındı", "Hazırlanıyor", "Kargoda", "Tamamlandı"]);
    await expect(steps.last()).toHaveAttribute("aria-current", "step");
    // API de aynı: terminal durumda iptal 409
    const response = await orderSend(page.request, baseURL!, `/orders/${order.id}/cancel`);
    expect(response.status()).toBe(409);
    expect((await response.json()).code).toBe("order_transition_invalid");
  } finally { await restore(request, v); }
});

test("fiyat önizleme ile tıklama arasında değişir: sipariş verilmez, ne değiştiği söylenir, yeniden onayla yeni anahtar ve yeni fiyatla verilir", async ({ page, request, baseURL }) => {
  const v = v12(2);
  const raised = v.priceMinor + 12345;
  try {
    const { address } = await prepare(page, baseURL!, [[v, 2]]);
    const posts = watchPlacements(page);
    await openCheckout(page);
    await fixture(request, "price", { sku: v.sku, priceMinor: raised });
    await submit(page).click();

    const problem = page.getByTestId("order-problem");
    await expect(problem).toContainText("Fiyatlar ya da sepet değişti");
    await expect(problem).toContainText("Sipariş verilmedi");
    await expect(page.getByTestId("order-changes")).toContainText(`birim fiyat ${formatPriceMinor(v.priceMinor)} iken ${formatPriceMinor(raised)} oldu`);
    await expect(page.getByTestId("order-changes")).toContainText(`Ara toplam ${formatPriceMinor(v.priceMinor * 2)} iken ${formatPriceMinor(raised * 2)} oldu`);
    expect(posts).toHaveLength(1);
    expect((await ordersApi(page.request)).totalCount).toBe(0); // hiçbir şey yazılmadı
    expect((await getCart(page.request)).itemCount).toBe(2); // sepet duruyor
    await expect(page.getByTestId("preview-subtotal")).toHaveText(formatPriceMinor(raised * 2));
    expect(posts).toHaveLength(1); // otomatik yeniden gönderim yok: taze tıklama gerekir
    await expect(submit(page)).toHaveAttribute("aria-disabled", "false");
    expect(await axeSerious(page)).toEqual([]);

    // Yeni quote = yeni niyet = yeni anahtar
    await submit(page).click();
    await expect(page).toHaveURL(new RegExp(`/siparisler/${UUID}$`));
    expect(posts).toHaveLength(2);
    expect(keyOf(posts[1])).not.toBe(keyOf(posts[0]));
    expect(posts[1].postDataJSON().quote).not.toBe(posts[0].postDataJSON().quote);
    await expect(page.getByTestId("order-subtotal")).toHaveText(formatPriceMinor(raised * 2));
    await expect(page.getByTestId("order-item")).toContainText(`2 adet × ${formatPriceMinor(raised)}`);
    void address;
  } finally {
    await fixture(request, "price", { sku: v.sku, priceMinor: v.priceMinor });
    await restore(request, v);
  }
});

test("tıklama anında stok yetmez: satır adıyla uyarı, /sepet bağlantısı, sepet ve stok dokunulmaz; stok gelince aynı anahtarla sipariş olur", async ({ page, request, baseURL }) => {
  const a = v12(3);
  const b = v50(1);
  try {
    await prepare(page, baseURL!, [[a, 3], [b, 1]]);
    await openCheckout(page);
    await fixture(request, "stock", { sku: a.sku, quantity: 1 });
    await submit(page).click();

    const problem = page.getByTestId("order-problem");
    await expect(problem).toContainText("yeterli stok yok");
    await expect(page.getByTestId("order-problem-lines")).toContainText(`${a.productName}, ${a.displayName}`);
    await expect(page.getByTestId("order-problem-lines")).not.toContainText(b.productName); // yalnız eksik satır
    await expect(problem.getByRole("link", { name: "Sepete dön" })).toHaveAttribute("href", "/sepet");
    expect((await ordersApi(page.request)).totalCount).toBe(0);
    const cart = await getCart(page.request);
    expect(cart.items.map((i) => i.quantity).sort()).toEqual([1, 3]); // sepet olduğu gibi
    expect(await availableOf(request, b)).toBe(20); // b'nin stoğuna dokunulmadı (görünür sınır 20)
    // Özet yenilendi: devam edilemez, sipariş düğmesi kapalı
    await expect(page.getByTestId("preview")).toHaveAttribute("data-can-proceed", "false");
    await expect(submit(page)).toHaveAttribute("aria-disabled", "true");
    await submit(page).click({ force: true }); // kapalı düğme istek göndermez
    expect((await ordersApi(page.request)).totalCount).toBe(0);

    // Stok geri gelince sepet yenilenir ve sipariş verilir
    await fixture(request, "stock", { sku: a.sku, quantity: a.stock });
    await page.reload();
    await expect(page.getByTestId("preview")).toHaveAttribute("data-can-proceed", "true");
    await submit(page).click();
    await expect(page).toHaveURL(new RegExp(`/siparisler/${UUID}$`));
  } finally { await restore(request, a, b); }
});

test("pasif ürün: satır adıyla uyarı, sepet dokunulmaz, sipariş yazılmaz", async ({ page, request, baseURL }) => {
  const a = v12(4);
  const b = v50(2);
  try {
    await prepare(page, baseURL!, [[a, 1], [b, 2]]);
    await openCheckout(page);
    await fixture(request, "active", { sku: b.sku, isActive: false });
    await submit(page).click();
    await expect(page.getByTestId("order-problem")).toContainText("artık satışta değil");
    await expect(page.getByTestId("order-problem-lines")).toContainText(b.productName);
    await expect(page.getByTestId("order-problem-lines")).not.toContainText(a.productName);
    await expect(page.getByTestId("order-problem").getByRole("link", { name: "Sepete dön" })).toHaveAttribute("href", "/sepet");
    expect((await ordersApi(page.request)).totalCount).toBe(0);
    expect((await getCart(page.request)).items).toHaveLength(2);
    expect(await axeSerious(page)).toEqual([]);
  } finally {
    await fixture(request, "active", { sku: b.sku, isActive: true });
    await restore(request, a, b);
  }
});

test("çift tıklama ve art arda Enter tek sipariş yaratır (senkron kilit)", async ({ page, request, baseURL }) => {
  const v = v12(5);
  try {
    await prepare(page, baseURL!, [[v, 1]]);
    const posts = watchPlacements(page);
    // Yanıtı geciktir: ikinci tıklama istek uçuştayken gelir
    await page.route(ORDERS, async (route) => {
      if (route.request().method() !== "POST") return route.continue();
      await new Promise((resolve) => setTimeout(resolve, 600));
      return route.continue();
    });
    await openCheckout(page);
    await submit(page).dblclick();
    await submit(page).press("Enter").catch(() => { /* gezinme başlamış olabilir */ });
    await expect(page).toHaveURL(new RegExp(`/siparisler/${UUID}$`));
    expect(posts).toHaveLength(1);
    const list = await ordersApi(page.request);
    expect(list.totalCount).toBe(1);
    expect(await availableOf(request, v)).toBe(11);
  } finally { await restore(request, v); }
});

test("yanıt kayboldu: 'sonuç bilinmiyor', başarısız denmez; aynı anahtarla yeniden gönderim ikinci sipariş yaratmaz", async ({ page, request, baseURL }) => {
  const v = v12(6);
  try {
    await prepare(page, baseURL!, [[v, 2]]);
    const posts = watchPlacements(page);
    let lost = false;
    await page.route(ORDERS, async (route) => {
      if (route.request().method() !== "POST" || lost) return route.continue();
      lost = true;
      await route.fetch(); // sunucu siparişi İŞLER...
      await route.abort("failed"); // ...ama yanıt tarayıcıya ulaşmaz
    });
    await openCheckout(page);
    await submit(page).click();

    const unknown = page.getByTestId("order-unknown");
    await expect(unknown).toContainText("sonucu bilinmiyor");
    await expect(unknown).toContainText("sipariş oluşmuş olabilir");
    await expect(page.locator("main")).not.toContainText(/sipariş verilemedi|başarısız/i);
    await expect(submit(page)).toHaveCount(0); // yeni anahtar üretecek ana düğme kapıda gizli
    expect((await ordersApi(page.request)).totalCount).toBe(1); // gerçekte oluştu
    expect(await axeSerious(page)).toEqual([]);
    const first = keyOf(posts[0]);
    const record = JSON.parse((await stored(page))!) as { key: string; unknown: boolean };
    expect(record.key).toBe(first);
    expect(record.unknown).toBe(true);

    await page.getByTestId("order-retry").click();
    await expect(page).toHaveURL(new RegExp(`/siparisler/${UUID}$`));
    expect(posts).toHaveLength(2);
    expect(keyOf(posts[1])).toBe(first); // AYNI anahtar
    expect(posts[1].postDataJSON()).toEqual(posts[0].postDataJSON());
    await expect(page.getByTestId("order-notice")).toContainText("daha önce oluşturulmuştu");
    expect((await ordersApi(page.request)).totalCount).toBe(1); // hâlâ tek sipariş
    expect(await availableOf(request, v)).toBe(10); // stok tek kez düştü
    expect(await stored(page)).toBeNull();
  } finally { await restore(request, v); }
});

test("sonuç bilinmezken sayfa yenilenir: aynı anahtar kullanılır, sepet boş olsa da asıl sipariş açılır", async ({ page, request, baseURL }) => {
  const v = v12(7);
  try {
    await prepare(page, baseURL!, [[v, 1]]);
    const posts = watchPlacements(page);
    let lost = false;
    await page.route(ORDERS, async (route) => {
      if (route.request().method() !== "POST" || lost) return route.continue();
      lost = true;
      await route.fetch();
      await route.abort("connectionreset");
    });
    await openCheckout(page);
    await submit(page).click();
    await expect(page.getByTestId("order-unknown")).toBeVisible();
    const first = keyOf(posts[0]);

    await page.reload(); // sepet artık boş; yine de kapı açılır
    await expect(page.getByTestId("order-unknown")).toBeVisible();
    await expect(page.getByTestId("checkout-empty")).toBeVisible(); // "sepet boş" tek başına başarısızlık iddiası değildir
    expect((JSON.parse((await stored(page))!) as { key: string }).key).toBe(first);
    await page.getByTestId("order-retry").click();
    await expect(page).toHaveURL(new RegExp(`/siparisler/${UUID}$`));
    expect(keyOf(posts[posts.length - 1])).toBe(first);
    expect((await ordersApi(page.request)).totalCount).toBe(1);
    expect(await availableOf(request, v)).toBe(11);
  } finally { await restore(request, v); }
});

test("istek sunucuya hiç ulaşmadı + zaman aşımı: sonuç bilinmiyor denir, yeniden deneme AYNI anahtarla siparişi oluşturur (tek sipariş)", async ({ page, request, baseURL }) => {
  test.setTimeout(90_000);
  const v = v12(8);
  try {
    await prepare(page, baseURL!, [[v, 1]]);
    const posts = watchPlacements(page);
    let hung = false;
    await page.route(ORDERS, async (route) => {
      if (route.request().method() !== "POST" || hung) return route.continue();
      hung = true; // yanıt hiç gelmez: istemci zaman aşımı devreye girer
    });
    await openCheckout(page);
    await submit(page).click();
    await expect(page.getByTestId("order-unknown")).toBeVisible({ timeout: 25_000 });
    await expect(page.locator("main")).not.toContainText(/sipariş verilemedi/i);
    expect((await ordersApi(page.request)).totalCount).toBe(0); // sunucu bu isteği hiç görmedi
    await page.getByTestId("order-retry").click();
    await expect(page).toHaveURL(new RegExp(`/siparisler/${UUID}$`));
    expect(keyOf(posts[1])).toBe(keyOf(posts[0]));
    await expect(page.getByTestId("order-notice")).toHaveText("Siparişin alındı. Ödeme alınmadı.");
    expect((await ordersApi(page.request)).totalCount).toBe(1);
  } finally { await restore(request, v); }
});

test("kontrollü sunucu yanıtları (route ile taklit): 503 busy aynı anahtarla, 500 bilinmiyor, 429 Retry-After, 403, key_reused, adres yok", async ({ page, request, baseURL }) => {
  const v = v12(9);
  try {
    await prepare(page, baseURL!, [[v, 1]]);
    const posts = watchPlacements(page);
    const queue: Array<{ status: number; code: string; headers?: Record<string, string>; extra?: Record<string, unknown> }> = [];
    await page.route(ORDERS, async (route) => {
      const next = route.request().method() === "POST" ? queue.shift() : undefined;
      if (!next) return route.continue();
      return route.fulfill({ status: next.status, contentType: "application/problem+json", headers: next.headers, body: JSON.stringify({ title: "x", code: next.code, ...next.extra }) });
    });
    await openCheckout(page);
    const problem = page.getByTestId("order-problem");

    // 503 busy: hiçbir şey yazılmadı, bilinmeyen değil; aynı anahtarla tekrar
    queue.push({ status: 503, code: "busy", headers: { "Retry-After": "1" } });
    await submit(page).click();
    await expect(problem).toContainText("meşgul");
    await expect(page.getByTestId("order-unknown")).toHaveCount(0);
    const k1 = keyOf(posts[0]);

    // 429: Retry-After kadar düğme kapalı, sonra açılır
    queue.push({ status: 429, code: "rate_limited", headers: { "Retry-After": "2" } });
    await submit(page).click();
    await expect(problem).toContainText("Çok fazla istek");
    await expect(submit(page)).toHaveAttribute("aria-disabled", "true");
    await expect(submit(page)).toHaveText(/sn sonra tekrar dene/);
    await expect(submit(page)).toHaveAttribute("aria-disabled", "false", { timeout: 8_000 });
    expect(keyOf(posts[1])).toBe(k1);

    // 403: güvenlik doğrulaması
    queue.push({ status: 403, code: "csrf_invalid" });
    await submit(page).click();
    await expect(problem).toContainText("Güvenlik doğrulaması geçersiz");
    await expect(problem.getByRole("button", { name: "Sayfayı yenile" })).toBeVisible();

    // adres yok: adres listesi yenilenir
    queue.push({ status: 404, code: "address_not_found" });
    await submit(page).click();
    await expect(problem).toContainText("Teslimat adresi bulunamadı");

    // key_reused: kayıtlı anahtar atılır, yenisi üretilir
    await expect(submit(page)).toHaveAttribute("aria-disabled", "false");
    queue.push({ status: 409, code: "idempotency_key_reused" });
    await submit(page).click();
    await expect(problem).toContainText("Sipariş denemesi yenilendi");
    expect(await stored(page)).toBeNull();
    await expect(submit(page)).toHaveAttribute("aria-disabled", "false");

    // 500: bilinmiyor kapısı; aynı anahtarla yeniden gönderim gerçek sunucuya gider ve siparişi oluşturur
    queue.push({ status: 500, code: "http_500" });
    await submit(page).click();
    await expect(page.getByTestId("order-unknown")).toBeVisible();
    const kUnknown = (JSON.parse((await stored(page))!) as { key: string }).key;
    await page.getByTestId("order-retry").click();
    await expect(page).toHaveURL(new RegExp(`/siparisler/${UUID}$`));
    expect(keyOf(posts[posts.length - 1])).toBe(kUnknown);
    expect((await ordersApi(page.request)).totalCount).toBe(1);
  } finally { await restore(request, v); }
});

test("gerçek sunucu: sepet boşalmışsa cart_empty 'sipariş oluşmadı' der ve Siparişlerim'e yönlendirir", async ({ page, request, baseURL }) => {
  const v = v12(10);
  try {
    await prepare(page, baseURL!, [[v, 1]]);
    await openCheckout(page);
    const cart = await getCart(page.request);
    expect((await send(page.request, "DELETE", `/cart/items/${cart.items[0].id}`, baseURL!)).status()).toBe(200); // başka sekmede boşaltıldı
    await submit(page).click();
    const problem = page.getByTestId("order-problem");
    await expect(problem).toContainText("Sepetin boş, sipariş oluşmadı");
    await expect(problem.getByRole("link", { name: "Siparişlerim" })).toHaveAttribute("href", "/siparisler");
    await expect(page.getByTestId("checkout-empty")).toBeVisible();
    expect((await ordersApi(page.request)).totalCount).toBe(0);
    await expect(page.getByTestId("order-unknown")).toHaveCount(0);
  } finally { await restore(request, v); }
});

test("oturum sona erdi: 401 dürüst mesaj ve giriş bağlantısı; girişten sonra checkout'a dönülür, sepet yerinde", async ({ page, request, baseURL }) => {
  const v = v12(11);
  try {
    const { email } = await prepare(page, baseURL!, [[v, 1]]);
    await openCheckout(page);
    await page.context().clearCookies(); // oturum çerezi gitti
    await submit(page).click();
    const problem = page.getByTestId("order-problem");
    await expect(problem).toContainText("Oturumun sona erdi");
    await expect(problem).toContainText("Sipariş verilmedi");
    await expect(page.getByTestId("order-unknown")).toHaveCount(0);
    const login = problem.getByRole("link", { name: "Giriş yap" });
    await expect(login).toHaveAttribute("href", `/giris?next=${encodeURIComponent("/checkout")}`);
    await login.click();
    await page.getByLabel("E-posta", { exact: true }).fill(email);
    await page.getByLabel("Parola", { exact: true }).fill(password);
    await page.getByRole("button", { name: "Giriş yap", exact: true }).click();
    await expect(page).toHaveURL(/\/checkout$/);
    await expect(page.getByTestId("preview")).toBeVisible();
    await expect(page.getByTestId("preview-line")).toHaveCount(1);
    expect((await ordersApi(page.request)).totalCount).toBe(0);
  } finally { await restore(request, v); }
});

test("başkasının siparişi: sahibi görür; diğer kullanıcı ve rastgele kimlik aynı 'bulunamadı' görünümü ve aynı API gövdesi; anonim giriş ister", async ({ page, browser, request, baseURL }) => {
  const v = v12(12);
  const other = await newUserContext(browser, baseURL!);
  try {
    const { address } = await prepare(page, baseURL!, [[v, 1]]);
    const order = await placeOrderApi(page.request, baseURL!, address.id);
    await openDetail(page, order.id);

    await other.page.goto(`/siparisler/${order.id}`);
    await expect(other.page.getByTestId("order-not-found")).toContainText("yok ya da bu hesaba ait değil");
    await expect(other.page.locator("main")).not.toContainText(order.orderNumber);
    await expect(other.page.locator("main")).not.toContainText(goodAddress.recipientName);
    await other.page.goto("/siparisler/00000000-0000-7000-8000-000000000000");
    await expect(other.page.getByTestId("order-not-found")).toBeVisible();
    await other.page.goto("/siparisler/bozuk-kimlik");
    await expect(other.page.getByTestId("order-not-found")).toBeVisible();
    await other.page.goto("/siparisler");
    await expect(other.page.getByTestId("orders-empty")).toBeVisible();

    const theirs = await other.context.request.get(`${api}/orders/${order.id}`);
    const random = await other.context.request.get(`${api}/orders/00000000-0000-7000-8000-000000000000`);
    expect(theirs.status()).toBe(404);
    expect(random.status()).toBe(404);
    const [a, b] = [await theirs.json(), await random.json()];
    expect({ code: a.code, title: a.title, status: a.status }).toEqual({ code: b.code, title: b.title, status: b.status });
    // başkasının siparişini iptal etmek de aynı 404
    const cancel = await orderSend(other.context.request, baseURL!, `/orders/${order.id}/cancel`);
    expect(cancel.status()).toBe(404);
    expect((await ordersApi(page.request)).items[0].status).toBe("placed");

    // anonim
    const anon = await browser.newContext({ baseURL });
    const anonPage = await anon.newPage();
    await anonPage.goto(`/siparisler/${order.id}`);
    await expect(anonPage.getByTestId("order-anonymous")).toBeVisible();
    await expect(anonPage.locator("main")).not.toContainText(order.orderNumber);
    await anon.close();
  } finally { await other.context.close(); await restore(request, v); }
});

test("sipariş kişisel verisi ham HTML'de ve RSC yükünde yok; sayfalar private/no-store; yanıtlar da", async ({ page, request, baseURL }) => {
  const v = v12(13);
  const pii = { recipientName: "Gizli Alıcı Kişisi", streetAddress: "Gizli Mahallesi Saklı Sk. No 77", phone: "5329998877", label: "GizliAdresEtiketi" };
  try {
    const { address } = await prepare(page, baseURL!, [[v, 1]], pii);
    const order = await placeOrderApi(page.request, baseURL!, address.id);
    // Tarayıcının kendi gezintisi sırasında gelen tüm HTML/RSC yanıtları (Link önbelleğe alma dahil) toplanır
    const bodies: { url: string; text: string }[] = [];
    page.on("response", (response) => {
      const url = response.url();
      if (url.includes("/api/backend/") || url.includes("/_next/static/")) return;
      const type = response.headers()["content-type"] ?? "";
      if (type.includes("text/html") || type.includes("text/x-component")) void response.text().then((text) => bodies.push({ url, text })).catch(() => {});
    });
    await page.goto("/hesabim");
    await page.getByRole("link", { name: "Siparişlerim" }).click();
    await expect(page.getByTestId("order-row")).toHaveCount(1);
    await page.getByTestId("order-link").click();
    await expect(page.getByTestId("order-address")).toContainText(pii.recipientName); // tarayıcıda görünür...
    await page.reload();
    await expect(page.getByTestId("order-address")).toContainText(pii.recipientName);

    // ...ama sunucunun gönderdiği HTML'de ve RSC yükünde YOK (oturum çerezi gönderilse bile). page.request çerezleri taşır.
    const raw: { url: string; text: string }[] = [];
    for (const path of [`/siparisler/${order.id}`, "/siparisler", "/siparisler?page=1", "/checkout"]) {
      for (const headers of [{}, { RSC: "1" }] as Record<string, string>[]) {
        let response = await page.request.get(path, { headers, maxRedirects: 0 });
        // RSC isteği önce "?_rsc=..." adresine yönlendirilir (Next); yönlendirmeyi aynı başlıklarla elle izle.
        if (response.status() === 307) response = await page.request.get(response.headers()["location"], { headers, maxRedirects: 0 });
        expect(response.status(), `${path} ${JSON.stringify(headers)}`).toBe(200);
        expect(response.headers()["cache-control"], path).toMatch(/no-store/);
        raw.push({ url: `${path} ${JSON.stringify(headers)}`, text: await response.text() });
      }
    }
    expect(bodies.length).toBeGreaterThan(2);
    for (const { url, text } of [...raw, ...bodies]) {
      for (const secret of [pii.recipientName, pii.streetAddress, pii.phone, pii.label, order.orderNumber, v.productName, v.sku, "Ödeme alınmadı. Bu sipariş"]) {
        expect(text, `${url} içinde ${secret}`).not.toContain(secret);
      }
    }
    // API yanıtları no-store
    for (const path of ["/orders", `/orders/${order.id}`]) {
      const response = await page.request.get(`${api}${path}`);
      expect(response.headers()["cache-control"], path).toMatch(/no-store/);
    }
    const write = await orderSend(page.request, baseURL!, "/orders", { addressId: address.id, quote: "0".repeat(64) }, "x".repeat(20));
    expect(write.headers()["cache-control"]).toMatch(/no-store/);
  } finally { await restore(request, v); }
});

test("iptal isteğinin yanıtı kaybolur: sonuç doğrulanamadı denir, güncel durum yüklenir, stok bir kez iade edilmiş olur", async ({ page, request, baseURL }) => {
  const v = v3(1);
  try {
    const { address } = await prepare(page, baseURL!, [[v, 2]]);
    const order = await placeOrderApi(page.request, baseURL!, address.id);
    expect(await availableOf(request, v)).toBe(1);
    await openDetail(page, order.id);
    let lost = false;
    await page.route("**/api/backend/api/v1/orders/*/cancel", async (route) => {
      if (lost) return route.continue();
      lost = true;
      await route.fetch();
      await route.abort("failed");
    });
    await page.getByTestId("order-cancel").click();
    await page.getByRole("dialog").getByRole("button", { name: "Siparişi iptal et" }).click();
    await expect(page.getByTestId("order-notice")).toContainText("İptalin sonucu doğrulanamadı");
    await expect(page.getByTestId("order-status")).toHaveText("İptal edildi"); // yeniden okunan gerçek durum
    expect(await availableOf(request, v)).toBe(3); // tek kez iade
    // Yeniden deneme güvenli
    const again = await orderSend(page.request, baseURL!, `/orders/${order.id}/cancel`);
    expect((await again.json()).replayed).toBe(true);
    expect(await availableOf(request, v)).toBe(3);
  } finally { await restore(request, v); }
});

test("klavye: Enter ile sipariş, odak sipariş başlığına gelir; iptal penceresi odağı yakalar, Esc odağı düğmeye geri verir, Enter ile onay", async ({ page, request, baseURL }) => {
  const v = v12(14);
  try {
    await prepare(page, baseURL!, [[v, 1]]);
    await openCheckout(page);
    await page.getByRole("radio", { name: /Ev/ }).focus();
    await page.keyboard.press("Tab"); // adres seçiminden sonraki etkileşimli öğeler: bağlantılar, sonra sipariş düğmesi
    await submit(page).focus();
    await expect(submit(page)).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(new RegExp(`/siparisler/${UUID}$`));
    await expect(page.getByRole("heading", { level: 1 })).toBeFocused();

    const cancel = page.getByTestId("order-cancel");
    await cancel.focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog", { name: "Sipariş iptal edilsin mi?" });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole("button", { name: "Vazgeç" })).toBeFocused(); // modal odağı içeri alır
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(cancel).toBeFocused();
    await page.keyboard.press("Enter");
    await page.keyboard.press("Tab");
    await expect(dialog.getByRole("button", { name: "Siparişi iptal et" })).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(page.getByTestId("order-status")).toHaveText("İptal edildi");
    await expect(page.getByRole("heading", { level: 1 })).toBeFocused();
    await expect(page.getByTestId("order-notice-live")).toContainText("iptal edildi");
  } finally { await restore(request, v); }
});

test("hesap bağlantısı: Hesabım'dan Siparişlerim açılır", async ({ page, baseURL }) => {
  await registerApi(page.request, baseURL!);
  await page.goto("/hesabim");
  await page.getByRole("link", { name: "Siparişlerim" }).click();
  await expect(page).toHaveURL(/\/siparisler$/);
  await expect(page.getByRole("heading", { level: 1, name: "Siparişlerim" })).toBeVisible();
});

for (const vp of viewports) {
  test(`görünüm ${vp.width}px: checkout, liste ve ayrıntı yatay taşmaz, alt gezintiyi örtmez, axe ciddi/kritik yok`, async ({ page, request, baseURL }, info) => {
    const v = v12(15);
    const w = v50(3);
    try {
      await page.setViewportSize(vp);
      const { address } = await prepare(page, baseURL!, [[v, 2], [w, 1]]);
      const checks = async (name: string) => {
        expect(await noHorizontalOverflow(page), `${name} yatay taşma`).toBe(true);
        expect(await axeSerious(page), `${name} axe`).toEqual([]);
        await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
        const nav = page.getByRole("navigation", { name: "Alt gezinti" });
        if (vp.width < 768) {
          await expect(nav).toBeVisible();
          const overlap = await page.evaluate(() => {
            const navTop = document.querySelector("nav[aria-label='Alt gezinti']")!.getBoundingClientRect().top;
            const footer = document.querySelector("footer")!.getBoundingClientRect();
            return footer.bottom - navTop; // <= 0: alt bilgi alt gezintinin üstünde bitiyor
          });
          expect(overlap, `${name} alt gezinti örtüşmesi`).toBeLessThanOrEqual(1);
        } else await expect(nav).toBeHidden();
        await page.evaluate(() => window.scrollTo(0, 0));
        await page.screenshot({ path: info.outputPath(`siparis-${name}-${vp.width}.png`), fullPage: true });
      };
      await openCheckout(page);
      await checks("checkout");
      await submit(page).click();
      await expect(page).toHaveURL(new RegExp(`/siparisler/${UUID}$`));
      await expect(page.getByTestId("order-number")).toBeVisible();
      await checks("detay");
      // Daha uzun liste ve iptal edilmiş bir kayıt (damga renkleri)
      await cartApi(page.request, baseURL!, w.id, 1);
      const second = await placeOrderApi(page.request, baseURL!, address.id);
      expect((await orderSend(page.request, baseURL!, `/orders/${second.id}/cancel`)).status()).toBe(200);
      await page.goto("/siparisler");
      await expect(page.getByTestId("order-row")).toHaveCount(2);
      await expect(page.getByTestId("order-row").first().getByTestId("order-status")).toHaveText("İptal edildi");
      await checks("liste");
      await openDetail(page, second.id);
      await expect(page.getByTestId("order-no-cancel")).toHaveCount(0);
      await checks("detay-iptal");
    } finally { await restore(request, v, w); }
  });
}

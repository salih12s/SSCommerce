import { API, credentialsPause, customerPassword, expect, newCtx, newEmail, onHand, ready, send, test, uniqueAddress, variantRow } from "./support";

// P3: gerçek sipariş akışı (müşteri), üretim-eşdeğeri yığında. Stok Admin API'siyle girilir (sellable fixture'ı: depo + stok).
// Arayüz akışı: kayıt -> ürün -> sepet -> adres -> checkout önizleme -> sipariş ver -> detay/liste. Sonra idempotency tekrarı ve iptalle stok iadesi.

test("P3 kayıt -> sepet -> adres -> önizleme -> sipariş -> liste/detay; ödenmemiş dili; idempotent tekrar; iptalde stok iadesi", async ({ browser, admin, sellable }) => {
  const stockBefore = await onHand(admin.context.request, sellable.variantId);
  // Fixture +15 girer, ama 02-csp testleri her genişlikte bir sipariş verir (-3) ve önceki koşulardan kalan stok 0 olabilir: gereken yalnız sipariş verebilmektir.
  expect(stockBefore, "sipariş verebilmek için başlangıç stoku (sellable fixture'ı +15 girdi, 02-csp -3 tüketir)").toBeGreaterThanOrEqual(1);

  const context = await newCtx(browser);
  const page = await context.newPage();
  const posted: { url: string; key: string | undefined; body: string }[] = [];
  page.on("request", (r) => { if (r.method() === "POST" && new URL(r.url()).pathname.endsWith("/orders")) posted.push({ url: r.url(), key: r.headers()["idempotency-key"], body: r.postData() ?? "" }); });

  // Kayıt (arayüz)
  await page.goto("/kayit");
  await expect(page.getByRole("button", { name: "Hesap oluştur", exact: true })).toBeEnabled();
  await page.getByLabel("E-posta", { exact: true }).fill(newEmail());
  await page.getByLabel("Parola", { exact: true }).fill(customerPassword);
  await credentialsPause();
  await page.getByRole("button", { name: "Hesap oluştur", exact: true }).click();
  await expect(page).toHaveURL(/\/hesabim$/);

  // Ürün -> sepet
  await page.goto(`/urun/${sellable.slug}`);
  await ready(page);
  await expect(page.getByText(/Sipariş verirken ödeme alınmaz/)).toBeVisible();
  await variantRow(page, sellable.variantId).getByRole("radio").check();
  await page.getByRole("button", { name: "Sepete ekle" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Sepete eklendi" })).toBeVisible();
  await page.getByRole("link", { name: "Sepete git" }).click();
  await expect(page).toHaveURL(/\/sepet$/);
  await expect(page.getByTestId("cart-line")).toHaveCount(1);
  await page.getByRole("link", { name: "Teslimat adresine geç" }).click();
  await expect(page).toHaveURL(/\/checkout$/);

  // Adres (arayüz)
  const address = uniqueAddress();
  await page.getByLabel("Adres adı").fill(address.label);
  await page.getByLabel("Alıcı adı soyadı").fill(address.recipientName);
  await page.getByLabel("Cep telefonu").fill(address.phone);
  await page.getByLabel("İl", { exact: true }).selectOption(address.province);
  await page.getByLabel("İlçe").fill(address.district);
  await page.getByLabel("Açık adres").fill(address.streetAddress);
  await page.getByRole("button", { name: "Adresi kaydet ve seç" }).click();

  // Önizleme: ödenmemiş dili, kargo/genel toplam yok
  const preview = page.getByTestId("preview");
  await expect(preview).toBeVisible();
  await expect(page.getByTestId("preview-payment")).toHaveText("Alınmaz");
  await expect(page.getByTestId("order-note")).toContainText("Ödeme alınmaz");
  const submit = page.getByTestId("order-submit");
  await expect(submit).toContainText("ödeme alınmaz");
  const previewText = (await page.locator("main").innerText()).toLowerCase();
  expect(previewText).not.toMatch(/ödendi|ciro|genel toplam:|tahsil/);

  // Sipariş ver
  await submit.click();
  await expect(page).toHaveURL(/\/siparisler\/[0-9a-f-]{36}$/);
  const orderId = new URL(page.url()).pathname.split("/").pop()!;
  await expect(page.getByTestId("order-notice")).toHaveText("Siparişin alındı. Ödeme alınmadı.");
  await expect(page.getByTestId("order-unpaid")).toContainText("Ödeme alınmadı");
  await expect(page.getByTestId("order-status")).toHaveText("Alındı");
  await expect(page.getByTestId("order-number")).toHaveText(/^SS-\d+$/);
  const detailText = (await page.locator("main").innerText()).toLowerCase();
  expect(detailText).not.toMatch(/ödendi|ciro|tahsil/);
  expect(detailText).not.toMatch(/genel toplam\s*[:\d]/);
  const orderNumber = (await page.getByTestId("order-number").innerText()).trim();

  // Liste
  await page.goto("/siparisler");
  await ready(page);
  await expect(page.getByTestId("order-link").filter({ hasText: orderNumber })).toHaveCount(1);
  const listText = (await page.locator("main").innerText()).toLowerCase();
  expect(listText).not.toMatch(/ödendi|ciro|tahsil/);

  // Stok düştü (Admin API'si)
  expect(await onHand(admin.context.request, sellable.variantId), "sipariş stoğu 1 düşürdü").toBe(stockBefore - 1);

  // İdempotency: aynı anahtar + aynı gövdeyle ikinci POST aynı siparişi döndürür, tek sipariş kalır, stok bir kez düşmüş kalır.
  expect(posted, "tarayıcı tek POST /orders gönderdi").toHaveLength(1);
  expect(posted[0].key, "Idempotency-Key başlığı").toMatch(/^[A-Za-z0-9_-]{16,128}$/);
  const replay = await send(context.request, "POST", "/orders", JSON.parse(posted[0].body), posted[0].key);
  expect(replay.status(), "aynı anahtar ikinci POST").toBe(200);
  const replayed = await replay.json() as { id: string; orderNumber: string; replayed: boolean };
  expect(replayed.id).toBe(orderId);
  expect(replayed.replayed).toBe(true);
  const listApi = await (await context.request.get(`${API}/orders?page=1&pageSize=50`)).json() as { totalCount: number; items: { id: string }[] };
  expect(listApi.totalCount, "tek sipariş").toBe(1);
  expect(await onHand(admin.context.request, sellable.variantId), "tekrar stoğu ikinci kez düşürmedi").toBe(stockBefore - 1);

  // Müşteri iptali (arayüz) -> stok geri gelir
  await page.goto(`/siparisler/${orderId}`);
  await ready(page);
  await page.getByTestId("order-cancel").click();
  await page.getByRole("dialog").getByRole("button", { name: "Siparişi iptal et" }).click();
  await expect(page.getByTestId("order-status")).toHaveText("İptal edildi");
  await expect(page.getByTestId("order-notice")).toHaveText("Siparişin iptal edildi. Ürünler stoğa geri döndü.");
  expect(await onHand(admin.context.request, sellable.variantId), "iptal stoğu geri verdi").toBe(stockBefore);
  await context.close();
});

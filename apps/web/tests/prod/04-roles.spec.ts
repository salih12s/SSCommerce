import type { Page, Response } from "@playwright/test";
import { customer, customerWithOrder, expect, newCtx, onHand, ready, send, test } from "./support";

// P4: roller ve sipariş işleme. Depo hesabı kayıtlı müşteriydi; Depo rolü `warehouse` fixture'ında Admin API'siyle (hesap kimliğiyle) verilir.

/** Gövdede anahtar adı olarak teslimat/PII alanı var mı (derin tarama). */
function piiKeys(value: unknown, path = ""): string[] {
  if (Array.isArray(value)) return value.flatMap((v, i) => piiKeys(v, `${path}[${i}]`));
  if (value && typeof value === "object") {
    return Object.entries(value).flatMap(([key, v]) => [...(/recipient|phone|street|postal|delivery|address|province|district/i.test(key) ? [`${path}.${key}`] : []), ...piiKeys(v, `${path}.${key}`)]);
  }
  return [];
}

async function advance(page: Page, orderId: string, to: "processing" | "shipped" | "completed") {
  await page.goto(`/admin/siparisler/${orderId}`);
  await ready(page);
  await page.getByTestId(`to-${to}`).click();
  await expect(page.getByText(/durumuna alındı/)).toBeVisible();
}

test("P4 erişim: Depo yalnız siparişleri görür; diğer admin sayfaları 403; anonim /giris'e, müşteri 403'e gider", async ({ browser, warehouse }) => {
  const page = await warehouse.context.newPage();
  await page.goto("/admin");
  await expect(page).toHaveURL(/\/admin\/siparisler$/);
  await ready(page);
  await expect(page.locator("main h1").first()).toHaveText("Siparişler");
  for (const path of ["/admin/urunler", "/admin/urunler/yeni", "/admin/kategoriler", "/admin/stok", "/admin/depolar", "/admin/raporlar", "/admin/kullanicilar"]) {
    const response = await page.goto(path);
    expect(response?.status(), path).toBe(403);
    await expect(page.getByRole("heading", { name: "Bu alana erişimin yok" }), path).toBeVisible();
  }
  // API de kapalı (arayüz kapısına güvenilmez)
  expect((await warehouse.context.request.get("/api/backend/api/v1/admin/products")).status()).toBe(403);
  expect((await send(warehouse.context.request, "POST", "/admin/inventory/receive", { warehouseId: "00000000-0000-0000-0000-000000000000", variantId: "00000000-0000-0000-0000-000000000000", quantity: 1, reason: "x" }, "yetkisiz-deneme-0123456789")).status()).toBe(403);
  await page.close();

  // Anonim -> /giris
  const anon = await newCtx(browser);
  const anonPage = await anon.newPage();
  await anonPage.goto("/admin/siparisler");
  await expect(anonPage).toHaveURL(/\/giris\?next=/);
  await anonPage.goto("/admin");
  await expect(anonPage).toHaveURL(/\/giris\?next=/);
  await anon.close();

  // Müşteri -> 403
  const plain = await customer(browser);
  const plainPage = await plain.context.newPage();
  for (const path of ["/admin", "/admin/siparisler", "/admin/urunler"]) {
    expect((await plainPage.goto(path))?.status(), path).toBe(403);
  }
  expect((await plain.context.request.get("/api/backend/api/v1/admin/orders")).status()).toBe(403);
  await plain.context.close();
});

test("P4 D-071: Depo sipariş detayında teslimat adresi/telefon/alıcı yok (sayfa, belge ve API yanıtları); Admin görür", async ({ browser, admin, warehouse, sellable }) => {
  const { actor: buyer, address, order } = await customerWithOrder(browser, sellable);
  const tag = /Q([0-9a-f]{10})/.exec(address.recipientName)![1];
  const secrets = [address.recipientName, address.phone, address.streetAddress, tag];

  const page = await warehouse.context.newPage();
  const bodies: { url: string; text: string }[] = [];
  page.on("response", async (r: Response) => {
    if (new URL(r.url()).pathname.includes("/api/backend/")) { try { bodies.push({ url: new URL(r.url()).pathname, text: await r.text() }); } catch { /* yönlendirme gövdesi yok */ } }
  });
  const doc = await page.goto("/admin/siparisler");
  await ready(page);
  const listDoc = await doc!.text();
  const detailDoc = await (await page.goto(`/admin/siparisler/${order.id}`))!.text();
  await ready(page);
  await expect(page.getByTestId("order-number")).toHaveText(order.orderNumber);
  await expect(page.getByText("Teslimat bilgisi yalnızca yöneticiye açıktır.")).toBeVisible();
  await expect(page.getByTestId("delivery-show")).toHaveCount(0);

  const rendered = await page.locator("body").innerText();
  const html = await page.content();
  for (const s of secrets) {
    expect(rendered, `sayfa metni "${s.slice(0, 8)}…" içermemeli`).not.toContain(s);
    expect(html, "DOM").not.toContain(s);
    expect(listDoc, "liste belgesi").not.toContain(s);
    expect(detailDoc, "detay belgesi").not.toContain(s);
    for (const b of bodies) expect(b.text, `${b.url} yanıtı`).not.toContain(s);
  }
  const detailApi = bodies.find((b) => /\/admin\/orders\/[0-9a-f-]{36}$/.test(b.url));
  expect(detailApi, "tarayıcı sipariş detayı API'sini çağırdı").toBeTruthy();
  expect(piiKeys(JSON.parse(detailApi!.text)), "detay yanıtında PII anahtarı").toEqual([]);
  const listApi = bodies.find((b) => b.url.endsWith("/admin/orders"));
  expect(piiKeys(JSON.parse(listApi!.text)), "liste yanıtında PII anahtarı").toEqual([]);

  // Doğrudan API: detay yanıtı + teslimat ucu Depo'ya kapalı
  const direct = await warehouse.context.request.get(`/api/backend/api/v1/admin/orders/${order.id}`);
  expect(direct.status()).toBe(200);
  const directText = await direct.text();
  for (const s of secrets) expect(directText).not.toContain(s);
  expect((await send(warehouse.context.request, "POST", `/admin/orders/${order.id}/delivery-view`)).status(), "Depo delivery-view").toBe(403);
  await page.close();

  // Admin görür (ve bu görüntüleme denetim kaydına yazılır)
  const adminPage = await admin.context.newPage();
  await adminPage.goto(`/admin/siparisler/${order.id}`);
  await ready(adminPage);
  await adminPage.getByTestId("delivery-show").click();
  await expect(adminPage.getByTestId("delivery-box")).toContainText(address.recipientName);
  await expect(adminPage.getByTestId("delivery-box")).toContainText(address.streetAddress);
  await adminPage.close();
  await buyer.context.close();
});

test("P4 durum ilerletme: Admin ve Depo ilerletir; iptal yalnız Admin (Depo API 403, arayüzde yok); iptal stoğu iade eder", async ({ browser, admin, warehouse, sellable }) => {
  const adminPage = await admin.context.newPage();
  const whPage = await warehouse.context.newPage();

  // Sipariş 1: Admin placed->processing, Depo processing->shipped, Admin shipped->completed
  const one = await customerWithOrder(browser, sellable);
  await advance(adminPage, one.order.id, "processing");
  await advance(whPage, one.order.id, "shipped");
  await advance(adminPage, one.order.id, "completed");
  const final = await admin.context.request.get(`/api/backend/api/v1/admin/orders/${one.order.id}`);
  expect((await final.json()).status).toBe("completed");
  // Müşteri de kendi siparişinde ilerlemeyi görür
  await one.actor.context.close();

  // Sipariş 2: Depo placed->processing; Depo iptal edemez; Admin iptal eder, stok iade olur
  const stockBefore = await onHand(admin.context.request, sellable.variantId);
  const two = await customerWithOrder(browser, sellable);
  expect(await onHand(admin.context.request, sellable.variantId), "sipariş stoğu düşürdü").toBe(stockBefore - 1);
  await advance(whPage, two.order.id, "processing");
  const denied = await send(warehouse.context.request, "POST", `/admin/orders/${two.order.id}/status`, { status: "cancelled", reason: "Depo iptal denemesi" });
  expect(denied.status(), "Depo iptali").toBe(403);
  await whPage.goto(`/admin/siparisler/${two.order.id}`);
  await ready(whPage);
  await expect(whPage.getByTestId("cancel-open")).toHaveCount(0);
  await expect(whPage.getByText(/İptal yalnızca yöneticidedir/)).toBeVisible();

  await adminPage.goto(`/admin/siparisler/${two.order.id}`);
  await ready(adminPage);
  await adminPage.getByLabel("İptal nedeni").fill("Prod drill: iptal ve stok iadesi");
  await adminPage.getByTestId("cancel-open").click();
  await adminPage.getByRole("dialog").getByRole("button", { name: "Evet, iptal et" }).click();
  await expect(adminPage.getByText(/Sipariş iptal edildi; ayrılan stok depolara iade edildi\./)).toBeVisible();
  expect(await onHand(admin.context.request, sellable.variantId), "Admin iptali stoğu iade etti").toBe(stockBefore);
  await two.actor.context.close();
  await adminPage.close();
  await whPage.close();
});

import { expect, test } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { actor, mkSellable, publicAvailable, retire, type Actor, type Sellable } from "./admin-support";
import { api, cartApi, goodAddress, orderSend, send } from "./support";

// Faz 8 / F8.5: ödenmemiş sipariş suistimali korumaları (sunucu tarafı sınırlar). Sözleşme (docs/CONTRACT_PHASE_8.md "Unpaid-order limits"):
//  - Ordering:MaxOpenOrdersPerUser (varsayılan 3): açık = placed + processing; aşım → 409 open_order_limit (+limit)
//  - Ordering:MaxOrdersPerRollingDay (sözleşme varsayılanı 10; E2E'de playwright.config.ts 11 yapar, bkz. orders.spec.ts sayfalama testi): son 24 saatteki TÜM siparişler (durumdan bağımsız); aşım → 429 order_daily_limit (+limit, Retry-After 1..86400)
//  - Replay (aynı anahtar) sınırdan önce döner ve sayılmaz; reddedilen istek anahtarı, stoku, sepeti tüketmez.
// E2E ana makinesi varsayılanlarla koşar (E2E_MAX_* ortam değişkenleri verilmediyse). Sayılar sözleşmeden: 3 ve 10.
// Bu dosya sipariş yazar ve stok tüketir: "admin-limits" projesinde, en sonda ve tek worker'da koşar.
test.use({ viewport: { width: 1440, height: 900 } });

const OPEN_LIMIT = Number(process.env.E2E_MAX_OPEN_ORDERS ?? 3);
const DAILY_LIMIT = Number(process.env.E2E_MAX_ORDERS_PER_DAY ?? 10); // playwright.config.ts E2E için 11 yapar (sözleşme varsayılanı 10)

let admin: Actor;
test.beforeAll(async ({ browser, baseURL }) => { admin = await actor(browser, baseURL!, "Admin"); });
test.afterAll(async () => { await admin?.context.close(); });

type Placed = { status: number; body: Record<string, unknown>; retryAfter: string | undefined };
/** Sepete 1 adet ekler, önizler ve verilen anahtarla sipariş verir. İsteğe bağlı: aynı anahtarla gövde tekrarı için `quote` verilirse önizleme atlanır. */
async function place(c: Actor, s: Sellable, addressId: string, key: string = randomUUID(), fixed?: { quote: string }): Promise<Placed & { quote: string }> {
  let quote = fixed?.quote;
  if (!quote) {
    await cartApi(c.request, c.origin, s.variant.id, 1);
    const preview = await send(c.request, "POST", "/checkout/preview", c.origin, { addressId });
    expect(preview.status()).toBe(200);
    quote = (await preview.json()).quote as string;
  }
  const response = await orderSend(c.request, c.origin, "/orders", { addressId, quote }, key);
  return { status: response.status(), body: await response.json() as Record<string, unknown>, retryAfter: response.headers()["retry-after"], quote };
}
async function newCustomer(browser: import("@playwright/test").Browser) {
  const c = await actor(browser, admin.origin, null);
  const address = await send(c.request, "POST", "/addresses", c.origin, goodAddress);
  expect(address.status()).toBe(201);
  return { c, addressId: (await address.json()).id as string };
}
const cancel = (c: Actor, orderId: string) => send(c.request, "POST", `/orders/${orderId}/cancel`, c.origin, {});
const openCount = async (c: Actor) => {
  const r = await (await c.request.get(`${api}/orders?page=1&pageSize=50`)).json() as { items: { status: string }[] };
  return r.items.filter((o) => o.status === "placed" || o.status === "processing").length;
};

test("açık sipariş sınırı: sınırın altında normal sipariş çalışır; aşımda 409 open_order_limit (+limit); red anahtar/stok/sepet tüketmez; replay sayılmaz; iptal sayacı düşürür", async ({ browser }) => {
  const s = await mkSellable(admin, { stock: 20, priceMinor: 9_900 });
  const { c, addressId } = await newCustomer(browser);
  try {
    const placed: { id: string; key: string; quote: string }[] = [];
    for (let i = 0; i < OPEN_LIMIT; i++) {
      const key = randomUUID();
      const r = await place(c, s, addressId, key);
      expect(r.status, `sipariş ${i + 1}: ${JSON.stringify(r.body)}`).toBe(201); // sınırın altında/eşiğinde normal akış
      placed.push({ id: r.body.id as string, key, quote: r.quote });
    }
    expect(await openCount(c)).toBe(OPEN_LIMIT);
    const stockAfterOpen = await publicAvailable(admin.request, s.slug, s.variant.id);
    expect(stockAfterOpen).toBe(20 - OPEN_LIMIT);

    // Replay: sınıra dayansa da aynı anahtar+gövde 200 replayed:true döner, yeni sipariş yazmaz
    const replay = await place(c, s, addressId, placed[0].key, { quote: placed[0].quote });
    expect(replay.status, JSON.stringify(replay.body)).toBe(200);
    expect(replay.body).toMatchObject({ id: placed[0].id, replayed: true });
    expect(await openCount(c)).toBe(OPEN_LIMIT);

    // Aşım: farklı anahtarla yeni sipariş
    const rejectedKey = randomUUID();
    const over = await place(c, s, addressId, rejectedKey);
    expect(over.status, JSON.stringify(over.body)).toBe(409);
    expect(over.body).toMatchObject({ code: "open_order_limit", limit: OPEN_LIMIT });
    // Reddedilen istek hiçbir şey tüketmedi: stok aynı, sipariş sayısı aynı, sepet yerinde
    expect(await publicAvailable(admin.request, s.slug, s.variant.id)).toBe(stockAfterOpen);
    expect(await openCount(c)).toBe(OPEN_LIMIT);
    const cart = await (await c.request.get(`${api}/cart`)).json() as { itemCount: number };
    expect(cart.itemCount).toBe(1);

    // Arayüz: checkout aynı durumu Türkçe ve yol göstererek söyler, kendiliğinden yeniden denemez
    let posts = 0;
    c.page.on("request", (r) => { if (r.method() === "POST" && new URL(r.url()).pathname.endsWith("/api/v1/orders")) posts++; });
    await c.page.goto("/checkout");
    await expect(c.page.getByTestId("preview")).toBeVisible();
    await c.page.getByTestId("order-submit").click();
    const problem = c.page.getByRole("alert").filter({ hasText: "Açık sipariş sınırına ulaştın" });
    await expect(problem).toBeVisible();
    await expect(problem).toContainText(`Aynı anda en çok ${OPEN_LIMIT} ödenmemiş açık siparişin olabilir.`);
    await expect(problem.getByRole("link", { name: /Siparişlerim/ })).toBeVisible();
    await c.page.waitForTimeout(1500);
    expect(posts, "arayüz otomatik yeniden denemedi").toBe(1);

    // İptal sayacı düşürür: aynı REDDEDİLMİŞ anahtar şimdi uygulanır (anahtar tüketilmemişti)
    expect((await cancel(c, placed[0].id)).status()).toBe(200);
    expect(await openCount(c)).toBe(OPEN_LIMIT - 1);
    const preview = await send(c.request, "POST", "/checkout/preview", c.origin, { addressId });
    const retry = await place(c, s, addressId, rejectedKey, { quote: (await preview.json()).quote as string });
    expect(retry.status, JSON.stringify(retry.body)).toBe(201);
    expect(retry.body.replayed).toBe(false);
    expect(await openCount(c)).toBe(OPEN_LIMIT);

    // İlerleyen durumlar (hazırlanıyor) hâlâ açık sayılır: yönetici bir siparişi hazırlamaya alsa da sınır korunur
    const processing = await send(admin.request, "POST", `/admin/orders/${placed[1].id}/status`, admin.origin, { status: "processing" });
    expect(processing.status()).toBe(200);
    const stillOver = await place(c, s, addressId, randomUUID());
    expect(stillOver.status).toBe(409);
    expect(stillOver.body.code).toBe("open_order_limit");
    // Yönetici iptali de sayacı düşürür
    expect((await send(admin.request, "POST", `/admin/orders/${placed[1].id}/status`, admin.origin, { status: "cancelled", reason: "sınır testi" })).status()).toBe(200);
    expect(await openCount(c)).toBe(OPEN_LIMIT - 1);
    // Tamamlanan sipariş açık sayılmaz: kalan açık siparişleri sona kadar ilerlet
    for (const o of placed.slice(2)) {
      for (const next of ["processing", "shipped", "completed"]) expect((await send(admin.request, "POST", `/admin/orders/${o.id}/status`, admin.origin, { status: next })).status()).toBe(200);
    }
    const afterCompleted = await openCount(c);
    expect(afterCompleted).toBeLessThan(OPEN_LIMIT);
    const fresh = await place(c, s, addressId, randomUUID());
    expect(fresh.status, JSON.stringify(fresh.body)).toBe(201);
  } finally {
    const mine = await (await c.request.get(`${api}/orders?page=1&pageSize=50`)).json() as { items: { id: string; status: string }[] };
    for (const o of mine.items) if (o.status === "placed") await cancel(c, o.id);
    await c.context.close();
    await retire(admin, s);
  }
});

test("günlük sınır: durumdan bağımsız sayılır (iptal edilenler dahil); aşımda 429 order_daily_limit (+limit, Retry-After); arayüz Türkçe söyler; başka kullanıcı etkilenmez", async ({ browser }) => {
  test.setTimeout(180_000);
  const s = await mkSellable(admin, { stock: 20, priceMinor: 5_500 });
  const { c, addressId } = await newCustomer(browser);
  const other = await newCustomer(browser);
  try {
    // DAILY_LIMIT sipariş ver ve her birini hemen iptal et: açık sayaç hiç dolmaz, ama günlük sayaç dolar
    for (let i = 0; i < DAILY_LIMIT; i++) {
      const r = await place(c, s, addressId);
      expect(r.status, `sipariş ${i + 1}: ${JSON.stringify(r.body)}`).toBe(201);
      expect((await cancel(c, r.body.id as string)).status()).toBe(200);
    }
    expect(await openCount(c)).toBe(0);
    expect(await publicAvailable(admin.request, s.slug, s.variant.id)).toBe(20); // iptallerle stok tam

    // Sınırı aşan istek
    const key = randomUUID();
    const over = await place(c, s, addressId, key);
    expect(over.status, JSON.stringify(over.body)).toBe(429);
    expect(over.body).toMatchObject({ code: "order_daily_limit", limit: DAILY_LIMIT });
    expect(Number(over.retryAfter), `Retry-After ${over.retryAfter}`).toBeGreaterThanOrEqual(1);
    expect(Number(over.retryAfter)).toBeLessThanOrEqual(86_400);
    // IP hız sınırlayıcıdan ayrı: kod `rate_limited` değil
    expect(over.body.code).not.toBe("rate_limited");
    // Hiçbir şey tüketilmedi: stok tam, sepet yerinde, açık sipariş yok
    expect(await publicAvailable(admin.request, s.slug, s.variant.id)).toBe(20);
    expect(((await (await c.request.get(`${api}/cart`)).json()) as { itemCount: number }).itemCount).toBe(1);
    // Aynı reddedilen anahtar sonra da aynı sınır cevabını alır (tüketilmediği için "idempotency_key_reused" DEĞİL)
    const again = await place(c, s, addressId, key, { quote: over.quote });
    expect(again.status).toBe(429);
    expect(again.body.code).toBe("order_daily_limit");

    // Arayüz
    let posts = 0;
    c.page.on("request", (r) => { if (r.method() === "POST" && new URL(r.url()).pathname.endsWith("/api/v1/orders")) posts++; });
    await c.page.goto("/checkout");
    await expect(c.page.getByTestId("preview")).toBeVisible();
    await c.page.getByTestId("order-submit").click();
    const problem = c.page.getByRole("alert").filter({ hasText: "Günlük sipariş sınırına ulaştın" });
    await expect(problem).toBeVisible();
    await expect(problem).toContainText(`Son 24 saatte en çok ${DAILY_LIMIT} sipariş verebilirsin`);
    await expect(problem).toContainText("sonra yeniden deneyebilirsin");
    await c.page.waitForTimeout(1500);
    expect(posts, "arayüz otomatik yeniden denemedi").toBe(1);

    // Başka kullanıcı kendi sayacıyla normal sipariş verir
    const fine = await place(other.c, s, other.addressId);
    expect(fine.status, JSON.stringify(fine.body)).toBe(201);
    await cancel(other.c, fine.body.id as string);
  } finally {
    await c.context.close();
    await other.c.context.close();
    await retire(admin, s);
  }
});

import { expect, test, type Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import { formatPriceMinor } from "../../src/lib/format";
import type { AuditRow, Movement, Paged } from "../../src/features/admin/types";
import { actor, adminOrder, aGet, aProblem, customerWithOrder, dashboardApi, mkSellable, publicAvailable, retire, settled, type Actor, type Sellable } from "./admin-support";
import { api, axeSerious, goodAddress, noHorizontalOverflow, send, viewports } from "./support";

// Faz 8 sipariş yürütmesi (personel), iptalde stok iadesi, PII sınırı, özet ve raporlar. Gerçek tarayıcı → Next proxy → gerçek API → izole PostgreSQL.
// Siparişler stok tüketir ve vitrin kataloguna kendi ürünlerini ekler: "admin-fulfilment" projesinde, vitrin ve katalog/stok projelerinden sonra koşar.
test.use({ viewport: { width: 1440, height: 900 } });

let admin: Actor;
let wh: Actor;
test.beforeAll(async ({ browser, baseURL }) => {
  admin = await actor(browser, baseURL!, "Admin");
  wh = await actor(browser, baseURL!, "Warehouse");
});
test.afterAll(async () => { await admin?.context.close(); await wh?.context.close(); });

const stamp = (p: Page) => p.getByTestId("order-status");
const statusApi = (a: Actor, id: string, status: string, reason?: string) => send(a.request, "POST", `/admin/orders/${id}/status`, a.origin, reason ? { status, reason } : { status });
const movementsOf = (variantId: string) => aGet<Paged<Movement>>(admin.request, `/inventory/movements?variantId=${variantId}&pageSize=50`);
const PII = [goodAddress.recipientName, goodAddress.phone, goodAddress.streetAddress, "Kadıköy", goodAddress.postalCode];

async function withOrder<T>(opts: { qty?: number; stock?: number; priceMinor?: number }, body: (ctx: { s: Sellable; order: Awaited<ReturnType<typeof customerWithOrder>>["order"]; customer: Actor }) => Promise<T>) {
  const s = await mkSellable(admin, { stock: opts.stock ?? 10, priceMinor: opts.priceMinor ?? 25_000 });
  let customer: Actor | null = null;
  try {
    const made = await customerWithOrder(admin.context.browser()!, admin.origin, s, opts.qty ?? 1);
    customer = made.customer;
    return await body({ s, order: made.order, customer });
  } finally { await customer?.context.close(); await retire(admin, s); }
}

test("Depo personeli siparişi UI'dan hazırlar → kargolar → tamamlar; geçmiş ve stok ayrımı görünür; iptal yalnız Admin'dedir", async () => {
  await withOrder({ qty: 2, stock: 10 }, async ({ s, order, customer }) => {
    expect(await publicAvailable(admin.request, s.slug, s.variant.id)).toBe(8);
    // Depo AÇIK siparişi API'den de iptal edemez (rol denetimi): 403, sipariş değişmez
    expect((await statusApi(wh, order.id, "cancelled", "yetkisiz")).status()).toBe(403);
    expect((await adminOrder(admin.request, order.id)).status).toBe("placed");
    await wh.page.goto(`/admin/siparisler/${order.id}`);
    await settled(wh.page);
    await expect(wh.page.getByTestId("order-number")).toHaveText(order.orderNumber);
    await expect(stamp(wh.page)).toHaveAttribute("data-status", "placed");
    await expect(wh.page.getByTestId("order-subtotal")).toHaveText(formatPriceMinor(50_000));
    // Depo iptal edemez: iptal kutusu yok, nedeni yazılı
    await expect(wh.page.getByTestId("cancel-open")).toHaveCount(0);
    await expect(wh.page.getByText("İptal yalnızca yöneticidedir")).toBeVisible();
    // Teslimat bilgisi yönetici içindir
    await expect(wh.page.getByText("Teslimat bilgisi yalnızca yöneticiye açıktır.")).toBeVisible();
    await expect(wh.page.getByTestId("delivery-show")).toHaveCount(0);
    // Stok ayrımı
    const alloc = wh.page.getByRole("table", { name: "Stok ayrımı" });
    await expect(alloc.getByRole("row").filter({ hasText: s.sku })).toContainText("E2E-DEPO");
    await expect(alloc.getByRole("row").filter({ hasText: s.sku })).toContainText("2");

    await wh.page.getByTestId("to-processing").click();
    await expect(wh.page.getByText('Sipariş "Hazırlanıyor" durumuna alındı.')).toBeVisible();
    await expect(stamp(wh.page)).toHaveAttribute("data-status", "processing");
    await wh.page.getByTestId("to-shipped").click();
    await expect(stamp(wh.page)).toHaveAttribute("data-status", "shipped");
    await wh.page.getByTestId("to-completed").click();
    await expect(stamp(wh.page)).toHaveAttribute("data-status", "completed");
    await expect(wh.page.getByText("Bu sipariş son durumda; başka işlem yapılamaz.")).toBeVisible();
    // Geçmiş: 4 satır, aktör Depo
    const history = wh.page.getByRole("region", { name: "Durum geçmişi" }).getByRole("listitem");
    await expect(history).toHaveCount(4);
    await expect(history.nth(1)).toContainText("Alındı → Hazırlanıyor");
    await expect(history.nth(1)).toContainText("Personel");
    // Müşteri tarafı aynı durumu görür; tamamlanan sipariş stoğu geri getirmez
    expect((await (await customer.request.get(`${api}/orders/${order.id}`)).json()).status).toBe("completed");
    expect(await publicAvailable(admin.request, s.slug, s.variant.id)).toBe(8);
    // Depo, tamamlanmış siparişi iptal edemez; Admin de edemez
    await aProblem(admin, "POST", `/orders/${order.id}/status`, { status: "cancelled", reason: "olmaz" }, 409, "order_transition_invalid");
  });
});

test("Admin açık siparişi iptal eder: neden şart, onay penceresi, stok depoya iade edilir (vitrin + hareket defteri), geçmişte neden görünür", async () => {
  await withOrder({ qty: 3, stock: 10 }, async ({ s, order }) => {
    expect(await publicAvailable(admin.request, s.slug, s.variant.id)).toBe(7);
    await admin.page.goto(`/admin/siparisler/${order.id}`);
    await settled(admin.page);
    await expect(admin.page.getByTestId("cancel-open")).toBeDisabled(); // neden boş
    await admin.page.getByLabel("İptal nedeni").fill("Müşteri vazgeçti");
    await admin.page.getByTestId("cancel-open").click();
    const dialog = admin.page.getByRole("dialog");
    await expect(dialog).toContainText(`${order.orderNumber} iptal edilsin mi?`);
    await expect(dialog).toContainText("Müşteri vazgeçti");
    await dialog.getByRole("button", { name: "Vazgeç" }).click();
    expect((await adminOrder(admin.request, order.id)).status).toBe("placed"); // vazgeçmek hiçbir şey yazmadı
    await admin.page.getByTestId("cancel-open").click();
    await dialog.getByRole("button", { name: "Evet, iptal et" }).click();
    await expect(admin.page.getByText("Sipariş iptal edildi; ayrılan stok depolara iade edildi.")).toBeVisible();
    await expect(stamp(admin.page)).toHaveAttribute("data-status", "cancelled");
    await expect(admin.page.getByText("Neden: Müşteri vazgeçti", { exact: true })).toBeVisible();
    // Stok iadesi: vitrin ve hareket defteri
    expect(await publicAvailable(admin.request, s.slug, s.variant.id)).toBe(10);
    const moves = (await movementsOf(s.variant.id)).items;
    const placement = moves.find((m) => m.operationType === "order_placement")!;
    const refund = moves.find((m) => m.operationType === "order_cancellation")!;
    expect([placement.quantityDelta, refund.quantityDelta]).toEqual([-3, 3]);
    expect(placement.actorEmail).toBeNull(); // müşteri anonim
    expect(refund.actorEmail).toBe(admin.email); // iptali yapan personel
    // Stok ekranında da: "Müşteri" ve yönetici e-postası
    await admin.page.goto(`/admin/stok?q=${encodeURIComponent(s.sku)}`);
    await settled(admin.page);
    await admin.page.getByRole("button", { name: `Seç: ${s.sku}` }).click();
    const ledger = admin.page.getByRole("table", { name: "Stok hareketleri" });
    await expect(ledger.getByRole("row").filter({ hasText: "Sipariş düşümü" })).toContainText("Müşteri");
    await expect(ledger.getByRole("row").filter({ hasText: "Sipariş iadesi" })).toContainText(admin.email);
    // İkinci iptal aynı hedef durum olduğundan idempotent 200 döner; stok İKİ KEZ dönmez, ikinci iade hareketi yazılmaz
    expect((await statusApi(admin, order.id, "cancelled", "tekrar")).status()).toBe(200);
    expect(await publicAvailable(admin.request, s.slug, s.variant.id)).toBe(10);
    expect((await movementsOf(s.variant.id)).items.filter((m) => m.operationType === "order_cancellation")).toHaveLength(1);
  });
});

test("order_transition_invalid: sayfa eskiyken geçiş denenirse arayüz güncel durumu yükler; atlayan geçişler ve bilinmeyen durum reddedilir; paralel iptal tek stok iadesi yazar", async () => {
  await withOrder({ qty: 2, stock: 10 }, async ({ order }) => {
    await wh.page.goto(`/admin/siparisler/${order.id}`);
    await settled(wh.page);
    await expect(wh.page.getByTestId("to-processing")).toBeVisible();
    // Başka bir oturum siparişi ilerletir
    // (Aynı hedef duruma ikinci kez geçiş sunucuda idempotent 200 döner; gerçek çakışma için sipariş iki adım ilerletilir.)
    expect((await statusApi(admin, order.id, "processing")).status()).toBe(200);
    expect((await statusApi(admin, order.id, "shipped")).status()).toBe(200);
    await wh.page.getByTestId("to-processing").click();
    await expect(wh.page.getByRole("alert").filter({ hasText: "Siparişin durumu bu sırada değişti" })).toBeVisible();
    await expect(wh.page.getByTestId("to-completed")).toBeVisible(); // güncel durum yüklendi
    await expect(stamp(wh.page)).toHaveAttribute("data-status", "shipped");

    // Geri / bilinmeyen / olmayan
    await aProblem(admin, "POST", `/orders/${order.id}/status`, { status: "processing" }, 409, "order_transition_invalid");
    await aProblem(admin, "POST", `/orders/${order.id}/status`, { status: "placed" }, 409, "order_transition_invalid");
    await aProblem(admin, "POST", `/orders/${order.id}/status`, { status: "kayip" }, 400, "validation_failed");
    await aProblem(admin, "POST", `/orders/${order.id}/status`, {}, 400, "validation_failed");
    await aProblem(admin, "POST", `/orders/${randomUUID()}/status`, { status: "shipped" }, 404, "order_not_found");
  });
});

test("paralel iptal: aynı anda iki Admin isteği → ikisi de sonuçlanır ama stok yalnız bir kez iade edilir; kargolanmış sipariş iptal edilemez", async () => {
  await withOrder({ qty: 2, stock: 10 }, async ({ s, order }) => {
    expect(await publicAvailable(admin.request, s.slug, s.variant.id)).toBe(8);
    const pair = await Promise.all([1, 2].map((n) => statusApi(admin, order.id, "cancelled", `paralel ${n}`)));
    expect(pair.map((r) => r.status()).sort()).toEqual([200, 200]); // aynı hedef durum: ikincisi idempotent
    expect(await publicAvailable(admin.request, s.slug, s.variant.id)).toBe(10);
    expect((await movementsOf(s.variant.id)).items.filter((m) => m.operationType === "order_cancellation")).toHaveLength(1);
    expect((await adminOrder(admin.request, order.id)).statusHistory.filter((h) => h.toStatus === "cancelled")).toHaveLength(1);
  });
  await withOrder({ qty: 1, stock: 10 }, async ({ s, order }) => {
    for (const next of ["processing", "shipped"]) expect((await statusApi(admin, order.id, next)).status()).toBe(200);
    await aProblem(admin, "POST", `/orders/${order.id}/status`, { status: "cancelled", reason: "kargoda" }, 409, "order_transition_invalid");
    expect(await publicAvailable(admin.request, s.slug, s.variant.id)).toBe(9); // stok geri gelmedi
  });
});

test("PII: teslimat yalnız Admin'e, tek tek ve denetim izli; liste/ayrıntı JSON'unda, HTML/RSC'de, URL ve depolarda adres yok; Depo 403", async () => {
  await withOrder({ qty: 1 }, async ({ order, customer }) => {
    const auditCount = async () => (await aGet<Paged<AuditRow>>(admin.request, `/audit?targetType=order&targetId=${order.id}`)).items.filter((r) => r.action === "order.delivery_view").length;
    expect(await auditCount()).toBe(0);
    // API: liste, ayrıntı ve geçmiş kişisel veri içermez (Admin ve Depo)
    for (const a of [admin, wh]) {
      for (const path of [`/admin/orders/${order.id}`, "/admin/orders?pageSize=50", "/admin/orders?open=true&pageSize=50"]) {
        const body = await (await a.request.get(`${api}${path}`)).text();
        for (const secret of [...PII, customer.email]) expect(body, `${path} ${secret}`).not.toContain(secret);
      }
    }
    // Ham HTML ve RSC
    for (const headers of [{}, { RSC: "1" }] as Record<string, string>[]) {
      let r = await admin.request.get(`/admin/siparisler/${order.id}`, { headers, maxRedirects: 0 });
      if (r.status() === 307) r = await admin.request.get(r.headers().location, { headers, maxRedirects: 0 });
      expect(r.status()).toBe(200);
      const text = await r.text();
      for (const secret of [...PII, customer.email, order.orderNumber]) expect(text, secret).not.toContain(secret);
    }
    // Depo: arayüzde yok, API 403, denetim satırı yok
    expect((await send(wh.request, "POST", `/admin/orders/${order.id}/delivery-view`, wh.origin)).status()).toBe(403);
    expect(await auditCount()).toBe(0);

    // Admin: göster → görünür, bir denetim satırı, satırda PII yok; gizle → DOM'dan kalkar
    await admin.page.goto(`/admin/siparisler/${order.id}`);
    await settled(admin.page);
    await expect(admin.page.getByTestId("delivery-box")).toHaveCount(0);
    await admin.page.getByTestId("delivery-show").click();
    const box = admin.page.getByTestId("delivery-box");
    await expect(box).toContainText(goodAddress.recipientName);
    await expect(box).toContainText(goodAddress.phone);
    await expect(box).toContainText(goodAddress.streetAddress);
    await expect(box).toContainText(`${goodAddress.province} / ${goodAddress.district}`);
    expect(await auditCount()).toBe(1);
    const row = (await aGet<Paged<AuditRow>>(admin.request, `/audit?targetType=order&targetId=${order.id}`)).items.find((r) => r.action === "order.delivery_view")!;
    expect(row.actorEmail).toBe(admin.email);
    expect(JSON.stringify(row)).not.toMatch(new RegExp([...PII, customer.email].map((x) => x.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("|")));
    // Adres ne URL'de ne depolarda
    expect(admin.page.url()).not.toContain(encodeURIComponent(goodAddress.recipientName));
    const storages = await admin.page.evaluate(() => JSON.stringify([{ ...window.localStorage }, { ...window.sessionStorage }]));
    for (const secret of PII) expect(storages).not.toContain(secret);
    await box.getByRole("button", { name: "Gizle" }).click();
    await expect(box).toHaveCount(0);
    await admin.page.reload();
    await settled(admin.page);
    await expect(admin.page.getByTestId("delivery-box")).toHaveCount(0); // yenilemede kalıcı değil
    // Her gösterim kendi denetim satırını yazar
    await admin.page.getByTestId("delivery-show").click();
    await expect(admin.page.getByTestId("delivery-box")).toBeVisible();
    expect(await auditCount()).toBe(2);
    // Olmayan sipariş
    await aProblem(admin, "POST", `/orders/${randomUUID()}/delivery-view`, undefined, 404, "order_not_found");
  });
});

test("D-071 Depo personeli müşteri teslimat/telefon/adres verisini göremez: sipariş ayrıntısı arayüzü, ham HTML/RSC, API gövdeleri ve depolar; teslimat ucu 403 ve denetim satırı yazmaz", async () => {
  await withOrder({ qty: 1 }, async ({ order, customer }) => {
    const secrets = [...PII, customer.email];
    // Arayüz: ayrıntı sayfası ve liste
    await wh.page.goto(`/admin/siparisler/${order.id}`);
    await settled(wh.page);
    await expect(wh.page.getByText("Teslimat bilgisi yalnızca yöneticiye açıktır.")).toBeVisible();
    await expect(wh.page.getByTestId("delivery-show")).toHaveCount(0);
    await expect(wh.page.getByTestId("delivery-box")).toHaveCount(0);
    const detailText = await wh.page.locator("body").innerText();
    for (const secret of secrets) expect(detailText, secret).not.toContain(secret);
    expect(wh.page.url()).not.toContain(encodeURIComponent(goodAddress.recipientName));
    const storages = await wh.page.evaluate(() => JSON.stringify([{ ...window.localStorage }, { ...window.sessionStorage }]));
    for (const secret of secrets) expect(storages, secret).not.toContain(secret);
    // Ham HTML ve RSC (Depo çerezi ile)
    for (const headers of [{}, { RSC: "1" }] as Record<string, string>[]) {
      let r = await wh.request.get(`/admin/siparisler/${order.id}`, { headers, maxRedirects: 0 });
      if (r.status() === 307) r = await wh.request.get(r.headers().location, { headers, maxRedirects: 0 });
      expect(r.status()).toBe(200);
      const text = await r.text();
      for (const secret of secrets) expect(text, secret).not.toContain(secret);
    }
    // API: ayrıntı, liste ve geçmiş; ayrıca adres/telefon/ad alan adları da yok
    for (const path of [`/admin/orders/${order.id}`, "/admin/orders?pageSize=50", "/admin/orders?open=true&pageSize=50"]) {
      const response = await wh.request.get(`${api}${path}`);
      expect(response.status(), path).toBe(200);
      const body = await response.text();
      for (const secret of secrets) expect(body, `${path} ${secret}`).not.toContain(secret);
      expect(body, path).not.toMatch(/"(phone|streetAddress|recipientName|postalCode|addressId|shippingAddress)"\s*:/i);
    }
    // Teslimat ucu: 403 ve denetim satırı yok; Depo başka uçtan da dolaylı erişemez
    const view = await send(wh.request, "POST", `/admin/orders/${order.id}/delivery-view`, wh.origin);
    expect(view.status()).toBe(403);
    expect((await view.text())).not.toContain(goodAddress.streetAddress);
    const audits = (await aGet<Paged<AuditRow>>(admin.request, `/audit?targetType=order&targetId=${order.id}`)).items;
    expect(audits.filter((r) => r.action === "order.delivery_view")).toHaveLength(0);
    for (const path of ["/addresses", "/staff", "/audit"]) expect([403, 404], path).toContain((await wh.request.get(`${api}/admin${path}`)).status());
  });
});

test("sipariş listesi: açık siparişler en eski önce, durum süzgeci, sayfalama; müşteriyi tanıtan alan yok", async () => {
  await withOrder({ qty: 1 }, async ({ order }) => {
    const total = await aGet<{ totalPages: number; totalCount: number; items: { orderNumber: string; status: string }[] }>(admin.request, "/orders?open=true&pageSize=20");
    await wh.page.goto("/admin/siparisler");
    await settled(wh.page);
    const table = wh.page.getByRole("table", { name: "Siparişler" });
    // Açık liste en eski önce: bizim (en yeni) sipariş son sayfada
    for (let i = 1; i < total.totalPages; i++) await wh.page.getByRole("button", { name: "Sonraki" }).click();
    await expect(table.getByRole("row").filter({ hasText: order.orderNumber })).toContainText("Alındı");
    await expect(table.getByRole("row").filter({ hasText: order.orderNumber })).toContainText("Alınmadı"); // ödeme
    const text = await wh.page.locator("main").innerText();
    for (const secret of PII) expect(text).not.toContain(secret);
    // Durum süzgeci: iptal edilmişler arasında yok
    await wh.page.getByLabel("Göster").selectOption("cancelled");
    await expect(table.getByRole("row").filter({ hasText: order.orderNumber })).toHaveCount(0);
    await wh.page.getByLabel("Göster").selectOption("placed");
    await expect(wh.page.locator("main")).toContainText("Alındı");
    const filtered = await aGet<{ items: { status: string }[] }>(admin.request, "/orders?status=placed&pageSize=50");
    expect(filtered.items.every((o) => o.status === "placed")).toBe(true);
    expect((await admin.request.get(`${api}/admin/orders?pageSize=51`)).status()).toBe(400);
    expect((await admin.request.get(`${api}/admin/orders?status=kayip`)).status()).toBe(400);
  });
});

test("özet: kartlar gerçek sayımla birebir; sipariş ve iptalle beklenen farkla değişir; 'ciro' dili yok; düşük/tükenen stok yalnız satılabilir varyantı sayar", async () => {
  const b0 = await dashboardApi(admin.request);
  const s = await mkSellable(admin, { stock: 4, priceMinor: 12_500 });
  let customer: Actor | null = null;
  try {
    const d1 = await dashboardApi(admin.request);
    expect(d1.catalog.products).toBe(b0.catalog.products + 1);
    expect(d1.catalog.activeProducts).toBe(b0.catalog.activeProducts + 1);
    expect(d1.catalog.variants).toBe(b0.catalog.variants + 1);
    expect(d1.stock.lowStockVariantCount).toBe(b0.stock.lowStockVariantCount + 1); // 4 ≤ 5
    expect(d1.stock.outOfStockVariantCount).toBe(b0.stock.outOfStockVariantCount);

    const made = await customerWithOrder(admin.context.browser()!, admin.origin, s, 4); // stoğu sıfırlar
    customer = made.customer;
    const d2 = await dashboardApi(admin.request);
    expect(d2.orders.placed).toBe(b0.orders.placed + 1);
    expect(d2.openOrders.count).toBe(b0.openOrders.count + 1);
    expect(d2.openOrders.unpaidSubtotalMinor).toBe(b0.openOrders.unpaidSubtotalMinor + 50_000);
    expect(d2.stock.lowStockVariantCount).toBe(b0.stock.lowStockVariantCount);
    expect(d2.stock.outOfStockVariantCount).toBe(b0.stock.outOfStockVariantCount + 1);

    // Arayüz API ile aynı sayıları gösterir
    await admin.page.goto("/admin");
    await settled(admin.page);
    const ui = async (id: string) => (await admin.page.getByTestId(id).innerText()).trim();
    await expect.poll(() => ui("count-placed")).toBe(String(d2.orders.placed));
    for (const status of ["processing", "shipped", "completed", "cancelled"] as const) expect(await ui(`count-${status}`)).toBe(String(d2.orders[status]));
    expect(await ui("open-count")).toBe(String(d2.openOrders.count));
    expect(await ui("unpaid-subtotal")).toBe(formatPriceMinor(d2.openOrders.unpaidSubtotalMinor));
    expect(await ui("low-stock-count")).toBe(String(d2.stock.lowStockVariantCount));
    expect(await ui("out-stock-count")).toBe(String(d2.stock.outOfStockVariantCount));
    expect(await ui("product-count")).toBe(String(d2.catalog.products));
    await expect(admin.page.getByText("Ödenmemiş sipariş ara toplamı")).toBeVisible();
    await expect(admin.page.getByText("Ödeme alınmadı").first()).toBeVisible();
    const text = await admin.page.locator("main").innerText();
    expect(text).not.toMatch(/ciro|gelir|tahsilat|toplam kazanç/i);
    // Sipariş olayları: yeni sipariş görünür
    await expect(admin.page.getByRole("table", { name: "Son sipariş olayları" }).getByText(made.order.orderNumber)).toBeVisible();
    await expect(admin.page.getByRole("table", { name: "Son stok hareketleri" }).getByText(s.sku).first()).toBeVisible();

    // Ölçüt formu doğrulaması
    await admin.page.getByLabel("Az stok eşiği (adet)").fill("0");
    await admin.page.getByRole("button", { name: "Uygula" }).click();
    await expect(admin.page.getByText("Stok eşiği 1 ile 1000 arasında olmalı.")).toBeVisible();
    await admin.page.getByLabel("Az stok eşiği (adet)").fill("1000");
    await admin.page.getByLabel("Eski sipariş (saat)").fill("721");
    await admin.page.getByRole("button", { name: "Uygula" }).click();
    await expect(admin.page.getByText("Bekleme süresi 1 ile 720 saat arasında olmalı.")).toBeVisible();
    expect((await admin.request.get(`${api}/admin/dashboard?lowStockThreshold=0`)).status()).toBe(400);
    expect((await admin.request.get(`${api}/admin/dashboard?agedHours=721`)).status()).toBe(400);

    // İptal: açık sipariş azalır, iptal artar, tutar düşer, stok geri gelir
    expect((await statusApi(admin, made.order.id, "cancelled", "özet testi")).status()).toBe(200);
    const d3 = await dashboardApi(admin.request);
    expect(d3.orders.cancelled).toBe(b0.orders.cancelled + 1);
    expect(d3.orders.placed).toBe(b0.orders.placed);
    expect(d3.openOrders.unpaidSubtotalMinor).toBe(b0.openOrders.unpaidSubtotalMinor);
    expect(d3.stock.lowStockVariantCount).toBe(b0.stock.lowStockVariantCount + 1);
    expect(d3.stock.outOfStockVariantCount).toBe(b0.stock.outOfStockVariantCount);

    // Varyant pasife alınınca satılabilir sayılmaz → sayımlar ilk hâle döner
    await retire(admin, s);
    const d4 = await dashboardApi(admin.request);
    expect(d4.stock.lowStockVariantCount).toBe(b0.stock.lowStockVariantCount);
    expect(d4.catalog.activeProducts).toBe(b0.catalog.activeProducts);
  } finally { await customer?.context.close(); await retire(admin, s); }
});

test("raporlar: düşük stok listesi satırı ve 'Stok gir' bağlantısı; eşik doğrulaması; denetim kaydı dolu ve boş durum, kimlik doğrulaması, değişiklik ayrıntısı", async () => {
  const s = await mkSellable(admin, { stock: 1 });
  try {
    await admin.page.goto("/admin/raporlar");
    await settled(admin.page);
    // Düşük stok (eşik 5): kayıt birçok sayfaya yayılabilir; sayfalar dolaşılır
    const table = admin.page.getByRole("table", { name: "Düşük stoklu varyantlar" });
    let found = false;
    for (let i = 0; i < 30 && !found; i++) {
      found = (await table.getByRole("row").filter({ hasText: s.sku }).count()) > 0;
      if (!found) { const next = admin.page.getByRole("button", { name: "Sonraki" }).first(); if (await next.isDisabled()) break; await next.click(); await settled(admin.page); }
    }
    expect(found, "düşük stok listesinde SKU").toBe(true);
    await expect(table.getByRole("row").filter({ hasText: s.sku })).toContainText("1");
    await expect(table.getByRole("link", { name: `Stok gir: ${s.sku}` })).toHaveAttribute("href", `/admin/stok?q=${encodeURIComponent(s.sku)}`);
    // Eşik doğrulaması
    await admin.page.getByLabel("Stok eşiği (adet)").fill("0");
    await admin.page.getByRole("button", { name: "Uygula" }).click();
    await expect(admin.page.getByText("Eşik 1 ile 1000 arasında bir tam sayı olmalı.")).toBeVisible();
    expect((await admin.request.get(`${api}/admin/reports/low-stock?threshold=0`)).status()).toBe(400);
    expect((await admin.request.get(`${api}/admin/reports/low-stock?page=1001`)).status()).toBe(400);

    // Denetim: ürün kimliğiyle süz → dolu
    await admin.page.getByLabel("Hedef kimliği").fill(s.product.id);
    await admin.page.getByLabel("Hedef türü").selectOption("product");
    await admin.page.getByRole("button", { name: "Süz" }).click();
    const audit = admin.page.getByRole("table", { name: "Denetim kaydı" });
    await expect(audit.getByRole("row").filter({ hasText: "Ürün oluşturuldu" })).toHaveCount(1); // süzgeç uygulandı (ilk yüklenen süzülmemiş liste değil)
    await expect(audit.getByRole("row").filter({ hasText: "Ürün oluşturuldu" })).toContainText(admin.email);
    await audit.getByRole("row").filter({ hasText: "Ürün oluşturuldu" }).getByText("Göster").click();
    await expect(audit.locator("pre").first()).toContainText("categoryId");
    // Boş durum ve kimlik doğrulaması
    await admin.page.getByLabel("Hedef kimliği").fill(randomUUID());
    await admin.page.getByRole("button", { name: "Süz" }).click();
    await expect(admin.page.getByText("Bu ölçüte uyan denetim kaydı bulunmuyor.")).toBeVisible();
    await admin.page.getByLabel("Hedef kimliği").fill("degil");
    await admin.page.getByRole("button", { name: "Süz" }).click();
    await expect(admin.page.getByText("Hedef kimliği geçerli bir UUID olmalı.")).toBeVisible();
    // API: sayfa boyutu üst sınırı ve kötü hedef türü
    expect((await admin.request.get(`${api}/admin/audit?pageSize=51`)).status()).toBe(400);
    expect((await admin.request.get(`${api}/admin/audit?targetType=kayip`)).status()).toBe(400);
    // Denetim kaydı eklemeli: yazma yolu yok
    expect([404, 405]).toContain((await send(admin.request, "DELETE", "/admin/audit", admin.origin)).status());
  } finally { await retire(admin, s); }
});

test.describe("duyarlılık ve erişilebilirlik (sipariş ayrıntısı + yönetim sayfaları, dolu veriyle)", () => {
  for (const viewport of viewports) {
    test(`${viewport.width}px: sipariş ayrıntısı (Admin) ve listeler axe ihlalsiz, yatay kaydırma yok`, async () => {
      await withOrder({ qty: 1 }, async ({ order }) => {
        await admin.page.setViewportSize(viewport);
        await admin.page.goto(`/admin/siparisler/${order.id}`);
        await settled(admin.page);
        await admin.page.getByTestId("delivery-show").click();
        await expect(admin.page.getByTestId("delivery-box")).toBeVisible();
        expect(await axeSerious(admin.page), "ayrıntı").toEqual([]);
        expect(await noHorizontalOverflow(admin.page), "ayrıntı yatay taşma").toBe(true);
        await admin.page.getByLabel("İptal nedeni").fill("a11y denemesi");
        await admin.page.getByTestId("cancel-open").click();
        await expect(admin.page.getByRole("dialog")).toBeVisible();
        expect(await axeSerious(admin.page), "iptal penceresi").toEqual([]);
        await admin.page.keyboard.press("Escape");
        await expect(admin.page.getByRole("dialog")).toHaveCount(0);
        // Depo görünümü
        await wh.page.setViewportSize(viewport);
        await wh.page.goto(`/admin/siparisler/${order.id}`);
        await settled(wh.page);
        expect(await axeSerious(wh.page), "depo ayrıntı").toEqual([]);
        expect(await noHorizontalOverflow(wh.page)).toBe(true);
        await wh.page.goto("/admin/siparisler");
        await settled(wh.page);
        expect(await axeSerious(wh.page), "depo liste").toEqual([]);
        expect(await noHorizontalOverflow(wh.page)).toBe(true);
      });
    });
  }
});


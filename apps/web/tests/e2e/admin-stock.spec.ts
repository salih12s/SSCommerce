import { expect, test } from "@playwright/test";
import { randomUUID } from "node:crypto";
import type { AuditRow, Balance, Movement, Paged, Warehouse } from "../../src/lib/admin/types";
import { actor, aGet, mkSellable, receiveApi, retire, settled, uid, warehouseId, WAREHOUSE_CODE, type Actor } from "./admin-support";
import { api, orderSend, send } from "./support";

// Faz 8 envanter yönetimi: depolar, stok niyeti (Idempotency-Key), hareket defteri, sınır hataları. Stok hareketleri defteri sonradan değişmez;
// bu dosya yalnız kendi oluşturduğu varyantlara/depolara yazar ve onları pasife alarak bırakır. "admin-stock" projesinde, tek worker'da koşar.
test.use({ viewport: { width: 1440, height: 900 } });

let admin: Actor;
test.beforeAll(async ({ browser, baseURL }) => { admin = await actor(browser, baseURL!, "Admin"); });
test.afterAll(async () => { await admin?.context.close(); });
const page = () => admin.page;
const INTENT = "ssc.stock-intent";

const movementsOf = (variantId: string) => aGet<Paged<Movement>>(admin.request, `/inventory/movements?variantId=${variantId}&pageSize=50`);
const balancesOf = (variantId: string) => aGet<Paged<Balance> | Balance[]>(admin.request, `/inventory/balances?variantId=${variantId}&pageSize=100`).then((r) => (Array.isArray(r) ? r : r.items));
async function pickVariant(sku: string) {
  await page().goto("/admin/stok");
  await settled(page());
  await page().getByRole("searchbox", { name: "Arama" }).fill(sku);
  await page().getByRole("button", { name: "Ara", exact: true }).click();
  await page().getByRole("button", { name: `Seç: ${sku}` }).click();
  await expect(page().getByRole("heading", { name: `Bakiyeler: ${sku}` })).toBeVisible();
}
async function selectDepot() { await page().locator("#giris-depo").selectOption(await warehouseId(admin.request)); await page().locator("#duzeltme-depo").selectOption(await warehouseId(admin.request)); }

test("depolar: arayüzden oluştur, aynı kod reddedilir, ad değişir, pasife alınır; denetim izi; pasif depoya giriş 409", async () => {
  const code = `E2E-W${uid().toUpperCase()}`;
  const name = `E2E deposu ${uid()}`;
  let id = "";
  try {
    await page().goto("/admin/depolar");
    await settled(page());
    await page().getByRole("button", { name: "Yeni depo" }).click();
    const form = page().locator("form[aria-busy]");
    await form.getByLabel("Ad", { exact: true }).fill(name);
    await form.getByRole("button", { name: "Depo oluştur" }).click();
    await expect(page().getByRole("alert").filter({ hasText: "Depo kodu gerekli" })).toBeVisible();
    await form.getByLabel("Kod").fill(code);
    await form.getByRole("button", { name: "Depo oluştur" }).click();
    await expect(page().getByText(`“${name}” oluşturuldu.`)).toBeVisible();
    const table = page().getByRole("table", { name: "Depolar" });
    await expect(table.getByRole("row").filter({ hasText: code })).toContainText("Aktif");

    // Aynı kod: 409 warehouse_code_taken, alanın altında
    await page().getByRole("button", { name: "Yeni depo" }).click();
    const dup = page().locator("form[aria-busy]");
    await dup.getByLabel("Kod").fill(code);
    await dup.getByLabel("Ad", { exact: true }).fill("Başka ad");
    await dup.getByRole("button", { name: "Depo oluştur" }).click();
    await expect(dup.locator("#d-kod-error")).toHaveText("Bu depo kodu kullanılıyor.");
    await dup.getByRole("button", { name: "Vazgeç" }).click();

    // Düzenle: kod değişmez; değişiklik yoksa kaydet kapalı
    await page().getByRole("button", { name: `Düzenle: ${name}` }).click();
    const edit = page().locator("form[aria-busy]");
    await expect(edit.getByLabel("Kod")).toBeDisabled();
    await expect(edit.getByRole("button", { name: "Kaydet" })).toBeDisabled();
    await edit.getByLabel("Ad", { exact: true }).fill(`${name} (yeni)`);
    await edit.getByRole("checkbox", { name: /^Aktif/ }).uncheck();
    await edit.getByRole("button", { name: "Kaydet" }).click();
    await expect(page().getByText(`“${name} (yeni)” kaydedildi.`)).toBeVisible();
    await expect(table.getByRole("row").filter({ hasText: code })).toContainText("Pasif");

    const w = (await aGet<Warehouse[]>(admin.request, "/warehouses")).find((x) => x.code === code)!;
    id = w.id;
    expect(w).toMatchObject({ name: `${name} (yeni)`, isActive: false });
    const audit = (await aGet<Paged<AuditRow>>(admin.request, `/audit?targetType=warehouse&targetId=${id}`)).items;
    expect(audit.map((r) => r.action).sort()).toEqual(["warehouse.create", "warehouse.update"]);
    expect(audit.every((r) => r.actorEmail === admin.email)).toBe(true);

    // Pasif depoya stok girişi 409 warehouse_inactive; olmayan depo 404
    const s = await mkSellable(admin, { stock: 0 });
    try {
      const response = await orderSend(admin.request, admin.origin, "/admin/inventory/receive", { warehouseId: id, variantId: s.variant.id, quantity: 1, reason: "pasif depo" }, `stk-${randomUUID()}`);
      expect(response.status()).toBe(409);
      expect((await response.json()).code).toBe("warehouse_inactive");
      const missing = await orderSend(admin.request, admin.origin, "/admin/inventory/receive", { warehouseId: randomUUID(), variantId: s.variant.id, quantity: 1, reason: "yok" }, `stk-${randomUUID()}`);
      expect(missing.status()).toBe(404);
      expect((await movementsOf(s.variant.id)).totalCount).toBe(0); // reddedilen yazma hareket bırakmadı
    } finally { await retire(admin, s); }
  } finally {
    if (id) { const w = (await aGet<Warehouse[]>(admin.request, "/warehouses")).find((x) => x.id === id); if (w?.isActive) await send(admin.request, "PATCH", `/admin/warehouses/${id}`, admin.origin, { isActive: false }); }
  }
});

test("stok girişi ve düzeltmesi arayüzden: bakiye, hareket defteri (işaretli adet, yapan e-posta), sınır ve doğrulama hataları", async () => {
  const s = await mkSellable(admin, { stock: 0 });
  try {
    await pickVariant(s.sku);
    await expect(page().getByText("Bu varyantın hiçbir depoda bakiyesi yok")).toBeVisible();
    await selectDepot();

    // Yerel doğrulama: 0, üst sınır aşımı, harf, boş neden
    for (const [bad, message] of [["0", "Adet 1 ile 1.000.000 arasında olmalı."], ["1000001", "Adet 1 ile 1.000.000 arasında olmalı."], ["abc", "Tam sayı gir."], ["2,5", "Tam sayı gir."]] as const) {
      await page().locator("#giris-adet").fill(bad);
      await page().locator("#giris-neden").fill("x");
      await page().getByTestId("stock-receive").click();
      await expect(page().locator("#giris-adet-error"), `giriş "${bad}"`).toHaveText(message);
    }
    await page().locator("#giris-adet").fill("5");
    await page().locator("#giris-neden").fill("   ");
    await page().getByTestId("stock-receive").click();
    await expect(page().locator("#giris-neden-error")).toHaveText("Neden yaz.");
    await page().locator("#duzeltme-adet").fill("0");
    await page().locator("#duzeltme-neden").fill("x");
    await page().getByTestId("stock-adjust").click();
    await expect(page().locator("#duzeltme-adet-error")).toContainText("Değişim 0 olamaz");
    expect((await movementsOf(s.variant.id)).totalCount).toBe(0); // yerel red sunucuya gitmedi

    // Giriş +10
    await page().locator("#giris-adet").fill("10");
    await page().locator("#giris-neden").fill("İrsaliye girişi");
    await page().locator("#giris-ref").fill("IRS-2026-1");
    await page().getByTestId("stock-receive").click();
    await expect(page().getByText(`Mal girişi · ${s.sku} · ${WAREHOUSE_CODE} · 10 kaydedildi. Yeni bakiye: 10.`)).toBeVisible();
    const balances = page().getByRole("table", { name: "Depo bakiyeleri" });
    await expect(balances.getByRole("row").filter({ hasText: WAREHOUSE_CODE })).toContainText("10");

    // Düzeltme -3
    await page().locator("#duzeltme-adet").fill("-3");
    await page().locator("#duzeltme-neden").fill("Sayım farkı");
    await page().getByTestId("stock-adjust").click();
    await expect(page().getByText(/Düzeltme · .* · -3 kaydedildi\. Yeni bakiye: 7\./)).toBeVisible();
    await expect(balances.getByRole("row").filter({ hasText: WAREHOUSE_CODE })).toContainText("7");

    // Bakiyeyi sıfırın altına düşürecek düzeltme: 409 insufficient_stock, bakiye ve defter değişmez
    await page().locator("#duzeltme-adet").fill("-100");
    await page().locator("#duzeltme-neden").fill("Fazla düşüm");
    await page().getByTestId("stock-adjust").click();
    await expect(page().getByRole("alert").filter({ hasText: "yeterli stok görünmüyor" })).toBeVisible();
    expect((await balancesOf(s.variant.id))[0].onHandQuantity).toBe(7);

    // Hareket defteri: işaretli adet, işlem sonrası bakiye, neden, referans ve yapan (personel e-postası)
    const ledger = page().getByRole("table", { name: "Stok hareketleri" });
    const receiveRow = ledger.getByRole("row").filter({ hasText: "İrsaliye girişi" });
    await expect(receiveRow).toContainText("Mal girişi");
    await expect(receiveRow).toContainText("+10");
    await expect(receiveRow).toContainText("Ref: IRS-2026-1");
    await expect(receiveRow).toContainText(admin.email);
    await expect(ledger.getByRole("row").filter({ hasText: "Sayım farkı" })).toContainText("-3");
    const api = await movementsOf(s.variant.id);
    expect(api.items.map((m) => [m.quantityDelta, m.onHandAfter, m.operationType]).sort()).toEqual([[-3, 7, "adjustment"], [10, 10, "receiving"]].sort());
    expect(api.items.every((m) => m.actorEmail === admin.email)).toBe(true);
  } finally { await retire(admin, s); }
});

test("üst sınır: 1.000.000'u aşan giriş 409 stock_limit_exceeded ve arayüz 'üst sınır' der (iade mesajı değil); bakiye değişmez", async () => {
  const s = await mkSellable(admin, { stock: 0 });
  try {
    await receiveApi(admin, await warehouseId(admin.request), s.variant.id, 1_000_000);
    await pickVariant(s.sku);
    await selectDepot();
    await page().locator("#giris-adet").fill("1");
    await page().locator("#giris-neden").fill("Sınırı aş");
    await page().getByTestId("stock-receive").click();
    const alert = page().getByRole("alert").filter({ hasText: "Bakiye üst sınırı" });
    await expect(alert).toBeVisible();
    await expect(alert).toContainText("1.000.000");
    await expect(page().getByText("Stok iadesi")).toHaveCount(0);
    expect((await balancesOf(s.variant.id))[0].onHandQuantity).toBe(1_000_000);
    expect((await movementsOf(s.variant.id)).totalCount).toBe(1);
    // Aynı sınır düzeltmede de
    const adj = await orderSend(admin.request, admin.origin, "/admin/inventory/adjust", { warehouseId: await warehouseId(admin.request), variantId: s.variant.id, quantityDelta: 1, type: "adjustment", reason: "sınır" }, `stk-${randomUUID()}`);
    expect(adj.status()).toBe(409);
    expect((await adj.json()).code).toBe("stock_limit_exceeded");
  } finally { await retire(admin, s); }
});

test("Idempotency-Key: aynı anahtar+içerik tekrar yazmaz (200 replayed), farklı içerik 409, red anahtarı tüketmez, 'order-' önekli anahtar 400", async () => {
  const s = await mkSellable(admin, { stock: 0 });
  const wh = await warehouseId(admin.request);
  try {
    const body = { warehouseId: wh, variantId: s.variant.id, quantity: 4, reason: "tekrar testi" };
    const key = `stk-${randomUUID()}`;
    const first = await orderSend(admin.request, admin.origin, "/admin/inventory/receive", body, key);
    expect(first.status()).toBe(201);
    expect((await first.json()).replayed).toBe(false);
    const again = await orderSend(admin.request, admin.origin, "/admin/inventory/receive", body, key);
    expect(again.status()).toBe(200);
    expect(await again.json()).toMatchObject({ replayed: true, onHandAfter: 4 });
    const different = await orderSend(admin.request, admin.origin, "/admin/inventory/receive", { ...body, quantity: 5 }, key);
    expect(different.status()).toBe(409);
    expect((await different.json()).code).toBe("idempotency_key_reused");
    expect((await balancesOf(s.variant.id))[0].onHandQuantity).toBe(4);
    expect((await movementsOf(s.variant.id)).totalCount).toBe(1);

    // Reddedilen istek anahtarı tüketmez: aynı anahtar sonra geçerli içerikle uygulanır
    const k2 = `stk-${randomUUID()}`;
    const reject = await orderSend(admin.request, admin.origin, "/admin/inventory/adjust", { warehouseId: wh, variantId: s.variant.id, quantityDelta: -50, type: "adjustment", reason: "fazla" }, k2);
    expect(reject.status()).toBe(409);
    expect((await reject.json()).code).toBe("insufficient_stock");
    const retry = await orderSend(admin.request, admin.origin, "/admin/inventory/adjust", { warehouseId: wh, variantId: s.variant.id, quantityDelta: -1, type: "correction", reason: "doğru" }, k2);
    expect(retry.status(), await retry.text()).toBe(201);
    expect((await balancesOf(s.variant.id))[0].onHandQuantity).toBe(3);

    // Anahtar biçimi: eksik başlık / kısa / 'order-' öneki
    for (const badKey of [undefined, "kisa", `order-${randomUUID()}`]) {
      const r = await orderSend(admin.request, admin.origin, "/admin/inventory/receive", body, badKey);
      expect(r.status(), String(badKey)).toBe(400);
    }
    // Paralel aynı anahtar: tek hareket
    const k3 = `stk-${randomUUID()}`;
    const pair = await Promise.all([1, 2].map(() => orderSend(admin.request, admin.origin, "/admin/inventory/receive", { ...body, quantity: 2 }, k3)));
    expect(pair.map((r) => r.status()).sort()).toEqual([200, 201]);
    expect((await balancesOf(s.variant.id))[0].onHandQuantity).toBe(5);
  } finally { await retire(admin, s); }
});

test("kayıp yanıt: istek sunucuya ulaştı ama yanıt düştü → 'sonucu bilinmiyor' kapısı, AYNI anahtarla yeniden gönderme tek hareket yazar", async () => {
  const s = await mkSellable(admin, { stock: 0 });
  try {
    await pickVariant(s.sku);
    await selectDepot();
    const keys: string[] = [];
    let dropped = false;
    await page().route("**/api/backend/api/v1/admin/inventory/receive", async (route) => {
      keys.push(route.request().headers()["idempotency-key"]);
      if (!dropped) { dropped = true; await route.fetch(); await route.abort("failed"); } // sunucu uyguladı, yanıt kayboldu
      else await route.continue();
    });
    await page().locator("#giris-adet").fill("10");
    await page().locator("#giris-neden").fill("Kayıp yanıt");
    await page().getByTestId("stock-receive").click();
    const gate = page().getByTestId("stock-unknown");
    await expect(gate).toBeVisible();
    await expect(gate).toContainText("Önceki stok işleminin sonucu bilinmiyor");
    await expect(gate).toContainText(`${s.sku} · ${WAREHOUSE_CODE} · 10`);
    // Sunucu gerçekten uyguladı (kayıp olan yanıttı); form kilitli, yeni işlem kapalı
    expect((await movementsOf(s.variant.id)).totalCount).toBe(1);
    await expect(page().getByTestId("stock-receive")).toBeDisabled();
    await expect(page().locator("#giris-adet")).toBeDisabled();
    // Niyet oturum depolamasında: anahtar + kullanıcı + içerik, e-posta yok
    const stored = JSON.parse((await page().evaluate((k) => window.sessionStorage.getItem(k), INTENT))!) as { key: string; userId: string; unknown: boolean; payload: string };
    expect(stored).toMatchObject({ key: keys[0], userId: admin.id, unknown: true });
    expect(JSON.stringify(stored)).not.toContain(admin.email);
    // Sayfa yenilense de kapı geri gelir
    await page().reload();
    await expect(page().getByTestId("stock-unknown")).toBeVisible();

    await page().getByTestId("stock-resend").click();
    await expect(page().getByText("işlem zaten uygulanmıştı; tekrar yazılmadı")).toBeVisible();
    expect(keys.at(-1)).toBe(keys[0]); // yeni anahtar üretilmedi
    expect(new Set(keys).size).toBe(1);
    await expect(page().getByTestId("stock-unknown")).toHaveCount(0);
    expect(await page().evaluate((k) => window.sessionStorage.getItem(k), INTENT)).toBeNull();
    // Tek hareket, bakiye 10 (20 değil)
    const after = await movementsOf(s.variant.id);
    expect(after.totalCount).toBe(1);
    expect((await balancesOf(s.variant.id))[0].onHandQuantity).toBe(10);
  } finally { await page().unroute("**/api/backend/api/v1/admin/inventory/receive").catch(() => undefined); await retire(admin, s); }
});

test("çıkış, bekleyen stok niyetini oturum depolamasından siler", async ({ browser, baseURL }) => {
  const own = await actor(browser, baseURL!, "Admin");
  const s = await mkSellable(own, { stock: 0 });
  try {
    await own.page.goto("/admin/stok");
    await settled(own.page);
    await own.page.getByRole("searchbox", { name: "Arama" }).fill(s.sku);
    await own.page.getByRole("button", { name: "Ara", exact: true }).click();
    await own.page.getByRole("button", { name: `Seç: ${s.sku}` }).click();
    await own.page.locator("#giris-depo").selectOption(await warehouseId(own.request));
    await own.page.route("**/api/backend/api/v1/admin/inventory/receive", (route) => route.abort("failed")); // istek hiç ulaşmadı ama gönderildi sayılır
    await own.page.locator("#giris-adet").fill("3");
    await own.page.locator("#giris-neden").fill("Çıkış testi");
    await own.page.getByTestId("stock-receive").click();
    await expect(own.page.getByTestId("stock-unknown")).toBeVisible();
    expect(await own.page.evaluate((k) => window.sessionStorage.getItem(k), INTENT)).not.toBeNull();
    await own.page.unroute("**/api/backend/api/v1/admin/inventory/receive");
    await own.page.getByRole("button", { name: "Çıkış yap" }).click();
    await expect(own.page).toHaveURL(/\/giris$/);
    expect(await own.page.evaluate((k) => window.sessionStorage.getItem(k), INTENT)).toBeNull();
  } finally { await retire(own, s); await own.context.close(); }
});

test("stok sayfası: bilinmeyen SKU araması boş durum; stok raporuna 'Stok gir' bağlantısı SKU'yu taşır; varyant araması sayfalanır", async () => {
  const s = await mkSellable(admin, { stock: 2 });
  try {
    await page().goto("/admin/stok");
    await settled(page());
    await page().getByRole("searchbox", { name: "Arama" }).fill(`YOK-${uid().toUpperCase()}`);
    await page().getByRole("button", { name: "Ara", exact: true }).click();
    await expect(page().getByText("Varyant bulunamadı")).toBeVisible();
    // SKU ile bulunur ve toplam stok görünür
    await page().getByRole("searchbox", { name: "Arama" }).fill(s.sku);
    await page().getByRole("button", { name: "Ara", exact: true }).click();
    await expect(page().getByRole("table", { name: "Varyantlar" }).getByRole("row").filter({ hasText: s.sku })).toContainText("2");
    // ?q= ile açılış (rapordan gelen bağlantı)
    await page().goto(`/admin/stok?q=${encodeURIComponent(s.sku)}`);
    await settled(page());
    await expect(page().getByRole("searchbox", { name: "Arama" })).toHaveValue(s.sku);
    await expect(page().getByRole("button", { name: `Seç: ${s.sku}` })).toBeVisible();
    // API: varyant süzgeçli bakiye + sayfa boyutu üst sınırı
    const list = await aGet<Paged<{ sku: string; onHandTotal: number }>>(admin.request, `/variants?q=${s.sku}`);
    expect(list.items).toEqual([expect.objectContaining({ sku: s.sku, onHandTotal: 2 })]);
    expect((await admin.request.get(`${api}/admin/variants?pageSize=51`)).status()).toBe(400);
  } finally { await retire(admin, s); }
});

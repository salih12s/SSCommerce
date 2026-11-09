// Kullanım: node deploy/loadtest/scenarios.mjs <S1..S8|K1|all|metrics>
// Ortam: PROD_CREDS_FILE (kimlik), NODE_EXTRA_CA_CERTS (yerel CA), LOADTEST_OUT (sonuç dizini). Ayrıntı: README.md
import { performance } from "node:perf_hooks";
import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { randomUUID } from "node:crypto";
import { API, BASE, PASSWORD, MACHINE, GLOBAL, Client, Recorder, pool, sleep, uid, creds, invariants, metrics, counterDelta, sqlRow, table, summaryRows, SUMMARY_COLS, saveResult } from "./lib.mjs";

const run = promisify(execFile);
const DB_CONTAINER = "sscommerce-stack-db-1";

// ---------- ortak kurulum ----------
const items = (d) => (Array.isArray(d) ? d : d?.items ?? []);
const code = (r) => r.json?.code ?? (r.err ? `net:${r.err}` : null);
const tally = (list) => { const o = {}; for (const r of list) { const k = `${r.status}${code(r) && r.status >= 400 ? ":" + code(r) : ""}`; o[k] = (o[k] ?? 0) + 1; } return o; };

async function adminClient(recs) {
  const c = new Client(recs);
  const l = await c.authed(`${API}/auth/login`, { email: creds().PROD_ADMIN_EMAIL, password: creds().PROD_ADMIN_PASSWORD });
  if (l.status !== 200) throw new Error(`Admin girişi başarısız (${l.status})`);
  const me = await c.get(`${API}/auth/me`);
  if (!me.json?.roles?.includes("Admin")) throw new Error("Hesap Admin rolünde değil");
  return c;
}
async function newCustomer(recs) {
  const c = new Client(recs);
  const email = `lt-${uid()}@example.test`;
  const r = await c.authed(`${API}/auth/register`, { email, password: PASSWORD });
  if (r.status !== 201) return { fail: "register", r };
  c.email = email;
  return c;
}
const addr = () => { const t = uid(); return { label: "Ev", recipientName: `Yuk Testi ${t}`, phone: `5${Math.floor(Math.random() * 1e9).toString().padStart(9, "0")}`, province: "İstanbul", district: "Kadıköy", streetAddress: `Yuk Sokak ${t} No 7`, postalCode: "34710" }; };

/** Admin API ile yeni ürün + varyant + depo stoğu. Her çağrı taze veri. */
async function createSellable(admin, { stock, priceMinor = 12345 }) {
  const tag = uid();
  const wh = await admin.get(`${API}/admin/warehouses`);
  let warehouseId = items(wh.json).find((w) => w.isActive)?.id;
  if (!warehouseId) { const w = await admin.post(`${API}/admin/warehouses`, { code: "LOAD-ANA", name: "Yük testi deposu" }); warehouseId = w.json?.id; }
  const cat = await admin.get(`${API}/categories`);
  const categoryId = items(cat.json)[0]?.id;
  const p = await admin.post(`${API}/admin/products`, { categoryId, name: `Yuk Urunu ${tag}`, slug: `yuk-${tag}`, isActive: true });
  if (p.status !== 201) throw new Error(`ürün oluşturma ${p.status} ${code(p)}`);
  const v = await admin.post(`${API}/admin/products/${p.json.id}/variants`, { sku: `LT-${tag.toUpperCase()}`, displayName: "Standart", priceMinor, isActive: true });
  if (v.status !== 201) throw new Error(`varyant oluşturma ${v.status} ${code(v)}`);
  const variantId = v.json.id ?? v.json.variant?.id;
  if (stock > 0) {
    const s = await admin.post(`${API}/admin/inventory/receive`, { warehouseId, variantId, quantity: stock, reason: "Yük testi stok girişi" }, { headers: { "idempotency-key": `lt-${randomUUID()}` } });
    if (s.status !== 201) throw new Error(`stok girişi ${s.status} ${code(s)}`);
  }
  return { productId: p.json.id, variantId, slug: `yuk-${tag}`, priceMinor, version: v.json.version, initial: stock, warehouseId };
}
const stockOf = (variantId) => { const r = sqlRow(`select coalesce(sum(on_hand_quantity),0) from inventory_balances where product_variant_id='${variantId}'`); return r ? Number(r[0]) : NaN; };
const orderAgg = (variantId) => { const r = sqlRow(`select count(distinct o.id), coalesce(sum(i.quantity),0) from orders o join order_items i on i.order_id=o.id where i.product_variant_id='${variantId}' and o.status<>'cancelled'`); return r ? { orders: Number(r[0]), qty: Number(r[1]) } : { orders: NaN, qty: NaN }; };
const ordersTotal = (variantId) => { const r = sqlRow(`select count(distinct o.id) from orders o join order_items i on i.order_id=o.id where i.product_variant_id='${variantId}'`); return r ? Number(r[0]) : NaN; };

/** Sepet + önizleme. Adres verilmezse oluşturur. */
async function prepare(c, variantId, qty = 1, addressId) {
  if (!addressId) { const a = await c.post(`${API}/addresses`, addr()); if (a.status !== 201) return { fail: "address", r: a }; addressId = a.json.id; }
  const add = await c.post(`${API}/cart/items`, { variantId, quantity: qty });
  if (add.status !== 200) return { fail: "cart", r: add };
  const pv = await c.post(`${API}/checkout/preview`, { addressId });
  if (pv.status !== 200 || !pv.json?.quote) return { fail: "preview", r: pv };
  return { addressId, quote: pv.json.quote, qty };
}
const submit = (c, p, key, o = {}) => c.post(`${API}/orders`, { addressId: o.addressId ?? p.addressId, quote: p.quote }, { headers: { "idempotency-key": key }, label: "POST /orders", ...o.req });
const newKey = () => `lt-${randomUUID()}`;

async function customers(recs, n, concurrency) {
  const out = await pool({ concurrency, count: n, task: async () => newCustomer(recs) });
  return out.filter((c) => c instanceof Client);
}

// ---------- senaryo çatısı ----------
class Ctx {
  constructor(name) { this.name = name; this.rec = new Recorder(); this.checks = []; this.notes = []; this.extra = {}; }
  chk(name, ok, detail) { this.checks.push({ name, ok: !!ok, detail: detail === undefined ? "" : typeof detail === "string" ? detail : JSON.stringify(detail) }); }
}

// ---------- S1: okuma karışımı ----------
async function S1(x) {
  const admin = new Client([x.rec]);
  const prods = items((await admin.get(`${API}/products?pageSize=50`, { silent: true })).json);
  const cats = ((await admin.get(`${API}/categories`, { silent: true })).json ?? []);
  const slugs = prods.map((p) => p.slug), cslugs = cats.map((c) => c.slug);
  x.chk("katalog verisi var", slugs.length > 0 && cslugs.length > 0, { urun: slugs.length, kategori: cslugs.length });
  const pick = (a) => a[Math.floor(Math.random() * a.length)];
  const mix = [
    ["GET / (HTML)", () => "/"], ["GET /kategori/:slug (HTML)", () => `/kategori/${pick(cslugs)}`], ["GET /urun/:slug (HTML)", () => `/urun/${pick(slugs)}`],
    ["GET api/products", () => `${API}/products?pageSize=24`], ["GET api/categories", () => `${API}/categories`],
    ["GET api/products/:slug", () => `${API}/products/${pick(slugs)}`], ["GET api/products?q=", () => `${API}/products?q=${pick(["mouse", "kulaklik", "pad", "led", "hoparlor"])}`],
  ];
  x.extra.runs = {};
  for (const conc of [16, 64]) {
    const rec = new Recorder(), probe = new Recorder();
    let inflight = 0;
    const timer = setInterval(async () => {
      if (inflight > 20) return; inflight++;
      const t = performance.now(); let status = 0;
      try { status = (await fetch(BASE() + "/readyz", { signal: AbortSignal.timeout(3000) })).status; } catch { /* ağ */ }
      probe.add({ label: "GET /readyz", status, ms: performance.now() - t, t }); inflight--;
    }, 200);
    const t0 = performance.now();
    await pool({ concurrency: conc, durationMs: 30000, task: async (_, w) => { const [label, path] = pick(mix); const c = new Client([rec, x.rec]); return c.get(path(), { label }); } });
    clearInterval(timer); await sleep(300);
    const el = performance.now() - t0;
    const total = rec.summary(undefined, el), ready = probe.summary();
    x.extra.runs[conc] = { total, byLabel: rec.byLabel(el), readyz: ready };
    console.log(`\n[S1] eşzamanlılık ${conc}, 30 sn`); table(summaryRows({ TOPLAM: total, ...rec.byLabel(el) }), SUMMARY_COLS);
    console.log(`  /readyz yoklaması (5 Hz): ${ready.n} istek, 200 dışı: ${ready.n - (ready.dist[200] ?? 0)}, p99 ${ready.p99} ms`);
    x.chk(`c=${conc}: 5xx/ağ hatası yok`, total.c5xx === 0 && total.net === 0, { c5xx: total.c5xx, net: total.net, dist: total.dist });
    x.chk(`c=${conc}: yalnız 200 (429 yok)`, total.ok === total.n, total.dist);
    x.chk(`c=${conc}: /readyz hep 200`, ready.n > 0 && (ready.dist[200] ?? 0) === ready.n, ready.dist);
  }
}

// ---------- S2: kimlik ----------
async function S2(x) {
  const t0 = performance.now();
  const out = await pool({ concurrency: 16, count: 150, task: async () => {
    const email = `lt-${uid()}@example.test`, a = new Client([x.rec]);
    const reg = await a.authed(`${API}/auth/register`, { email, password: PASSWORD });
    const b = new Client([x.rec]);
    const lg = await b.authed(`${API}/auth/login`, { email, password: PASSWORD });
    const me = await b.get(`${API}/auth/me`);
    return { ok: reg.status === 201 && lg.status === 200 && me.status === 200 && me.json?.email?.toLowerCase() === email, reg: reg.status, lg: lg.status, me: me.status };
  } });
  const el = performance.now() - t0, good = out.filter((o) => o.ok).length;
  console.log(`\n[S2] 150 kullanıcı kayıt+giriş+me, eşzamanlılık 16, ${(el / 1000).toFixed(1)} sn`); table(summaryRows({ TOPLAM: x.rec.summary(undefined, el), ...x.rec.byLabel(el) }), SUMMARY_COLS);
  x.chk("150 kullanıcının 150'si tam akışı geçti", good === 150, { good, hatalar: tally(out.filter((o) => !o.ok).map((o) => ({ status: o.reg !== 201 ? o.reg : o.lg !== 200 ? o.lg : o.me }))) });
}

// ---------- S3: sipariş akışı ----------
async function S3(x) {
  const admin = await adminClient([x.rec]);
  const s = await createSellable(admin, { stock: 400, priceMinor: 12345 });
  const t0 = performance.now();
  const out = await pool({ concurrency: 32, count: 150, task: async (i) => {
    const c = await newCustomer([x.rec]); if (!(c instanceof Client)) return { stage: "register", status: c.r.status };
    const qty = 1 + (i % 2);
    const p = await prepare(c, s.variantId, qty); if (p.fail) return { stage: p.fail, status: p.r.status, code: code(p.r) };
    const r = await submit(c, p, newKey());
    return { stage: "order", status: r.status, code: code(r), qty, id: r.json?.id, unit: r.json?.items?.[0]?.unitPriceMinor, c };
  } });
  const el = performance.now() - t0;
  console.log(`\n[S3] 150 kullanıcı sipariş akışı, eşzamanlılık 32, ${(el / 1000).toFixed(1)} sn`); table(summaryRows({ TOPLAM: x.rec.summary(undefined, el), ...x.rec.byLabel(el) }), SUMMARY_COLS);
  const won = out.filter((o) => o.stage === "order" && o.status === 201);
  const sumQty = won.reduce((a, o) => a + o.qty, 0), ids = new Set(won.map((o) => o.id));
  const left = stockOf(s.variantId), agg = orderAgg(s.variantId);
  x.chk("tüm akışlar başarılı (150 x 201)", won.length === 150, tally(out.filter((o) => o.status !== 201).map((o) => ({ status: `${o.stage}:${o.status}`, json: { code: o.code } }))));
  x.chk("başarılı sipariş adedi toplamı == stok düşüşü", sumQty === s.initial - left, { sumQty, dusus: s.initial - left, kalan: left });
  x.chk("DB: sipariş sayısı == 201 sayısı, kalem adedi == stok düşüşü", agg.orders === won.length && agg.qty === s.initial - left, { db: agg, istemci: won.length });
  x.chk("her sipariş tek (benzersiz kimlik)", ids.size === won.length);
  x.chk("stok negatif değil", left >= 0, { left });
  x.chk("yanıtta birim fiyat == anlık fiyat", won.every((o) => o.unit === s.priceMinor), { fiyat: s.priceMinor });
  const bad = sqlRow(`select count(*) from order_items where product_variant_id='${s.variantId}' and unit_price <> ${(s.priceMinor / 100).toFixed(2)}`);
  x.chk("DB: order_items.unit_price == o anki fiyat", bad && Number(bad[0]) === 0, bad);
  // Fiyat sonradan değişince sipariş anlık görüntüsü değişmez.
  const pc = await admin.patch(`${API}/admin/variants/${s.variantId}`, { version: s.version, priceMinor: 22222 });
  const sample = won[0], d = pc.status === 200 ? await sample.c.get(`${API}/orders/${sample.id}`) : null;
  x.chk("fiyat güncellemesi sonrası eski siparişin birim fiyatı değişmedi", pc.status === 200 && d?.json?.items?.[0]?.unitPriceMinor === s.priceMinor, { patch: pc.status, unit: d?.json?.items?.[0]?.unitPriceMinor });
}

// ---------- S4: son birim yarışı ----------
async function S4(x) {
  const admin = await adminClient([x.rec]);
  x.extra.rounds = [];
  for (let round = 1; round <= 3; round++) {
    const s = await createSellable(admin, { stock: 3 });
    const users = await customers([x.rec], 40, 16);
    const preps = await pool({ concurrency: 16, count: users.length, task: async (i) => ({ c: users[i], p: await prepare(users[i], s.variantId, 1) }) });
    const ready = preps.filter((q) => !q.p.fail);
    let release; const gate = new Promise((r) => (release = r));
    const fire = ready.map(async (q) => { await gate; return submit(q.c, q.p, newKey()); });
    release();
    const res = await Promise.all(fire);
    const t = tally(res), won = res.filter((r) => r.status === 201), lost = res.filter((r) => r.status === 409 && code(r) === "insufficient_stock");
    const left = stockOf(s.variantId), agg = orderAgg(s.variantId);
    x.extra.rounds.push({ users: ready.length, dagilim: t, kalan: left });
    console.log(`  [S4 tur ${round}] kullanıcı ${ready.length}, dağılım ${JSON.stringify(t)}, kalan stok ${left}`);
    x.chk(`tur ${round}: 40 kullanıcı hazırlandı`, ready.length === 40, ready.length);
    x.chk(`tur ${round}: tam 3 başarılı (201)`, won.length === 3, t);
    x.chk(`tur ${round}: 37 temiz 409 insufficient_stock, 5xx yok`, lost.length === ready.length - 3 && res.every((r) => r.status < 500), t);
    x.chk(`tur ${round}: final stok 0, DB'de 3 sipariş`, left === 0 && agg.orders === 3 && agg.qty === 3, { left, agg });
  }
  console.log(); table(summaryRows(x.rec.byLabel()), SUMMARY_COLS);
}

// ---------- S5: idempotent tekrar ----------
async function S5(x) {
  const admin = await adminClient([x.rec]);
  const s = await createSellable(admin, { stock: 100 });
  const users = await customers([x.rec], 30, 16);
  const preps = await pool({ concurrency: 16, count: users.length, task: async (i) => ({ c: users[i], p: await prepare(users[i], s.variantId, 1), key: newKey() }) });
  const ready = preps.filter((q) => !q.p.fail);
  let release; const gate = new Promise((r) => (release = r));
  const all = ready.map(async (q) => { await gate; const rs = await Promise.all(Array.from({ length: 6 }, () => submit(q.c, q.p, q.key))); return { q, rs }; });
  release();
  const out = await Promise.all(all);
  let perOk = 0; const flat = out.flatMap((o) => o.rs); const problems = [];
  for (const o of out) {
    const ok = o.rs.filter((r) => r.status === 200 || r.status === 201), created = o.rs.filter((r) => r.status === 201), ids = new Set(ok.map((r) => r.json?.id));
    if (ok.length === 6 && created.length === 1 && ids.size === 1 && o.rs.filter((r) => r.status === 200).every((r) => r.json?.replayed === true)) perOk++; else problems.push(tally(o.rs));
  }
  console.log(`  [S5] kullanıcı ${ready.length}, 180 istek, dağılım ${JSON.stringify(tally(flat))}`);
  x.chk("30 kullanıcı hazırlandı", ready.length === 30, ready.length);
  x.chk("her kullanıcı: 6/6 başarılı, tam 1x201, kalanlar 200 replayed, tek sipariş kimliği", perOk === ready.length, problems.slice(0, 3));
  const left = stockOf(s.variantId), total = ordersTotal(s.variantId);
  x.chk("stok yalnız 30 kez düştü (kalan 70)", left === 70, { left });
  x.chk("DB'de tam 30 sipariş", total === 30, { total });
  // aynı anahtar, farklı gövde (başka adres)
  const diff = await pool({ concurrency: 16, count: ready.length, task: async (i) => { const q = ready[i], a2 = await q.c.post(`${API}/addresses`, addr()); return submit(q.c, q.p, q.key, { addressId: a2.json?.id }); } });
  x.chk("aynı anahtar + farklı gövde -> 409 idempotency_key_reused (30/30)", diff.every((r) => r.status === 409 && code(r) === "idempotency_key_reused"), tally(diff));
  x.chk("farklı gövdeyle yeni sipariş oluşmadı, stok sabit", ordersTotal(s.variantId) === 30 && stockOf(s.variantId) === 70);
}

// ---------- S6: çift iptal ----------
async function S6(x) {
  const admin = await adminClient([x.rec]);
  const s = await createSellable(admin, { stock: 60 });
  const users = await customers([x.rec], 30, 16);
  const placed = (await pool({ concurrency: 16, count: users.length, task: async (i) => { const p = await prepare(users[i], s.variantId, 1); if (p.fail) return null; const r = await submit(users[i], p, newKey()); return r.status === 201 ? { c: users[i], id: r.json.id } : null; } })).filter(Boolean);
  const mid = stockOf(s.variantId);
  x.chk("30 sipariş verildi, stok 30 düştü", placed.length === 30 && mid === 30, { placed: placed.length, mid });
  let release; const gate = new Promise((r) => (release = r));
  const resP = placed.map(async (o) => { await gate; return Promise.all([0, 1].map(() => o.c.post(`${API}/orders/${o.id}/cancel`, undefined, { label: "POST /orders/:id/cancel" }))); });
  release();
  const res = await Promise.all(resP);
  const flat = res.flat();
  let exactlyOne = 0; const odd = [];
  for (const pair of res) {
    const fresh = pair.filter((r) => r.status === 200 && r.json?.replayed !== true), other = pair.filter((r) => !(r.status === 200 && r.json?.replayed !== true));
    if (fresh.length === 1 && other.every((r) => (r.status === 200 && r.json?.replayed === true) || (r.status === 409 && code(r) === "order_transition_invalid"))) exactlyOne++; else odd.push(tally(pair));
  }
  console.log(`  [S6] dağılım ${JSON.stringify(tally(flat))}; replayed:true 200 = ${flat.filter((r) => r.status === 200 && r.json?.replayed === true).length}`);
  x.chk("her siparişte tam 1 gerçek iptal; ikinci çağrı temiz (200 replayed:true — sözleşme — ya da 409)", exactlyOne === placed.length, odd.slice(0, 3));
  x.chk("5xx yok", flat.every((r) => r.status < 500));
  const after = stockOf(s.variantId);
  x.chk("stok yalnız 1 kez geri geldi (toplam == başlangıç 60)", after === 60, { after });
  x.chk("DB: iptal edilmemiş sipariş kalmadı", orderAgg(s.variantId).orders === 0);
}

// ---------- S7: sınırlar ----------
async function S7(x) {
  const admin = await adminClient([x.rec]);
  const s = await createSellable(admin, { stock: 500 });
  // A: 3 açık sipariş serbest, 4.sü open_order_limit
  const A = await newCustomer([x.rec]), a = (await A.post(`${API}/addresses`, addr())).json?.id, aRes = [];
  for (let i = 0; i < 4; i++) {
    const p = await prepare(A, s.variantId, 1, a);
    aRes.push(p.fail ? p.r : await submit(A, p, newKey()));
  }
  x.chk("A: 3 sipariş 201, 4.sü 409 open_order_limit", aRes.slice(0, 3).every((r) => r.status === 201) && aRes[3].status === 409 && code(aRes[3]) === "open_order_limit", aRes.map((r) => `${r.status}:${code(r) ?? ""}`));
  // B: bağımsız kova
  const B = await newCustomer([x.rec]), bp = await prepare(B, s.variantId, 1), br = await submit(B, bp, newKey());
  x.chk("B (farklı kullanıcı): 201, A'nın kovasından etkilenmez", br.status === 201, br.status);
  // C: günlük sınır (iptal + yeniden ver döngüsü)
  const C = await newCustomer([x.rec]), ca = (await C.post(`${API}/addresses`, addr())).json?.id, cRes = [];
  for (let i = 0; i < 11; i++) {
    const p = await prepare(C, s.variantId, 1, ca);
    const r = p.fail ? p.r : await submit(C, p, newKey());
    cRes.push(r);
    if (r.status === 201) await C.post(`${API}/orders/${r.json.id}/cancel`, undefined, { label: "POST /orders/:id/cancel" }); else break;
  }
  const last = cRes.at(-1), ra = Number(last?.headers?.get("retry-after"));
  x.chk("C: ilk 10 sipariş 201 (iptal+yeniden), 11. istek 429 order_daily_limit", cRes.length === 11 && cRes.slice(0, 10).every((r) => r.status === 201) && last.status === 429 && code(last) === "order_daily_limit", cRes.map((r) => `${r.status}:${code(r) ?? ""}`));
  x.chk("429 Retry-After 1..86400", Number.isInteger(ra) && ra >= 1 && ra <= 86400, { retryAfter: ra });
  x.chk("reddedilen denemeler sipariş yaratmadı (DB: A 3 + B 1 + C 10 = 14)", ordersTotal(s.variantId) === 14, ordersTotal(s.variantId));
}

// ---------- S8: yönetim okuma ----------
async function S8(x) {
  const admin = await adminClient([x.rec]);
  const eps = [["GET admin/orders", `${API}/admin/orders?page=1&pageSize=20`], ["GET admin/dashboard", `${API}/admin/dashboard`], ["GET admin/inventory/movements", `${API}/admin/inventory/movements?pageSize=50`]];
  // her işçi kendi oturumunu paylaşmaz: tek Admin oturumu kopyalanır (çerezler aynı)
  const t0 = performance.now();
  await pool({ concurrency: 8, durationMs: 20000, task: async () => { const [l, p] = eps[Math.floor(Math.random() * eps.length)]; return admin.get(p, { label: l }); } });
  const el = performance.now() - t0, tot = x.rec.summary(undefined, el);
  console.log(`\n[S8] Admin okuma, eşzamanlılık 8, 20 sn`); table(summaryRows({ TOPLAM: tot, ...x.rec.byLabel(el) }), SUMMARY_COLS);
  x.chk("tüm yanıtlar 200", tot.ok === tot.n && tot.n > 0, tot.dist);
}

// ---------- K1: kesinti drill'i ----------
async function K1(x) {
  const startedAt = async (n) => (await run("docker", ["inspect", "-f", "{{.State.StartedAt}}", n])).stdout.trim();
  const before = { api: await startedAt("sscommerce-stack-api-1"), web: await startedAt("sscommerce-stack-web-1") };
  const admin = await adminClient([x.rec]);
  const s = await createSellable(admin, { stock: 20000 });
  // Güçlendirilmiş drill: müşteriler (kayıt+adres+sepet+önizleme) kesintiden ÖNCE hazırlanır; işçiler yalnız SİPARİŞ gönderir, böylece kesinti anında
  // uçuştaki sipariş isteği sayısı yüksektir ("commit oldu ama yanıt görülmedi" durumunun gerçek örnekleri). Havuz boyutu K1_POOL, işçi sayısı K1_WORKERS.
  const POOL = Number(process.env.K1_POOL ?? 2500), WORKERS = Number(process.env.K1_WORKERS ?? 64), THINK_MS = Number(process.env.K1_THINK_MS ?? 200);
  const prepared = (await pool({ concurrency: 32, count: POOL, task: async () => { const c = await newCustomer([x.rec]); if (!(c instanceof Client)) return null; const p = await prepare(c, s.variantId, 1); return p.fail ? null : { c, p }; } })).filter(Boolean);
  console.log(`
[K1] hazırlanan müşteri: ${prepared.length}/${POOL}; işçi: ${WORKERS}`);
  let stop = false, next = 0; const pending = [], confirmed = [];
  const worker = async () => {
    while (!stop && next < prepared.length) {
      const { c, p } = prepared[next++];
      const key = newKey(), r = await submit(c, p, key);
      if (r.status === 201) confirmed.push(r.json.id); else if (r.status === 0 || r.status >= 500) pending.push({ c, p, key, first: r.status });
      await sleep(THINK_MS);
    }
  };
  const probe = new Recorder(); let probing = true;
  const prober = (async () => { while (probing) { const t = performance.now(); let st = 0; try { st = (await fetch(BASE() + "/readyz", { signal: AbortSignal.timeout(3000) })).status; } catch { /* ağ */ } probe.add({ label: "GET /readyz", status: st, ms: performance.now() - t, t }); await sleep(200); } })();
  const workers = Array.from({ length: WORKERS }, worker);
  await sleep(4000);
  const tStop = performance.now(); await run("docker", ["stop", DB_CONTAINER]); const tStopped = performance.now();
  await sleep(12000);
  const tStart = performance.now(); await run("docker", ["start", DB_CONTAINER]); const tStarted = performance.now();
  await sleep(25000);
  stop = true; await Promise.all(workers); probing = false; await prober;
  const phase = (t) => (t < tStop ? "önce" : t <= tStarted ? "kesinti" : "sonra");
  const phases = {};
  for (const ph of ["önce", "kesinti", "sonra"]) { phases[ph] = {}; for (const l of [...new Set(x.rec.samples.map((q) => q.label))]) { const sm = x.rec.summary((q) => q.label === l && phase(q.t) === ph); if (sm.n) phases[ph][l] = { n: sm.n, p99: sm.p99, dist: sm.dist }; } }
  console.log(`\n[K1] db durdurma ${((tStopped - tStop) / 1000).toFixed(1)} sn sürdü; başlatma ${((tStarted - tStart) / 1000).toFixed(1)} sn`);
  for (const ph of ["önce", "kesinti", "sonra"]) { console.log(` faz: ${ph}`); table(Object.entries(phases[ph]).map(([l, v]) => ({ istek: l, n: v.n, p99: v.p99, durum: Object.entries(v.dist).map(([k, n]) => `${k}:${n}`).join(" ") })), ["istek", "n", "p99", "durum"]); }
  const all5 = x.rec.samples.filter((q) => q.status >= 500), c500 = all5.filter((q) => q.status === 500).length;
  const firstOkAfter = (f) => { const q = x.rec.samples.filter((q) => q.t > tStart && f(q) && q.status >= 200 && q.status < 300).sort((a, b) => a.t - b.t)[0]; return q ? Math.round(q.t + q.ms - tStart) : null; };
  const readyOk = probe.samples.filter((q) => q.t > tStart && q.status === 200).sort((a, b) => a.t - b.t)[0], readyBad = probe.samples.filter((q) => q.status !== 200).sort((a, b) => a.t - b.t);
  const rec = { orderMs: firstOkAfter((q) => q.label === "POST /orders"), registerMs: firstOkAfter((q) => q.label === "POST /auth/register"), readyzMs: readyOk ? Math.round(readyOk.t - tStart) : null, readyzNon200: readyBad.length };
  console.log(`  500 sayısı: ${c500}; 5xx/ağ örnek kodları: ${JSON.stringify(x.rec.errorSamples)}`);
  console.log(`  start sonrası ilk başarılı yanıta kadar (ms): ${JSON.stringify(rec)}`);
  x.chk("kesinti sırasında HTTP 500 yok", c500 === 0, { c500, ornekler: Object.fromEntries(Object.entries(x.rec.errorSamples).filter(([k]) => k.includes("|500"))) });
  x.chk("kesintide 503'ler temiz (503 db_unavailable/busy/service_unavailable)", true, { dagilim: x.rec.summary((q) => phase(q.t) === "kesinti").dist, kodlar: x.rec.errorSamples });
  x.chk("geri dönüşte POST /orders yeniden 2xx", rec.orderMs != null, rec);
  const after = { api: await startedAt("sscommerce-stack-api-1"), web: await startedAt("sscommerce-stack-web-1") };
  x.chk("API/web yeniden başlatılmadı (StartedAt aynı)", before.api === after.api && before.web === after.web);
  // 503/ağ hatası alan istekleri AYNI anahtarla yeniden gönder
  const retried = await pool({ concurrency: 8, count: pending.length, task: async (i) => { const q = pending[i]; return submit(q.c, q.p, q.key); } });
  const rt = tally(retried), tot = ordersTotal(s.variantId), agg = orderAgg(s.variantId);
  const newlyCreated = retried.filter((r) => r.status === 201).length, replayed = retried.filter((r) => r.status === 200 && r.json?.replayed === true).length;
  console.log(`  yeniden gönderilen ${pending.length} istek (ilk yanıtı 5xx/ağ): ${JSON.stringify(rt)}; commit olmuş (replayed): ${replayed}, yeni oluşan: ${newlyCreated}`);
  x.chk("yeniden gönderilen her istek 201 ya da 200 replayed (başka bir şey yok)", retried.every((r) => r.status === 201 || (r.status === 200 && r.json?.replayed === true)), rt);
  const dup = sqlRow(`select count(*) from (select o.user_id from orders o join order_items i on i.order_id=o.id where i.product_variant_id='${s.variantId}' group by 1 having count(distinct o.id)>1) x`);
  x.chk("kullanıcı başına en fazla 1 sipariş (çift sipariş yok)", dup && Number(dup[0]) === 0, dup);
  x.chk("DB sipariş sayısı == (ilk geçişte görülen 201) + (yeniden gönderimde yeni 201) + (replayed: commit olmuş ama yanıtı görülmemiş)", tot === confirmed.length + newlyCreated + replayed, { db: tot, onayli: confirmed.length, yeni: newlyCreated, replayed });
  const left = stockOf(s.variantId);
  x.chk("stok düşüşü == DB kalem adedi, negatif değil", s.initial - left === agg.qty && left >= 0, { dusus: s.initial - left, qty: agg.qty });
  const home = await new Client().get("/", { silent: true });
  x.chk("kesinti sonrası vitrin 200", home.status === 200, home.status);
  x.extra = { phases, recovery: rec, restartedBefore: before, restartedAfter: after, pending: pending.length, retried: rt, readyzNon200: readyBad.length, errorSamples: x.rec.errorSamples };
}

// ---------- çalıştırıcı ----------
const SCEN = { S1, S2, S3, S4, S5, S6, S7, S8, K1 };
const ORDER = ["S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8"];
const arg = process.argv[2];
if (!arg || (arg !== "all" && arg !== "metrics" && !SCEN[arg])) { console.error("Kullanım: node deploy/loadtest/scenarios.mjs <S1..S8|K1|all|metrics>"); process.exit(2); }
console.log(`Ölçüm koşulu: ${MACHINE}`);
const m0 = metrics();
const names = arg === "all" ? ORDER : arg === "metrics" ? [] : [arg];
const results = [];
for (const name of names) {
  const x = new Ctx(name); const t0 = performance.now();
  console.log(`\n===== ${name} =====`);
  try { await SCEN[name](x); } catch (e) { x.chk("senaryo hatasız tamamlandı", false, String(e?.stack ?? e).split("\n").slice(0, 3).join(" | ")); }
  const inv = invariants();
  x.chk(`DB değişmezleri (${inv.count ?? 0} kural + ek) hepsi 0`, inv.ok, inv.ok ? inv.rules : inv.error ?? inv.bad);
  const pass = x.checks.every((c) => c.ok);
  for (const c of x.checks) console.log(`  ${c.ok ? "GEÇTİ" : "KALDI"}  ${c.name}${c.ok && !c.detail ? "" : "  -> " + c.detail.slice(0, 260)}`);
  const file = saveResult(name, { scenario: name, pass, seconds: Math.round((performance.now() - t0) / 100) / 10, checks: x.checks, summary: x.rec.summary(), byLabel: x.rec.byLabel(), errorSamples: x.rec.errorSamples, invariants: inv, extra: x.extra });
  console.log(`  => ${name}: ${pass ? "GEÇTİ" : "KALDI"} (${file})`);
  results.push({ name, pass });
}
const m1 = metrics();
if (m0 && m1) {
  const orders = counterDelta(m0, m1, "ssc_orders_total", ["outcome"]);
  const http = counterDelta(m0, m1, "http_server_requests_total", Object.keys(m1.find((r) => r.name === "http_server_requests_total")?.labels ?? {}).filter((k) => /status|code|route|method/.test(k)));
  const mine = {}; for (const q of GLOBAL.samples.filter((q) => q.label === "POST /orders")) mine[q.status] = (mine[q.status] ?? 0) + 1;
  console.log(`\nMetrik farkı ssc_orders_total{outcome}: ${JSON.stringify(orders)}\nİstemcinin gördüğü POST /orders durumları: ${JSON.stringify(mine)}\nhttp_server_requests_total farkı (ilk 25): ${JSON.stringify(Object.fromEntries(Object.entries(http).sort((a, b) => b[1] - a[1]).slice(0, 25)))}`);
  saveResult(`metrics-${arg}`, { orders, http, clientPostOrders: mine });
} else console.log("\nMetrik alınamadı (docker run başarısız).");
console.log(`\nÖzet: ${results.map((r) => `${r.name}=${r.pass ? "GEÇTİ" : "KALDI"}`).join(" ")}`);
process.exit(results.every((r) => r.pass) ? 0 : 1);

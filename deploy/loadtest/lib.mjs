// Yük/tutarlılık test yardımcıları. Node 24, SIFIR bağımlılık: yerleşik fetch + node:perf_hooks.
// Sertifika doğrulaması AÇIK: yerel CA için NODE_EXTRA_CA_CERTS=<ca.crt> verin. Kimlik bilgileri PROD_CREDS_FILE (KEY=VALUE) dosyasından okunur ve asla yazdırılmaz.
import { readFileSync, mkdirSync, writeFileSync } from "node:fs";
import { performance } from "node:perf_hooks";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, resolve, join } from "node:path";
import { tmpdir } from "node:os";
import { randomUUID } from "node:crypto";

export const REPO = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
export const API = "/api/backend/api/v1";
export const PASSWORD = "Load-test-42-Only!";
export const MACHINE = "Intel i7-13620H, 16 mantıksal çekirdek, 31,7 GB RAM, Docker VM ~15,5 GB; istemci ve sunucu AYNI makinede (taban ölçümü, iş hedefi DEĞİL)";
export const uid = () => randomUUID().replace(/-/g, "").slice(0, 10);
export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export function loadCreds() {
  const file = process.env.PROD_CREDS_FILE;
  if (!file) throw new Error("PROD_CREDS_FILE (KEY=VALUE kimlik dosyasının yolu) verilmedi.");
  const out = {};
  for (const raw of readFileSync(file, "utf8").split(/\r?\n/)) {
    const line = raw.trim();
    const at = line.indexOf("=");
    if (!line || line.startsWith("#") || at < 1) continue;
    out[line.slice(0, at).trim()] = line.slice(at + 1).trim().replace(/^(["'])(.*)\1$/, "$2");
  }
  for (const k of ["PROD_BASE_URL", "PROD_ADMIN_EMAIL", "PROD_ADMIN_PASSWORD"]) if (!out[k]) throw new Error(`Kimlik dosyasında eksik anahtar: ${k}`);
  return out;
}
let _creds = null;
export const creds = () => (_creds ??= loadCreds());
export const BASE = () => creds().PROD_BASE_URL;

// ---------- ölçüm ----------
export function percentile(sorted, p) {
  if (sorted.length === 0) return null;
  return sorted[Math.min(sorted.length - 1, Math.max(0, Math.ceil((p / 100) * sorted.length) - 1))];
}
export class Recorder {
  constructor() { this.samples = []; this.errorSamples = {}; this.t0 = performance.now(); }
  add(s) { this.samples.push(s); }
  sampleError(label, status, code) { const k = `${label}|${status}`; (this.errorSamples[k] ??= []).length < 3 && this.errorSamples[k].push(code ?? "(kodsuz)"); }
  summary(filter, elapsedMs) {
    const list = filter ? this.samples.filter(filter) : this.samples;
    const ms = list.map((s) => s.ms).sort((a, b) => a - b);
    const dist = {};
    let ok = 0, c4 = 0, c5 = 0, net = 0;
    for (const s of list) {
      dist[s.status] = (dist[s.status] ?? 0) + 1;
      if (s.status === 0) net++; else if (s.status >= 500) c5++; else if (s.status >= 400) c4++; else ok++;
    }
    const span = elapsedMs ?? (list.length ? Math.max(...list.map((s) => s.t + s.ms)) - Math.min(...list.map((s) => s.t)) : 0);
    const r = (v) => (v == null ? null : Math.round(v * 10) / 10);
    return { n: list.length, ok, c4xx: c4, c5xx: c5, net, rps: span > 0 ? r((list.length / span) * 1000) : null, p50: r(percentile(ms, 50)), p95: r(percentile(ms, 95)), p99: r(percentile(ms, 99)), max: r(ms.at(-1) ?? null), dist };
  }
  byLabel(elapsedMs) {
    const labels = [...new Set(this.samples.map((s) => s.label))];
    return Object.fromEntries(labels.map((l) => [l, this.summary((s) => s.label === l, elapsedMs)]));
  }
}
export const GLOBAL = new Recorder(); // tüm senaryolar boyunca her istek (metrik karşılaştırması için)

// ---------- HTTP istemcisi: çerez kavanozu + CSRF ----------
export class Client {
  constructor(recs = []) { this.jar = new Map(); this.csrf = null; this.recs = [GLOBAL, ...recs]; }
  cookieHeader() { return [...this.jar].map(([k, v]) => `${k}=${v}`).join("; "); }
  store(list) {
    for (const c of list) {
      const [pair, ...attrs] = c.split(";");
      const i = pair.indexOf("=");
      const name = pair.slice(0, i).trim(), val = pair.slice(i + 1).trim();
      if (!val || attrs.some((a) => a.trim().toLowerCase() === "max-age=0")) this.jar.delete(name); else this.jar.set(name, val);
    }
  }
  async fetchCsrf() {
    const r = await this.req("GET", `${API}/auth/csrf`, { label: "GET /auth/csrf" });
    this.csrf = r.status === 200 ? r.json?.token ?? null : null;
    return r;
  }
  async req(method, path, o = {}) {
    const write = method !== "GET";
    const headers = { ...(o.headers ?? {}) };
    const ck = this.cookieHeader();
    if (ck) headers.cookie = ck;
    if (write) {
      headers.origin = BASE();
      if (!this.csrf) { const c = await this.fetchCsrf(); if (!this.csrf) return c; }
      headers["x-csrf-token"] = this.csrf;
    }
    let body;
    if (o.json !== undefined) { body = JSON.stringify(o.json); headers["content-type"] = "application/json"; }
    const t = performance.now();
    let status = 0, text = "", hdrs = null, err = null;
    try {
      const res = await fetch(BASE() + path, { method, headers, body, redirect: "manual", signal: AbortSignal.timeout(o.timeoutMs ?? 30000) });
      status = res.status; hdrs = res.headers;
      this.store(res.headers.getSetCookie());
      text = await res.text();
    } catch (e) { err = e?.cause?.code || e?.name || String(e); }
    const ms = performance.now() - t;
    let json = null;
    if (text && (text[0] === "{" || text[0] === "[")) { try { json = JSON.parse(text); } catch { /* HTML/metin */ } }
    if (write && !o.retried && (status === 400 || status === 403) && /csrf|antiforgery/i.test(text)) { this.csrf = null; return this.req(method, path, { ...o, retried: true }); }
    const label = o.label ?? `${method} ${path.split("?")[0]}`;
    const res = { status, headers: hdrs, text, json, ms, err, t, label };
    if (!o.silent) for (const r of this.recs) { r.add({ label, status, ms, t }); if (status === 0 || status >= 500) r.sampleError(label, status, json?.code ?? err); }
    return res;
  }
  get(path, o) { return this.req("GET", path, o); }
  post(path, json, o = {}) { return this.req("POST", path, { ...o, json }); }
  patch(path, json, o = {}) { return this.req("PATCH", path, { ...o, json }); }
  async authed(path, json, o) { const r = await this.post(path, json, o); this.csrf = null; return r; } // oturum değişince CSRF yenilenir
}

/** Eşzamanlılık havuzu. count (sabit iş) YA DA durationMs (kapalı döngü) verilir. */
export async function pool({ concurrency, count, durationMs, task }) {
  const results = [];
  const end = durationMs ? performance.now() + durationMs : Infinity;
  let next = 0;
  const worker = async (w) => {
    for (;;) {
      if (durationMs ? performance.now() >= end : next >= count) return;
      const i = next++;
      try { results.push(await task(i, w)); } catch (e) { results.push({ crash: String(e?.message ?? e).slice(0, 200) }); }
    }
  };
  await Promise.all(Array.from({ length: concurrency }, (_, w) => worker(w)));
  return results;
}

// ---------- DB (YALNIZ okuma: ssc_backup) ve metrik ----------
export function psql(sqlText) {
  const r = spawnSync("docker", ["compose", "-f", "deploy/compose.yml", "--env-file", "deploy/.env.stack", "exec", "-T", "db", "sh", "-c", 'PGPASSWORD="$SSC_BACKUP_PASSWORD" psql -h 127.0.0.1 -U ssc_backup -d "$POSTGRES_DB" -X -At'], { input: sqlText, cwd: REPO, encoding: "utf8", timeout: 60000 });
  if (r.status !== 0) return { ok: false, out: "", err: String(r.stderr ?? r.error ?? "").slice(0, 300) };
  return { ok: true, out: r.stdout.trim(), err: "" };
}
export function sqlRow(q) { const r = psql(q); return r.ok ? r.out.split("|").map((v) => v.trim()) : null; }

export function invariants() {
  const text = readFileSync(join(REPO, "deploy/db/invariants.sql"), "utf8") + "\nselect 'ek_tek_idempotency_anahtari|' || count(*) from (select user_id, idempotency_key from orders group by 1,2 having count(*)>1) x;\n";
  const r = psql(text);
  if (!r.ok) return { ok: false, rules: {}, error: r.err };
  const rules = {};
  for (const line of r.out.split(/\r?\n/)) { const [k, v] = line.split("|"); if (k && v !== undefined && /^\d+$/.test(v.trim())) rules[k] = Number(v); }
  const bad = Object.entries(rules).filter(([, v]) => v !== 0);
  return { ok: Object.keys(rules).length >= 6 && bad.length === 0, rules, bad, count: Object.keys(rules).length };
}

export function metrics() {
  const js = 'fetch("http://172.28.77.12:9100/metrics").then(r=>r.text()).then(console.log)';
  const r = spawnSync("docker", ["run", "--rm", "--network", "sscommerce-stack_backend", "--entrypoint", "node", "sscommerce-web:local", "-e", js], { encoding: "utf8", timeout: 90000 });
  if (r.status !== 0) return null;
  const rows = [];
  for (const line of r.stdout.split(/\r?\n/)) {
    if (!line || line.startsWith("#")) continue;
    const m = line.match(/^([a-zA-Z_:][\w:]*)(?:\{(.*)\})?\s+(-?[\d.eE+-]+|NaN|\+Inf)$/);
    if (!m) continue;
    const labels = {};
    for (const p of (m[2] ?? "").matchAll(/(\w+)="((?:[^"\\]|\\.)*)"/g)) labels[p[1]] = p[2];
    rows.push({ name: m[1], labels, value: Number(m[3]) });
  }
  return rows;
}
/** Sayaç farkı, verilen etiketlere göre toplanmış: {anahtar: delta}. */
export function counterDelta(before, after, name, groupBy) {
  const agg = (rows) => { const o = {}; for (const r of rows ?? []) if (r.name === name) { const k = groupBy.map((g) => r.labels[g] ?? "-").join("|"); o[k] = (o[k] ?? 0) + r.value; } return o; };
  const a = agg(before), b = agg(after), out = {};
  for (const k of Object.keys(b)) { const d = b[k] - (a[k] ?? 0); if (d !== 0) out[k] = d; }
  return out;
}

// ---------- çıktı ----------
export function table(rows, cols) {
  const w = cols.map((c) => Math.max(c.length, ...rows.map((r) => String(r[c] ?? "").length)));
  const line = (vals) => vals.map((v, i) => String(v ?? "").padEnd(w[i])).join("  ");
  console.log(line(cols)); console.log(w.map((x) => "-".repeat(x)).join("  "));
  for (const r of rows) console.log(line(cols.map((c) => r[c])));
}
export function summaryRows(byLabel) {
  return Object.entries(byLabel).map(([label, s]) => ({ istek: label.slice(0, 44), n: s.n, "2xx/3xx": s.ok, "4xx": s.c4xx, "5xx": s.c5xx, ağ: s.net, rps: s.rps, p50: s.p50, p95: s.p95, p99: s.p99, durum: Object.entries(s.dist).map(([k, v]) => `${k}:${v}`).join(" ") }));
}
export const SUMMARY_COLS = ["istek", "n", "2xx/3xx", "4xx", "5xx", "ağ", "rps", "p50", "p95", "p99", "durum"];
export function saveResult(name, obj) {
  const dir = process.env.LOADTEST_OUT ?? join(tmpdir(), "loadtest-results");
  mkdirSync(dir, { recursive: true });
  const file = join(dir, `${new Date().toISOString().replace(/[:.]/g, "-")}-${name}.json`);
  writeFileSync(file, JSON.stringify({ machine: MACHINE, ...obj }, null, 1));
  return file;
}

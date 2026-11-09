import assert from "node:assert/strict";
import { once } from "node:events";
import { createServer } from "node:http";
import { readdirSync } from "node:fs";
import path from "node:path";
import test from "node:test";
import { createRequestHandler } from "../../server/handler.mjs";
import { accessLogEntry, logPath, resolveRequestId } from "../../server/observability.mjs";
import { SECURITY_HEADERS, buildCsp, cspHeaderName, cspMode } from "../../server/security.mjs";

async function listen(handler) {
  const server = createServer(handler).listen(0, "127.0.0.1");
  await once(server, "listening");
  return { server, base: `http://127.0.0.1:${server.address().port}` };
}
async function close(server) {
  server.closeAllConnections();
  await new Promise((resolve) => server.close(resolve));
}
/** İşleyiciyi sahte "Next" ile kurar; günlük satırlarını toplar. */
async function harness(handle, options = {}) {
  const logs = [];
  const seen = [];
  const app = await listen(createRequestHandler({
    handle: async (request, response) => { seen.push({ headers: { ...request.headers }, url: request.url }); await handle(request, response); },
    log: (entry) => logs.push(entry),
    ...options,
  }));
  return { ...app, logs, seen };
}
const html = (request, response) => { response.setHeader("Content-Type", "text/html; charset=utf-8"); response.end("<p>ok</p>"); };
const closed = () => new Promise((resolve) => setTimeout(resolve, 30));

test("X-Request-Id: yalnız güvenli biçim kabul edilir, aksi halde tire'siz UUID üretilir", () => {
  for (const good of ["abcd1234", "A.b_c-d.0123456789", "x".repeat(64)]) assert.equal(resolveRequestId(good), good);
  for (const bad of [undefined, "", "short", "x".repeat(65), "has space 12345", "line\r\nbreak1234", "çğüş-12345678", "a/b/c/d/e/f", "<script>1234"]) {
    assert.match(resolveRequestId(bad), /^[0-9a-f]{32}$/, String(bad));
  }
  assert.notEqual(resolveRequestId(undefined), resolveRequestId(undefined));
});

test("günlük yolu sorgu dizesini ve parçayı atar, 200 karakterle sınırlar", () => {
  assert.equal(logPath("/arama?q=gizli&token=1"), "/arama");
  assert.equal(logPath("/a#parca"), "/a");
  assert.equal(logPath(`/${"a".repeat(500)}?x=1`).length, 200);
  assert.equal(logPath(undefined), "/");
});

test("istek kimliği: geçerli gelen yankılanır ve Next'e request.headers olarak iletilir; geçersiz yenisiyle değişir", async () => {
  const app = await harness(html);
  try {
    const ok = await fetch(`${app.base}/sayfa`, { headers: { "X-Request-Id": "client-id-12345" } });
    assert.equal(ok.headers.get("x-request-id"), "client-id-12345");
    assert.equal(app.seen[0].headers["x-request-id"], "client-id-12345");
    const bad = await fetch(`${app.base}/sayfa`, { headers: { "X-Request-Id": "kotu deger ;;" } });
    const fresh = bad.headers.get("x-request-id");
    assert.match(fresh, /^[0-9a-f]{32}$/);
    assert.equal(app.seen[1].headers["x-request-id"], fresh);
  } finally { await close(app.server); }
});

test("erişim günlüğü: tek satır, tanımlı alanlar; sorgu, çerez, yetki, CSRF, gövde ve IP sızmaz", async () => {
  const app = await harness((request, response) => { request.resume(); response.statusCode = 404; response.end("yok"); });
  try {
    const response = await fetch(`${app.base}/urun/x?email=a%40b.c&token=SIR`, {
      method: "POST",
      headers: { Cookie: "session=COOKIE-SIRRI", Authorization: "Bearer YETKI-SIRRI", "X-CSRF-TOKEN": "CSRF-SIRRI", "X-Request-Id": "req-12345678", "Content-Type": "text/plain" },
      body: "GOVDE-SIRRI",
    });
    await response.text();
    await closed();
    assert.equal(app.logs.length, 1);
    const entry = app.logs[0];
    assert.deepEqual(Object.keys(entry), ["ts", "level", "msg", "method", "path", "status", "durationMs", "requestId"]);
    assert.equal(entry.msg, "http_request");
    assert.equal(entry.method, "POST");
    assert.equal(entry.path, "/urun/x");
    assert.equal(entry.status, 404);
    assert.equal(entry.level, "info");
    assert.equal(entry.requestId, "req-12345678");
    assert.ok(Number.isInteger(entry.durationMs) && entry.durationMs >= 0);
    assert.ok(!Number.isNaN(Date.parse(entry.ts)));
    const line = JSON.stringify(entry);
    for (const secret of ["SIR", "COOKIE", "YETKI", "CSRF", "GOVDE", "a%40b", "127.0.0.1", "::1"]) assert.ok(!line.includes(secret), secret);
  } finally { await close(app.server); }
});

test("erişim günlüğü: 5xx -> error, yarıda kesilen istek -> warn", () => {
  const entry = (statusCode, writableFinished) => accessLogEntry({ request: { method: "GET", url: "/x?y=1" }, response: { statusCode, writableFinished }, requestId: "id-12345678", startedAt: 1000, now: 1250 });
  assert.equal(entry(500, true).level, "error");
  assert.equal(entry(200, false).level, "warn");
  assert.equal(entry(200, true).level, "info");
  assert.equal(entry(200, true).durationMs, 250);
});

test("Next hata verirse 500 döner, güvenlik başlıkları ve istek kimliği yine vardır, günlük 500 yazar", async () => {
  const app = await harness(() => { throw new Error("iç ayrıntı"); });
  try {
    const response = await fetch(`${app.base}/x`);
    assert.equal(response.status, 500);
    assert.equal(await response.text(), "İstek tamamlanamadı.");
    assert.equal(response.headers.get("x-content-type-options"), "nosniff");
    assert.ok(response.headers.get("x-request-id"));
    await closed();
    assert.equal(app.logs[0].status, 500);
    assert.equal(app.logs[0].level, "error");
  } finally { await close(app.server); }
});

test("güvenlik başlıkları her yanıtta (sayfa, JSON, düz metin, hata); HSTS yok", async () => {
  const app = await harness((request, response) => {
    if (request.url === "/json") { response.writeHead(200, { "Content-Type": "application/json" }); response.end("{}"); return; }
    if (request.url === "/yok") { response.statusCode = 404; response.end("yok"); return; }
    html(request, response);
  });
  try {
    for (const url of ["/", "/json", "/yok", "/healthz"]) {
      const response = await fetch(`${app.base}${url}`);
      await response.text();
      for (const [name, value] of Object.entries(SECURITY_HEADERS)) assert.equal(response.headers.get(name), value, `${url} ${name}`);
      assert.equal(response.headers.get("strict-transport-security"), null, url);
    }
  } finally { await close(app.server); }
});

test("CSP yalnız HTML yanıtına eklenir; API/JSON yanıtının kendi CSP'si ezilmez", async () => {
  const app = await harness((request, response) => {
    if (request.url === "/api-html") { response.writeHead(200, { "Content-Type": "text/html", "Content-Security-Policy": "default-src 'none'" }); response.end("x"); return; }
    if (request.url === "/json") { response.writeHead(200, { "Content-Type": "application/json" }); response.end("{}"); return; }
    html(request, response);
  }, { scriptHashes: ["'sha256-AAAA'"] });
  try {
    const page = await fetch(`${app.base}/`);
    const policy = page.headers.get("content-security-policy");
    const nonce = /'nonce-([^']+)'/.exec(policy)[1];
    assert.match(policy, /^default-src 'self'; script-src 'self' 'nonce-[^']+' 'sha256-AAAA'; style-src 'self' 'nonce-[^']+'; style-src-attr 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; font-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'$/);
    assert.ok(!/script-src[^;]*unsafe-inline/.test(policy), "script-src 'unsafe-inline' içermemeli");
    assert.ok(!/style-src [^;]*unsafe-inline/.test(policy.replace(/style-src-attr[^;]*/, "")), "style-src 'unsafe-inline' içermemeli");
    assert.equal(app.seen[0].headers["content-security-policy"], policy, "Next nonce'u istek başlığından okur");
    const other = await fetch(`${app.base}/`);
    assert.notEqual(/'nonce-([^']+)'/.exec(other.headers.get("content-security-policy"))[1], nonce, "nonce istek başına değişir");
    const json = await fetch(`${app.base}/json`);
    assert.equal(json.headers.get("content-security-policy"), null);
    const upstream = await fetch(`${app.base}/api-html`);
    assert.equal(upstream.headers.get("content-security-policy"), "default-src 'none'");
  } finally { await close(app.server); }
});

test("WEB_CSP_MODE: varsayılan enforce; report-only başlık adını değiştirir; geçersiz değer hata verir", async () => {
  assert.equal(cspMode(undefined), "enforce");
  assert.equal(cspMode("report-only"), "report-only");
  assert.equal(cspMode(" Enforce "), "enforce");
  assert.throws(() => cspMode("off"));
  assert.equal(cspHeaderName("enforce"), "Content-Security-Policy");
  const app = await harness(html, { mode: "report-only" });
  try {
    const response = await fetch(`${app.base}/`);
    assert.equal(response.headers.get("content-security-policy"), null);
    assert.match(response.headers.get("content-security-policy-report-only"), /^default-src 'self'; script-src /);
  } finally { await close(app.server); }
});

test("geliştirme politikası: HMR için eval/inline açık, nonce yok (nonce olsaydı unsafe-inline yok sayılırdı)", async () => {
  const policy = buildCsp({ dev: true });
  assert.match(policy, /script-src 'self' 'unsafe-inline' 'unsafe-eval'/);
  assert.ok(!policy.includes("nonce-"));
  const app = await harness(html, { dev: true });
  try {
    await (await fetch(`${app.base}/`)).text();
    assert.equal(app.seen[0].headers["content-security-policy"], undefined);
  } finally { await close(app.server); }
});

test("/healthz: 200 Healthy, no-store, bağımlılığa bakmaz ve Next'e gitmez", async () => {
  let fetched = 0;
  const app = await harness(html, { health: { apiBaseUrl: "http://127.0.0.1:1", fetchImpl: async () => { fetched += 1; throw new Error("çağrılmamalı"); } } });
  try {
    const response = await fetch(`${app.base}/healthz?x=1`);
    assert.equal(response.status, 200);
    assert.deepEqual(await response.json(), { status: "Healthy" });
    assert.equal(response.headers.get("cache-control"), "no-store");
    assert.equal(fetched, 0);
    assert.equal(app.seen.length, 0);
    const head = await fetch(`${app.base}/healthz`, { method: "HEAD" });
    assert.equal(head.status, 200);
    const post = await fetch(`${app.base}/healthz`, { method: "POST" });
    assert.equal(post.status, 405);
    assert.equal(post.headers.get("allow"), "GET, HEAD");
  } finally { await close(app.server); }
});

test("/readyz: API 200 -> 200; 503, ağ hatası ve zaman aşımı -> 503 Unhealthy, iç ayrıntı sızmaz", async () => {
  const requested = [];
  const api = await listen((request, response) => {
    requested.push(request.url);
    if (request.url === "/health/ready") { response.writeHead(200).end("Healthy"); return; }
    response.writeHead(404).end();
  });
  const sick = await listen((request, response) => { response.writeHead(503, { "Content-Type": "text/plain" }).end("Postgres sifre=SIR"); });
  const slow = await listen(() => { /* hiç yanıt vermez */ });
  const app = await harness(html, { health: { apiBaseUrl: api.base } });
  try {
    const ok = await fetch(`${app.base}/readyz`);
    assert.equal(ok.status, 200);
    assert.deepEqual(await ok.json(), { status: "Healthy" });
    assert.equal(ok.headers.get("cache-control"), "no-store");
    assert.deepEqual(requested, ["/health/ready"]);

    const cases = [
      ["API 503", { apiBaseUrl: sick.base }],
      ["ağ hatası", { apiBaseUrl: "http://127.0.0.1:1" }],
      ["zaman aşımı", { apiBaseUrl: slow.base, timeoutMs: 150 }],
    ];
    for (const [label, health] of cases) {
      const own = await harness(html, { health });
      try {
        const started = Date.now();
        const response = await fetch(`${own.base}/readyz`);
        const text = await response.text();
        assert.equal(response.status, 503, label);
        assert.deepEqual(JSON.parse(text), { status: "Unhealthy" }, label);
        assert.equal(response.headers.get("cache-control"), "no-store", label);
        for (const leak of ["127.0.0.1", "SIR", "Postgres", "ECONNREFUSED", "timeout", "abort"]) assert.ok(!text.toLowerCase().includes(leak.toLowerCase()), `${label}: ${leak}`);
        assert.ok(Date.now() - started < 3000, label);
      } finally { await close(own.server); }
    }
  } finally {
    await Promise.all([close(app.server), close(api.server), close(sick.server), close(slow.server)]);
  }
});

test("/readyz varsayılan zaman aşımı 2,5 sn'dir", async () => {
  const { apiIsReady } = await import("../../server/health.mjs");
  let signal;
  await apiIsReady("http://x", { fetchImpl: async (url, init) => { signal = init.signal; return { status: 200, body: null }; } });
  assert.ok(signal instanceof AbortSignal);
  const slow = await listen(() => {});
  const started = Date.now();
  try {
    assert.equal(await apiIsReady(slow.base), false);
    const elapsed = Date.now() - started;
    assert.ok(elapsed >= 2400 && elapsed < 3500, `süre ${elapsed}`);
  } finally { await close(slow.server); }
});

test("sağlık yolları mevcut Next rotalarıyla çakışmaz", () => {
  const app = path.join(import.meta.dirname, "../../src/app");
  const names = [];
  (function walk(dir) {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.isDirectory()) { names.push(entry.name.replace(/^\((.*)\)$/, "$1").toLowerCase()); walk(path.join(dir, entry.name)); }
    }
  })(app);
  for (const reserved of ["healthz", "readyz", "[...slug]", "[...path]", "[[...slug]]"]) assert.ok(!names.includes(reserved), reserved);
});

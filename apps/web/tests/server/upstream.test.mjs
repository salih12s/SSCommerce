import assert from "node:assert/strict";
import { once } from "node:events";
import { createServer } from "node:http";
import test from "node:test";
import { createRequestHandler } from "../../server/handler.mjs";
import { SERVICE_UNAVAILABLE_PROBLEM, isBackendProxyPath } from "../../server/upstream.mjs";
import { DEFAULT_PROXY_TIMEOUT_MS, resolveProxyTimeout } from "../../next.config.ts";

async function harness(handle) {
  const logs = [];
  const server = createServer(createRequestHandler({ handle, log: (entry) => logs.push(entry) })).listen(0, "127.0.0.1");
  await once(server, "listening");
  return { server, logs, base: `http://127.0.0.1:${server.address().port}` };
}
async function close(server) {
  server.closeAllConnections();
  await new Promise((resolve) => server.close(resolve));
}
// Next'in proxy hata davranışının birebir taklidi (proxy-request.js onProxyError): başlıksız 500 + sabit metin.
const nextProxyError = (request, response) => { response.statusCode = 500; response.end("Internal Server Error"); };

test("isBackendProxyPath: yalnız /api/backend ve altı", () => {
  for (const url of ["/api/backend", "/api/backend/", "/api/backend/api/v1/products?x=1", "/api/backend?x=1"]) assert.equal(isBackendProxyPath(url), true, url);
  for (const url of ["/api/backendx", "/api", "/urun/api/backend", "/", undefined]) assert.equal(isBackendProxyPath(url), false, String(url));
});

test("Next'in proxy hatası (/api/backend): 503 problem+json, Retry-After, no-store, ayrıntı yok, günlük 503", async () => {
  const app = await harness(nextProxyError);
  try {
    const response = await fetch(`${app.base}/api/backend/api/v1/products?gizli=1`);
    assert.equal(response.status, 503);
    assert.match(response.headers.get("content-type"), /^application\/problem\+json/);
    assert.equal(response.headers.get("retry-after"), "5");
    assert.equal(response.headers.get("cache-control"), "no-store");
    assert.equal(response.headers.get("x-content-type-options"), "nosniff");
    const text = await response.text();
    assert.deepEqual(JSON.parse(text), SERVICE_UNAVAILABLE_PROBLEM);
    assert.equal(Number(response.headers.get("content-length")), Buffer.byteLength(text));
    for (const leak of ["127.0.0.1", "ECONN", "gizli", "products", "Internal Server Error"]) assert.ok(!text.includes(leak), leak);
    await new Promise((resolve) => setTimeout(resolve, 30));
    assert.equal(app.logs[0].status, 503);
  } finally { await close(app.server); }
});

test("API'nin kendi yanıtları aynen geçer: 500 problem+json, 500 text/plain (başlıklı), 200, 404", async () => {
  const problem = JSON.stringify({ type: "about:blank", title: "API hatası", status: 500, code: "internal_error", traceId: "t-1" });
  const app = await harness((request, response) => {
    if (request.url.endsWith("/problem")) { response.writeHead(500, { "Content-Type": "application/problem+json" }); response.end(problem); return; }
    if (request.url.endsWith("/plain")) { response.writeHead(500, { "Content-Type": "text/plain" }); response.end("Internal Server Error"); return; }
    if (request.url.endsWith("/yok")) { response.writeHead(404, { "Content-Type": "application/problem+json" }); response.end('{"status":404}'); return; }
    response.writeHead(200, { "Content-Type": "application/json" }); response.end('{"ok":true}');
  });
  try {
    const p = await fetch(`${app.base}/api/backend/problem`);
    assert.equal(p.status, 500);
    assert.match(p.headers.get("content-type"), /^application\/problem\+json/);
    assert.equal(p.headers.get("retry-after"), null);
    assert.equal(await p.text(), problem);
    const t = await fetch(`${app.base}/api/backend/plain`);
    assert.equal(t.status, 500);
    assert.equal(await t.text(), "Internal Server Error");
    const n = await fetch(`${app.base}/api/backend/yok`);
    assert.equal(n.status, 404);
    await n.text();
    const ok = await fetch(`${app.base}/api/backend/x`);
    assert.equal(ok.status, 200);
    assert.deepEqual(await ok.json(), { ok: true });
  } finally { await close(app.server); }
});

test("API'nin başlıklı ama gövdesiz 500 yanıtı (Date var, Content-Type yok) değiştirilmez", async () => {
  const app = await harness((request, response) => { response.writeHead(500, { Date: new Date().toUTCString(), "Content-Length": "0" }); response.end(); });
  try {
    const response = await fetch(`${app.base}/api/backend/bos`);
    assert.equal(response.status, 500);
    assert.equal(await response.text(), "");
  } finally { await close(app.server); }
});

test("/api/backend dışındaki yollarda aynı imza 503'e çevrilmez", async () => {
  const app = await harness(nextProxyError);
  try {
    const response = await fetch(`${app.base}/baska`);
    assert.equal(response.status, 500);
    assert.equal(await response.text(), "Internal Server Error");
  } finally { await close(app.server); }
});

test("proxy zaman aşımı: varsayılan 10 sn, WEB_PROXY_TIMEOUT_MS geçerliyse uygulanır, geçersizse varsayılan", () => {
  assert.equal(DEFAULT_PROXY_TIMEOUT_MS, 10_000);
  assert.equal(resolveProxyTimeout({}), 10_000);
  assert.equal(resolveProxyTimeout({ WEB_PROXY_TIMEOUT_MS: "1500" }), 1500);
  for (const bad of ["0", "-5", "abc", "1.5", "999999999", ""]) assert.equal(resolveProxyTimeout({ WEB_PROXY_TIMEOUT_MS: bad }), 10_000, bad);
});

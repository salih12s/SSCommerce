import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { existsSync, readFileSync } from "node:fs";
import { createServer } from "node:http";
import path from "node:path";
import test from "node:test";

// Süreç düzeyinde kanıt: gerçek `node server.mjs` + gerçek Next üretim derlemesi + sahte upstream.
// Derleme yoksa (.next/BUILD_ID) ya da rewrite hedefi portu doluysa (ör. E2E API çalışıyor) atlanır.
const root = path.join(import.meta.dirname, "../..");
const manifest = path.join(root, ".next", "routes-manifest.json");
const built = existsSync(path.join(root, ".next", "BUILD_ID")) && existsSync(manifest);

function bakedUpstream() {
  const rewrites = JSON.parse(readFileSync(manifest, "utf8")).rewrites;
  const all = Array.isArray(rewrites) ? rewrites : [...(rewrites.beforeFiles ?? []), ...(rewrites.afterFiles ?? []), ...(rewrites.fallback ?? [])];
  const rule = all.find((item) => item.source === "/api/backend/:path*");
  return new URL(rule.destination.replace(":path*", ""));
}
async function freePort() {
  const probe = createServer().listen(0, "127.0.0.1");
  await once(probe, "listening");
  const { port } = probe.address();
  await new Promise((resolve) => probe.close(resolve));
  return port;
}

test("server.mjs: korelasyon kimliği rewrite ile API'ye taşınır, başlıklar, günlük, sağlık uçları, zarif kapanış", { skip: !built && "üretim derlemesi yok", timeout: 90_000 }, async (t) => {
  const target = bakedUpstream();
  const seen = [];
  const upstream = createServer((request, response) => {
    if (request.url === "/health/ready") { response.writeHead(200).end("Healthy"); return; }
    if (request.url?.startsWith("/slow")) { setTimeout(() => response.end("yavas-bitti"), 1500); return; }
    seen.push({ url: request.url, requestId: request.headers["x-request-id"] });
    response.writeHead(200, { "Content-Type": "application/json" }).end("{}");
  });
  try {
    upstream.listen(Number(target.port), target.hostname);
    await once(upstream, "listening");
  } catch (error) {
    if (error.code === "EADDRINUSE") return t.skip(`rewrite hedefi ${target.host} dolu`);
    throw error;
  }
  const port = await freePort();
  const child = spawn(process.execPath, ["server.mjs", "-p", String(port)], { cwd: root, env: { ...process.env, API_BASE_URL: target.origin, WEB_HOST: "127.0.0.1" }, stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
  let stdout = "";
  child.stdout.setEncoding("utf8").on("data", (chunk) => { stdout += chunk; });
  const exited = once(child, "exit");
  const base = `http://127.0.0.1:${port}`;
  try {
    for (let i = 0; i < 120; i += 1) {
      if (stdout.includes("SSCommerce web")) break;
      await new Promise((resolve) => setTimeout(resolve, 250));
    }
    assert.ok(stdout.includes("SSCommerce web"), "sunucu açılmalı");

    // F2: istek kimliği API'ye ulaşır
    const proxied = await fetch(`${base}/api/backend/api/v1/probe?gizli=1`, { headers: { "X-Request-Id": "trace-abcdef12" } });
    assert.equal(proxied.status, 200);
    assert.equal(proxied.headers.get("x-request-id"), "trace-abcdef12");
    assert.deepEqual(seen.at(-1), { url: "/api/v1/probe?gizli=1", requestId: "trace-abcdef12" });
    await fetch(`${base}/api/backend/api/v1/probe`, { headers: { "X-Request-Id": "kotu id!" } });
    assert.match(seen.at(-1).requestId, /^[0-9a-f]{32}$/);

    // F3/F4: Next sayfası (404) başlıkları + CSP, statik dosya
    const page = await fetch(`${base}/bu-sayfa-yok`);
    assert.equal(page.status, 404);
    await page.text();
    assert.equal(page.headers.get("x-frame-options"), "DENY");
    assert.match(page.headers.get("content-security-policy"), /script-src 'self' 'nonce-[^']+' 'sha256-/);

    // F1
    assert.deepEqual(await (await fetch(`${base}/healthz`)).json(), { status: "Healthy" });
    assert.deepEqual(await (await fetch(`${base}/readyz`)).json(), { status: "Healthy" });

    // F2: günlük — sorgu dizesi yok
    await new Promise((resolve) => setTimeout(resolve, 200));
    const entries = stdout.split("\n").filter((line) => line.startsWith("{")).map((line) => JSON.parse(line));
    const probe = entries.find((entry) => entry.requestId === "trace-abcdef12");
    assert.equal(probe.path, "/api/backend/api/v1/probe");
    assert.equal(probe.msg, "http_request");
    assert.ok(!stdout.includes("gizli=1"));

    // F5: zarif kapanış (Windows'ta SIGTERM süreci doğrudan öldürür; yalnız POSIX'te sınanır)
    if (process.platform === "win32") return t.diagnostic("SIGTERM zarif kapanışı Windows'ta sınanamaz; gracefulClose birim testleri kapsar");
    const slow = fetch(`${base}/api/backend/slow`).then((response) => response.text());
    await new Promise((resolve) => setTimeout(resolve, 300));
    child.kill("SIGTERM");
    assert.equal(await slow, "yavas-bitti", "sürmekte olan istek kapanışta tamamlanmalı");
    const [code] = await exited;
    assert.equal(code, 0);
    const last = stdout.trim().split("\n").filter((line) => line.startsWith("{")).map((line) => JSON.parse(line));
    assert.ok(last.some((entry) => entry.msg === "shutdown_started"));
    assert.ok(last.some((entry) => entry.msg === "shutdown_complete" && entry.forced === false));
  } finally {
    if (child.exitCode === null) child.kill();
    upstream.closeAllConnections();
    await new Promise((resolve) => upstream.close(resolve));
  }
});

// --- /api/backend upstream hatası: aynı dosyada (aynı rewrite hedef portu; dosyalar paralel çalışırsa çakışırdı) ---
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
/** Hedef portu kısa süre tutup bırakır (boşsa true). Doluysa (ör. E2E API çalışıyor) 500 ms arayla en çok `tries` kez dener. */
async function targetIsFree(target, tries = 40) {
  for (let i = 0; i < tries; i += 1) {
    const probe = createServer();
    try {
      probe.listen(Number(target.port), target.hostname);
      await once(probe, "listening");
      await new Promise((resolve) => probe.close(resolve));
      return true;
    } catch (error) {
      if (error.code !== "EADDRINUSE") throw error;
      await sleep(500);
    }
  }
  return false;
}

test("server.mjs: /api/backend upstream kapalı/askıda -> 503 problem+json; API'nin kendi yanıtları aynen geçer", { skip: !built && "üretim derlemesi yok", timeout: 120_000 }, async (t) => {
  const target = bakedUpstream();
  if (!(await targetIsFree(target))) return t.skip(`rewrite hedefi ${target.host} dolu`);
  const web = await freePort();
  const child = spawn(process.execPath, ["server.mjs", "-p", String(web)], { cwd: root, env: { ...process.env, API_BASE_URL: target.origin, WEB_HOST: "127.0.0.1", WEB_PROXY_TIMEOUT_MS: "1500" }, stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
  let stdout = "";
  child.stdout.setEncoding("utf8").on("data", (chunk) => { stdout += chunk; });
  child.stderr.resume();
  const base = `http://127.0.0.1:${web}`;
  const problemBody = JSON.stringify({ type: "about:blank", title: "API hatası", status: 500, code: "internal_error" });
  const upstream = createServer((request, response) => {
    if (request.url === "/hang") return; // yanıt vermez
    if (request.url === "/api/v1/boom") { response.writeHead(500, { "Content-Type": "application/problem+json" }).end(problemBody); return; }
    if (request.url === "/api/v1/missing") { response.writeHead(404, { "Content-Type": "application/problem+json" }).end('{"status":404}'); return; }
    response.writeHead(200, { "Content-Type": "application/json" }).end('{"ok":true}');
  });
  try {
    for (let i = 0; i < 120; i += 1) {
      if (stdout.includes("SSCommerce web")) break;
      await sleep(250);
    }
    assert.ok(stdout.includes("SSCommerce web"), "sunucu açılmalı");

    // (a) hedef KAPALI: bağlantı reddi -> 503 problem+json, ayrıntı yok
    const startedAt = Date.now();
    const refused = await fetch(`${base}/api/backend/api/v1/products?gizli=1`);
    assert.equal(refused.status, 503);
    assert.match(refused.headers.get("content-type"), /^application\/problem\+json/);
    assert.equal(refused.headers.get("retry-after"), "5");
    assert.equal(refused.headers.get("cache-control"), "no-store");
    const text = await refused.text();
    const problem = JSON.parse(text);
    assert.equal(problem.status, 503);
    assert.equal(problem.code, "service_unavailable");
    assert.deepEqual(Object.keys(problem).sort(), ["code", "status", "title", "type"]);
    for (const leak of ["127.0.0.1", String(target.port), "ECONNREFUSED", "products", "gizli", "Internal Server Error"]) assert.ok(!text.includes(leak), leak);
    assert.ok(Date.now() - startedAt < 5000, "bağlantı reddi hızlı dönmeli");

    upstream.listen(Number(target.port), target.hostname);
    await once(upstream, "listening");

    // (b) API'nin gerçek 500 problem+json yanıtı AYNEN geçer
    const boom = await fetch(`${base}/api/backend/api/v1/boom`);
    assert.equal(boom.status, 500);
    assert.match(boom.headers.get("content-type"), /^application\/problem\+json/);
    assert.equal(boom.headers.get("retry-after"), null);
    assert.equal(await boom.text(), problemBody);

    // (c) 200 ve 404 aynen geçer
    const ok = await fetch(`${base}/api/backend/api/v1/ok`);
    assert.equal(ok.status, 200);
    assert.deepEqual(await ok.json(), { ok: true });
    const missing = await fetch(`${base}/api/backend/api/v1/missing`);
    assert.equal(missing.status, 404);
    assert.deepEqual(await missing.json(), { status: 404 });

    // (d) askıda kalan upstream -> ayarlı süre (1.5 sn) sonunda 503
    const hangStart = Date.now();
    const hung = await fetch(`${base}/api/backend/hang`);
    const elapsed = Date.now() - hangStart;
    assert.equal(hung.status, 503);
    assert.equal(hung.headers.get("retry-after"), "5");
    assert.equal((await hung.json()).code, "service_unavailable");
    assert.ok(elapsed >= 1200 && elapsed < 6000, `zaman aşımı ayarlı sürede olmalı (${elapsed} ms)`);

    // Erişim günlüğünde durum 503 görünür; sorgu dizesi sızmaz
    await sleep(200);
    const entries = stdout.split("\n").filter((line) => line.startsWith("{")).map((line) => JSON.parse(line)).filter((entry) => entry.msg === "http_request");
    assert.ok(entries.some((entry) => entry.path === "/api/backend/api/v1/products" && entry.status === 503 && entry.level === "error"));
    assert.ok(entries.some((entry) => entry.path === "/api/backend/hang" && entry.status === 503));
    assert.ok(entries.some((entry) => entry.path === "/api/backend/api/v1/boom" && entry.status === 500));
    assert.ok(!stdout.includes("gizli=1"));
  } finally {
    if (child.exitCode === null) child.kill();
    upstream.closeAllConnections();
    await new Promise((resolve) => upstream.close(resolve));
  }
});

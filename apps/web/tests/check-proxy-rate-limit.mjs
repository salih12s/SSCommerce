import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import startApi from "./e2e/start-api.mjs";

// Separate disposable host: do not exhaust the browser suite's limiter or touch dev data.
process.env.E2E_CREDENTIALS_LIMIT = "3";
const cleanup = await startApi();
let web;
try {
  web = spawn(process.execPath, ["server.mjs", "-p", "3200"], {
    env: { ...process.env, API_BASE_URL: "http://127.0.0.1:5081" },
    stdio: ["ignore", "ignore", "pipe"], windowsHide: true,
  });
  web.stderr.resume();
  const origin = "http://127.0.0.1:3200";
  let ready = false;
  for (let i = 0; i < 100 && web.exitCode === null; i++) {
    try { ready = (await fetch(`${origin}/giris`)).ok; } catch { /* Startup */ }
    if (ready) break;
    await new Promise((resolve) => setTimeout(resolve, 200));
  }
  assert.ok(ready, "Next web server must start");
  const bootstrap = await fetch(`${origin}/api/backend/api/v1/auth/csrf`);
  assert.equal(bootstrap.status, 200);
  const token = (await bootstrap.json()).token;
  const cookie = bootstrap.headers.getSetCookie().map((v) => v.split(";")[0]).join("; ");
  const statuses = [];
  for (let i = 1; i <= 4; i++) {
    const response = await fetch(`${origin}/api/backend/api/v1/auth/login`, {
      method: "POST", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token, Cookie: cookie, Origin: origin, "X-Forwarded-For": `198.51.100.${i}` },
      body: JSON.stringify({}),
    });
    statuses.push(response.status);
    if (i === 4) {
      assert.ok(Number(response.headers.get("retry-after")) > 0);
      assert.equal((await response.json()).code, "rate_limited");
    }
  }
  assert.deepEqual(statuses, [400, 400, 400, 429]);
  console.log("PASS: rotated untrusted X-Forwarded-For cannot evade real Next -> API rate limit (400/400/400/429).");
} finally {
  if (web && web.exitCode === null) { const exited = once(web, "exit"); web.kill(); await exited; }
  await cleanup();
}

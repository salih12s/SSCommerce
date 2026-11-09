import assert from "node:assert/strict";
import test from "node:test";
import constants from "next/dist/shared/lib/constants.js";
const { PHASE_DEVELOPMENT_SERVER, PHASE_PRODUCTION_BUILD, PHASE_PRODUCTION_SERVER } = constants;
import config, { resolveApiBaseUrl } from "../../next.config.ts";

// Üretim derlemesi API_BASE_URL olmadan sessizce yerel adrese düşmez; dev/çalışma zamanı etkilenmez.
test("next build: API_BASE_URL yoksa (ya da boşsa) derleme hata verir", () => {
  assert.throws(() => resolveApiBaseUrl(PHASE_PRODUCTION_BUILD, {}), /API_BASE_URL is required/);
  assert.throws(() => resolveApiBaseUrl(PHASE_PRODUCTION_BUILD, { API_BASE_URL: "  " }), /API_BASE_URL is required/);
  const previous = process.env.API_BASE_URL;
  delete process.env.API_BASE_URL;
  try {
    assert.throws(() => config(PHASE_PRODUCTION_BUILD), /API_BASE_URL is required/);
  } finally {
    if (previous !== undefined) process.env.API_BASE_URL = previous;
  }
});

test("next build: değişken verilmişse geçer ve rewrite hedefi o değerdir", async () => {
  assert.equal(resolveApiBaseUrl(PHASE_PRODUCTION_BUILD, { API_BASE_URL: "http://api.internal:5080" }), "http://api.internal:5080");
  const previous = process.env.API_BASE_URL;
  process.env.API_BASE_URL = "http://127.0.0.1:5081";
  try {
    const rules = await config(PHASE_PRODUCTION_BUILD).rewrites();
    assert.deepEqual(rules, [{ source: "/api/backend/:path*", destination: "http://127.0.0.1:5081/:path*" }]);
  } finally {
    if (previous === undefined) delete process.env.API_BASE_URL; else process.env.API_BASE_URL = previous;
  }
});

test("dev ve üretim sunucusu fazlarında yerel varsayılan korunur", () => {
  assert.equal(resolveApiBaseUrl(PHASE_DEVELOPMENT_SERVER, {}), "http://localhost:5080");
  assert.equal(resolveApiBaseUrl(PHASE_PRODUCTION_SERVER, {}), "http://localhost:5080");
});

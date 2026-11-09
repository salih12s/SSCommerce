import assert from "node:assert/strict";
import { createServer } from "node:http";
import { once } from "node:events";
import { test } from "node:test";
import { sanitizeForwardedHeaders, trustedProxyAddresses } from "../../server/forwarding.mjs";

test("HTTP ingress ignores rotating spoofed IPs from an untrusted socket", async () => {
  const server = createServer((req, res) => {
    sanitizeForwardedHeaders(req);
    res.setHeader("Content-Type", "application/json");
    res.end(JSON.stringify(req.headers));
  }).listen(0, "127.0.0.1");
  await once(server, "listening");
  try {
    for (const spoof of ["198.51.100.10", "198.51.100.20, 203.0.113.9"]) {
      const response = await fetch(`http://127.0.0.1:${server.address().port}`, {
        headers: { "X-Forwarded-For": spoof, "X-Forwarded-Proto": "https", "Forwarded": "for=evil", "X-Original-For": spoof },
      });
      const headers = await response.json();
      assert.equal(headers["x-forwarded-for"], "127.0.0.1");
      assert.equal(headers["x-forwarded-proto"], "http");
      assert.equal(headers.forwarded, undefined);
      assert.equal(headers["x-original-for"], undefined);
    }
  } finally {
    server.closeAllConnections();
    await new Promise((resolve) => server.close(resolve));
  }
});

test("trusted chain stops at nearest untrusted hop, preserving distinct visitors", () => {
  for (const client of ["203.0.113.10", "203.0.113.20"]) {
    const req = { socket: { remoteAddress: "::ffff:127.0.0.1" }, headers: { host: "shop.test", "x-forwarded-for": `192.0.2.99, ${client}, 10.0.0.2`, "x-forwarded-proto": "https" } };
    sanitizeForwardedHeaders(req, trustedProxyAddresses("127.0.0.1,10.0.0.2"));
    assert.equal(req.headers["x-forwarded-for"], client);
    assert.equal(req.headers["x-forwarded-proto"], "https");
    assert.equal(req.headers["x-forwarded-host"], "shop.test");
  }
});

test("malformed chains fail closed to socket peer and invalid proxy config fails startup", () => {
  for (const chain of ["unknown", "203.0.113.1, invalid", "1".repeat(2049)]) {
    const req = { socket: { remoteAddress: "127.0.0.1" }, headers: { "x-forwarded-for": chain } };
    sanitizeForwardedHeaders(req, trustedProxyAddresses("127.0.0.1"));
    assert.equal(req.headers["x-forwarded-for"], "127.0.0.1");
  }
  assert.throws(() => trustedProxyAddresses("0.0.0.0/0"));
});

import assert from "node:assert/strict";
import { once } from "node:events";
import { createServer } from "node:http";
import test from "node:test";
import { gracefulClose } from "../../server/shutdown.mjs";

async function slowServer(holdMs) {
  const server = createServer((request, response) => {
    setTimeout(() => response.end("bitti"), holdMs);
  }).listen(0, "127.0.0.1");
  await once(server, "listening");
  return { server, url: `http://127.0.0.1:${server.address().port}/` };
}

test("zarif kapanış: sürmekte olan istek tamamlanır, yeni bağlantı kabul edilmez", async () => {
  const { server, url } = await slowServer(400);
  const inflight = fetch(url).then((response) => response.text());
  await new Promise((resolve) => setTimeout(resolve, 100)); // istek sunucuya ulaşsın
  const started = Date.now();
  const closing = gracefulClose(server, { graceMs: 5000 });
  await new Promise((resolve) => setTimeout(resolve, 50));
  await assert.rejects(fetch(url, { signal: AbortSignal.timeout(1000) }), "kapanış başladıktan sonra yeni bağlantı reddedilmeli");
  assert.equal(await inflight, "bitti");
  const { forced } = await closing;
  assert.equal(forced, false);
  assert.ok(Date.now() - started >= 250, "istek bitene kadar beklenmeli");
  assert.equal(server.listening, false);
});

test("zarif kapanış: süre dolunca takılan istek koparılır ve söz yine çözülür", async () => {
  const { server, url } = await slowServer(60_000);
  const inflight = fetch(url).then((response) => response.text(), (error) => error);
  await new Promise((resolve) => setTimeout(resolve, 100));
  const started = Date.now();
  const { forced } = await gracefulClose(server, { graceMs: 300 });
  assert.equal(forced, true);
  assert.ok(Date.now() - started < 2000);
  assert.ok((await inflight) instanceof Error, "bağlantı koparıldığı için istemci hata görmeli");
});

test("zarif kapanış: boştaki keep-alive bağlantısı kapanışı geciktirmez", async () => {
  const { server, url } = await slowServer(0);
  const { Agent, request } = await import("node:http");
  const agent = new Agent({ keepAlive: true });
  await new Promise((resolve, reject) => {
    const req = request(url, { agent }, (response) => { response.resume(); response.on("end", resolve); });
    req.on("error", reject).end();
  });
  const started = Date.now();
  const { forced } = await gracefulClose(server, { graceMs: 5000 });
  agent.destroy();
  assert.equal(forced, false);
  assert.ok(Date.now() - started < 1000, "boşta bağlantı için 10 sn beklenmemeli");
});

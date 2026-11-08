import { createServer } from "node:http";
import { trustedProxyAddresses } from "./server/forwarding.mjs";
import { createRequestHandler } from "./server/handler.mjs";
import path from "node:path";
import { collectInlineScriptHashes } from "./server/csp.mjs";
import { cspMode } from "./server/security.mjs";
import { gracefulClose } from "./server/shutdown.mjs";

const dev = process.argv.includes("--dev");
process.env.NODE_ENV = dev ? "development" : "production";
const { default: next } = await import("next");
const portFlag = process.argv.findIndex((arg) => arg === "-p" || arg === "--port");
const port = Number(portFlag >= 0 ? process.argv[portFlag + 1] : process.env.PORT ?? 3000);
if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error("Invalid web port.");
const hostname = process.env.WEB_HOST ?? "127.0.0.1";
const trusted = trustedProxyAddresses(process.env.WEB_TRUSTED_PROXIES);
const mode = cspMode();
const scriptHashes = dev ? [] : collectInlineScriptHashes(path.join(import.meta.dirname, ".next", "server"));
const SHUTDOWN_GRACE_MS = 10_000;
const app = next({ dev, hostname, port });
await app.prepare();
const handle = app.getRequestHandler();
let stopping = false;
const server = createServer(createRequestHandler({ handle, trusted, mode, dev, scriptHashes, isDraining: () => stopping }));
const logLine = (entry) => process.stdout.write(`${JSON.stringify({ ts: new Date().toISOString(), ...entry })}\n`);
server.listen(port, hostname, () => console.log(`SSCommerce web http://${hostname}:${port}`));
server.on("error", (error) => { console.error(error.message); process.exitCode = 1; });
async function stop(signal) {
  if (stopping) return;
  stopping = true;
  logLine({ level: "info", msg: "shutdown_started", signal, graceMs: SHUTDOWN_GRACE_MS });
  // Yeni bağlantı kabulü durur; sürmekte olan istekler en çok 10 sn beklenir, sonra kalanlar koparılır.
  const { forced } = await gracefulClose(server, { graceMs: SHUTDOWN_GRACE_MS });
  await app.close().catch(() => {});
  logLine({ level: forced ? "warn" : "info", msg: "shutdown_complete", forced });
  process.exitCode ||= 0;
  setTimeout(() => process.exit(), 2000).unref();
}
process.on("SIGTERM", () => void stop("SIGTERM"));
process.on("SIGINT", () => void stop("SIGINT"));

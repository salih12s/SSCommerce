import { createServer } from "node:http";
import { sanitizeForwardedHeaders, trustedProxyAddresses } from "./server/forwarding.mjs";
import { isPrivateRoute } from "./server/privacy.mjs";

const dev = process.argv.includes("--dev");
process.env.NODE_ENV = dev ? "development" : "production";
const { default: next } = await import("next");
const portFlag = process.argv.findIndex((arg) => arg === "-p" || arg === "--port");
const port = Number(portFlag >= 0 ? process.argv[portFlag + 1] : process.env.PORT ?? 3000);
if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error("Invalid web port.");
const hostname = process.env.WEB_HOST ?? "127.0.0.1";
const trusted = trustedProxyAddresses(process.env.WEB_TRUSTED_PROXIES);
const app = next({ dev, hostname, port });
await app.prepare();
const handle = app.getRequestHandler();
const server = createServer(async (request, response) => {
  try {
    sanitizeForwardedHeaders(request, trusted);
    if (isPrivateRoute(request.url)) {
      response.setHeader("Cache-Control", "private, no-store");
    }
    await handle(request, response);
  } catch {
    if (!response.headersSent) response.writeHead(500, { "Content-Type": "text/plain; charset=utf-8" });
    response.end("İstek tamamlanamadı.");
  }
});
server.listen(port, hostname, () => console.log(`SSCommerce web http://${hostname}:${port}`));
server.on("error", (error) => { console.error(error.message); process.exitCode = 1; });
let stopping = false;
async function stop() {
  if (stopping) return;
  stopping = true;
  server.close();
  server.closeAllConnections();
  await app.close();
}
process.on("SIGTERM", stop);
process.on("SIGINT", stop);

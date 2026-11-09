import { sanitizeForwardedHeaders } from "./forwarding.mjs";
import { handleHealth } from "./health.mjs";
import { accessLogEntry, resolveRequestId, writeLog } from "./observability.mjs";
import { isPrivateRoute } from "./privacy.mjs";
import { buildCsp, cspHeaderName, installResponseHeaders, newNonce } from "./security.mjs";
import { installUpstreamFailureGuard } from "./upstream.mjs";

/**
 * İstek işleyici. Sıra: korelasyon kimliği + erişim günlüğü -> güvenlik başlıkları -> sağlık uçları -> Next.
 * `handle` Next'in istek işleyicisidir (testte sahtesi verilir).
 */
export function createRequestHandler({ handle, trusted = new Set(), mode = "enforce", dev = false, scriptHashes = [], isDraining = () => false, log = writeLog, health = {}, now = Date.now }) {
  return async function onRequest(request, response) {
    const startedAt = now();
    const requestId = resolveRequestId(request.headers["x-request-id"]);
    request.headers["x-request-id"] = requestId;
    response.setHeader("X-Request-Id", requestId);
    if (isDraining()) response.setHeader("Connection", "close");
    let logged = false;
    response.once("close", () => {
      if (logged) return;
      logged = true;
      try { log(accessLogEntry({ request, response, requestId, startedAt, now: now() })); } catch { /* günlük yazımı isteği bozmaz */ }
    });

    const nonce = newNonce();
    const policy = buildCsp({ nonce, scriptHashes, dev });
    // Next, nonce'u istekteki CSP başlığından okuyup akıtılan betiklere ekler.
    if (!dev) request.headers["content-security-policy"] = policy;
    installResponseHeaders(response, { csp: { name: cspHeaderName(mode), value: policy } });

    try {
      if (await handleHealth(request, response, health)) return;
      sanitizeForwardedHeaders(request, trusted);
      installUpstreamFailureGuard(request, response);
      if (isPrivateRoute(request.url)) response.setHeader("Cache-Control", "private, no-store");
      await handle(request, response);
    } catch {
      if (!response.headersSent) response.writeHead(500, { "Content-Type": "text/plain; charset=utf-8" });
      response.end("İstek tamamlanamadı.");
    }
  };
}

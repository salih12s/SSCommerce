// Web sağlık uçları. server.mjs bunları Next'ten ÖNCE ele alır; API'ye yönlendirilmezler.
const HEALTH_PATHS = new Set(["/healthz", "/readyz"]);
const READY_TIMEOUT_MS = 2500;

export function healthPath(rawUrl) {
  if (typeof rawUrl !== "string") return null;
  const end = rawUrl.search(/[?#]/);
  const path = end === -1 ? rawUrl : rawUrl.slice(0, end);
  return HEALTH_PATHS.has(path) ? path : null;
}

function send(request, response, status, body) {
  const payload = JSON.stringify(body);
  response.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Content-Length": Buffer.byteLength(payload),
    "Cache-Control": "no-store",
    ...(status === 405 ? { Allow: "GET, HEAD" } : {}),
  });
  response.end(request.method === "HEAD" ? undefined : payload);
}

/** API hazır mı? Yalnız 200 hazırdır. Hata iletisi, URL ya da ayrıntı dışarı verilmez. */
export async function apiIsReady(apiBaseUrl, { fetchImpl = fetch, timeoutMs = READY_TIMEOUT_MS } = {}) {
  try {
    const url = `${apiBaseUrl.replace(/\/+$/, "")}/health/ready`;
    const result = await fetchImpl(url, { redirect: "manual", cache: "no-store", signal: AbortSignal.timeout(timeoutMs) });
    await result.body?.cancel().catch(() => {});
    return result.status === 200;
  } catch {
    return false;
  }
}

/** true dönerse istek işlenmiştir. */
export async function handleHealth(request, response, { apiBaseUrl = process.env.API_BASE_URL ?? "http://localhost:5080", ...options } = {}) {
  const path = healthPath(request.url);
  if (!path) return false;
  if (request.method !== "GET" && request.method !== "HEAD") {
    send(request, response, 405, { status: "MethodNotAllowed" });
    return true;
  }
  if (path === "/healthz") {
    send(request, response, 200, { status: "Healthy" });
    return true;
  }
  const ready = await apiIsReady(apiBaseUrl, options);
  send(request, response, ready ? 200 : 503, { status: ready ? "Healthy" : "Unhealthy" });
  return true;
}

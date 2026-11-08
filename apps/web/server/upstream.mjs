// /api/backend rewrite'ında upstream (API) kurulamayınca ya da zaman aşımına uğrayınca Next'in iç proxy'si
// (node_modules/next/dist/server/lib/router-utils/proxy-request.js, onProxyError) `res.statusCode = 500` +
// `res.end('Internal Server Error')` üretir. İstemci kesintide yalnız güvenli 503 problem+json görmelidir.
//
// Neden writeHead'i sarıyoruz, end'i değil: Next'in sıkıştırma ara katmanı (compression) `end`'i sarar ve `end` içinde
// önce başlıkları gönderir (_implicitHeader); gövde bizim `end`'imize geldiğinde başlıklar çoktan gitmiş olur.
// İmza (başlıklar gitmeden): yol /api/backend, durum 500 ve upstream'den hiçbir başlık kopyalanmamış
// (Content-Type, Content-Length, Date, Transfer-Encoding yok). API'nin kendi yanıtları (500 problem+json dahil)
// her zaman upstream başlıkları taşır (Kestrel en az Date yazar); bu yüzden değiştirilmez.

const PROXY_PREFIX = "/api/backend";
const UPSTREAM_HEADERS = ["content-type", "content-length", "date", "transfer-encoding"];

export const SERVICE_UNAVAILABLE_PROBLEM = Object.freeze({
  type: "https://tools.ietf.org/html/rfc9110#section-15.6.4",
  title: "Hizmet şu an kullanılamıyor. Lütfen biraz sonra tekrar deneyin",
  status: 503,
  code: "service_unavailable",
});
const BODY = JSON.stringify(SERVICE_UNAVAILABLE_PROBLEM);

export function isBackendProxyPath(rawUrl) {
  const text = typeof rawUrl === "string" ? rawUrl : "";
  return text === PROXY_PREFIX || text.startsWith(`${PROXY_PREFIX}/`) || text.startsWith(`${PROXY_PREFIX}?`);
}

/** Next'in proxy hatası imzası (başlıklar henüz gitmedi): 500 ve upstream başlığı yok. */
export function isNextProxyFailure(response, status, supplied = {}) {
  if (response.headersSent || Number(status) !== 500) return false;
  const given = Object.keys(supplied).map((key) => key.toLowerCase());
  return !UPSTREAM_HEADERS.some((name) => response.hasHeader(name) || given.includes(name));
}

/** `writeHead` ve `end`'i yalnız /api/backend istekleri için sarar. */
export function installUpstreamFailureGuard(request, response) {
  if (!isBackendProxyPath(request.url)) return;
  const originalWriteHead = response.writeHead;
  const originalEnd = response.end;
  let replaced = false;
  response.writeHead = function writeHead(status, ...rest) {
    const supplied = rest.find((value) => value && typeof value === "object" && !Array.isArray(value)) ?? {};
    if (!replaced && isNextProxyFailure(this, status, supplied)) {
      replaced = true;
      this.statusCode = 503;
      this.statusMessage = "Service Unavailable";
      this.setHeader("Content-Type", "application/problem+json; charset=utf-8");
      this.setHeader("Content-Length", Buffer.byteLength(BODY));
      this.setHeader("Retry-After", "5");
      this.setHeader("Cache-Control", "no-store");
      return originalWriteHead.call(this, 503);
    }
    return originalWriteHead.call(this, status, ...rest);
  };
  response.end = function end(chunk, ...rest) {
    // Başlıklar henüz gitmediyse imzayı burada da yakala (sıkıştırma ara katmanı yoksa Node başlığı `end` içinde yazar).
    if (!replaced && !this.headersSent && isNextProxyFailure(this, this.statusCode)) this.writeHead(this.statusCode);
    // Başlıklar 503 olarak gitti; Next'in sabit metni yerine problem gövdesi yazılır.
    if (replaced) return originalEnd.call(this, BODY);
    return originalEnd.call(this, chunk, ...rest);
  };
}

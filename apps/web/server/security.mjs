import { randomBytes } from "node:crypto";

// Tüm yanıtlara eklenir. HSTS bilerek yok: TLS kenarında (Caddy) eklenir.
export const SECURITY_HEADERS = Object.freeze({
  "X-Content-Type-Options": "nosniff",
  "Referrer-Policy": "strict-origin-when-cross-origin",
  "X-Frame-Options": "DENY",
  "Permissions-Policy": "camera=(), microphone=(), geolocation=(), payment=()",
  "Cross-Origin-Opener-Policy": "same-origin",
});

export function cspMode(value = process.env.WEB_CSP_MODE) {
  const mode = (value ?? "enforce").trim().toLowerCase();
  if (mode === "enforce" || mode === "report-only") return mode;
  throw new Error("WEB_CSP_MODE must be 'enforce' or 'report-only'.");
}

export const newNonce = () => randomBytes(16).toString("base64");

/**
 * HTML yanıtları için içerik güvenliği politikası.
 * Üretim: script-src nonce + derleme kabuğu özetleri (bkz. csp.mjs); 'unsafe-inline' YOK, 'strict-dynamic' YOK
 * (statik kabuktaki nonce'suz <script src> etiketleri 'self' ile yüklenir; strict-dynamic 'self'i yok sayardı).
 * style-src nonce'lu; ancak SSR'lanan style="…" öznitelikleri nonce/hash ile açılamayacağından yalnız
 * style-src-attr 'unsafe-inline' verilir (öznitelikler betik çalıştıramaz; <style> blokları nonce ister).
 * Geliştirme (--dev): HMR için nonce'suz 'unsafe-inline' + 'unsafe-eval'.
 */
export function buildCsp({ nonce, scriptHashes = [], dev = false } = {}) {
  const script = dev
    ? ["'self'", "'unsafe-inline'", "'unsafe-eval'"]
    : ["'self'", `'nonce-${nonce}'`, ...scriptHashes];
  const style = dev ? ["'self'", "'unsafe-inline'"] : ["'self'", `'nonce-${nonce}'`];
  return [
    "default-src 'self'",
    `script-src ${script.join(" ")}`,
    `style-src ${style.join(" ")}`,
    "style-src-attr 'unsafe-inline'",
    "img-src 'self' data:",
    "connect-src 'self'",
    "font-src 'self'",
    "frame-ancestors 'none'",
    "base-uri 'self'",
    "form-action 'self'",
    "object-src 'none'",
  ].join("; ");
}

export const cspHeaderName = (mode) => (mode === "report-only" ? "Content-Security-Policy-Report-Only" : "Content-Security-Policy");

/**
 * writeHead'i sarar: başlıklar yanıtın gönderildiği anda eklenir; böylece Next'in ya da API proxy'sinin ürettiği
 * yanıtlar da (statik, hata, rewrite) kapsanır. Yanıtta zaten bulunan başlığın üzerine yazılmaz
 * (ör. API'nin kendi Content-Security-Policy'si korunur).
 */
export function installResponseHeaders(response, { csp } = {}) {
  const original = response.writeHead;
  response.writeHead = function writeHead(status, ...rest) {
    if (!this.headersSent) {
      const supplied = rest.find((value) => value && typeof value === "object" && !Array.isArray(value)) ?? {};
      const has = (name) => this.hasHeader(name) || Object.keys(supplied).some((key) => key.toLowerCase() === name.toLowerCase());
      for (const [name, value] of Object.entries(SECURITY_HEADERS)) if (!has(name)) this.setHeader(name, value);
      if (csp && !has(csp.name) && !has("Content-Security-Policy")) {
        const type = String(this.getHeader("content-type") ?? supplied["Content-Type"] ?? supplied["content-type"] ?? "");
        if (/^text\/html\b/i.test(type)) this.setHeader(csp.name, csp.value);
      }
    }
    return original.call(this, status, ...rest);
  };
}

import type { NextConfig } from "next";

// next/constants içindeki PHASE_PRODUCTION_BUILD ile aynı değer (doğrudan içe aktarmak yapılandırmayı Node ile birim sınamaya engel olur).
const PHASE_PRODUCTION_BUILD = "phase-production-build";

// Same-origin proxy (DECISIONS D-006): tarayıcı yalnızca Next.js origin'ine istek atar.
//
// API_BASE_URL İKİ yerde okunur ve ikisi aynı değeri göstermelidir:
//  1. BURADA, `next build` sırasında: aşağıdaki rewrite hedefi `.next/routes-manifest.json` içine SABİTLENİR (bake).
//     Çalışma zamanında bu değişkeni değiştirmek /api/backend/* rewrite hedefini DEĞİŞTİRMEZ; hedefi değiştirmek için yeniden derleme gerekir.
//  2. `src/proxy.ts` (/admin, /kategori, /urun ön kontrolleri) ve `server/health.mjs` (/readyz) onu ÇALIŞMA ZAMANINDA okur.
// Üretim derlemesinde değişken verilmemişse sessizce yerel adrese düşmek yerine derleme hata verir; dev ve çalışma zamanı etkilenmez.
export function resolveApiBaseUrl(phase: string, env: NodeJS.ProcessEnv = process.env): string {
  const value = env.API_BASE_URL?.trim();
  if (value) return value;
  if (phase === PHASE_PRODUCTION_BUILD) {
    throw new Error("API_BASE_URL is required for `next build`: the /api/backend rewrite target is baked at build time.");
  }
  return "http://localhost:5080";
}

// /api/backend rewrite'ı için upstream yanıt bekleme sınırı (Next experimental.proxyTimeout, varsayılan 30 sn; burada 10 sn).
// API'nin normal yanıtları <1 sn; zaman aşımı server/upstream.mjs ile güvenli 503'e çevrilir. Çalışma zamanında okunur
// (WEB_PROXY_TIMEOUT_MS ile geçersiz kılınabilir; geçersiz/<=0 değer varsayılana düşer).
export const DEFAULT_PROXY_TIMEOUT_MS = 10_000;
export function resolveProxyTimeout(env: NodeJS.ProcessEnv = process.env): number {
  const value = Number(env.WEB_PROXY_TIMEOUT_MS);
  return Number.isInteger(value) && value > 0 && value <= 120_000 ? value : DEFAULT_PROXY_TIMEOUT_MS;
}

export default function config(phase: string): NextConfig {
  const apiBaseUrl = resolveApiBaseUrl(phase);
  return {
    cacheComponents: true,
    partialPrefetching: true,
    reactCompiler: true,
    experimental: { proxyTimeout: resolveProxyTimeout() },
    turbopack: {
      rules: {
        "*.css": {
          loaders: ["@tailwindcss/turbopack"],
          as: "*.css",
        },
      },
    },
    async rewrites() {
      return [
        {
          source: "/api/backend/:path*",
          destination: `${apiBaseUrl}/:path*`,
        },
      ];
    },
  };
}

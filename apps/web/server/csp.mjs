import { createHash } from "node:crypto";
import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";

// cacheComponents (PPR) ön-render edilmiş "statik kabuk" HTML'i derleme zamanında üretilir; isteğe özel nonce taşıyamaz.
// Kabuktaki satır içi betikler (Turbopack yükleyici `_R_`, Suspense yardımcısı `$RB`, `requestAnimationFrame`) derlemeye göre
// sabit içeriklidir; bu yüzden SHA-256 özetleri derleme çıktısından okunup script-src'ye eklenir.
// İsteğe göre akıtılan (stream) betiklere Next nonce'u kendi ekler. 'unsafe-inline' kullanılmaz.
const INLINE_SCRIPT = /<script((?:\s[^>]*)?)>([\s\S]*?)<\/script>/gi;

function* htmlFiles(dir) {
  let entries;
  try { entries = readdirSync(dir, { withFileTypes: true }); } catch { return; }
  for (const entry of entries) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) yield* htmlFiles(full);
    else if (entry.name.endsWith(".html")) yield full;
  }
}

/** `.next/server` altındaki ön-render HTML'lerde nonce'suz satır içi betiklerin 'sha256-…' kaynak ifadeleri (sıralı, tekrarsız). */
export function collectInlineScriptHashes(serverDir) {
  const hashes = new Set();
  for (const file of htmlFiles(serverDir)) {
    let html;
    try { html = readFileSync(file, "utf8"); } catch { continue; }
    for (const [, attributes, body] of html.matchAll(INLINE_SCRIPT)) {
      if (/\bsrc\s*=/i.test(attributes) || /\bnonce\s*=/i.test(attributes) || body.trim() === "") continue;
      hashes.add(`'sha256-${createHash("sha256").update(body).digest("base64")}'`);
    }
  }
  return [...hashes].sort();
}

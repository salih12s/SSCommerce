import { readFileSync } from "node:fs";

// Kimlik bilgileri depoda tutulmaz: PROD_CREDS_FILE ile verilen KEY=VALUE dosyasından çalışma zamanında okunur.
// Hata iletileri yalnız anahtar adlarını söyler, değer yazdırmaz.
const KEYS = ["PROD_BASE_URL", "PROD_ADMIN_EMAIL", "PROD_ADMIN_PASSWORD", "PROD_WAREHOUSE_EMAIL", "PROD_WAREHOUSE_PASSWORD"] as const;
export type Creds = Record<(typeof KEYS)[number], string>;

let cached: Creds | null = null;

export function loadCreds(): Creds {
  if (cached) return cached;
  const file = process.env.PROD_CREDS_FILE;
  if (!file) throw new Error("PROD_CREDS_FILE ortam değişkeni (KEY=VALUE kimlik dosyasının yolu) verilmedi.");
  const parsed: Record<string, string> = {};
  for (const raw of readFileSync(file, "utf8").split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) continue;
    const at = line.indexOf("=");
    if (at < 1) continue;
    parsed[line.slice(0, at).trim()] = line.slice(at + 1).trim().replace(/^(["'])(.*)\1$/, "$2");
  }
  const missing = KEYS.filter((key) => !(parsed[key] ?? process.env[key]));
  if (missing.length > 0) throw new Error(`Kimlik dosyasında eksik anahtar(lar): ${missing.join(", ")}`);
  cached = Object.fromEntries(KEYS.map((key) => [key, parsed[key] ?? process.env[key]!])) as Creds;
  return cached;
}

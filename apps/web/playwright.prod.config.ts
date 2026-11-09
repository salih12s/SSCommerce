import { defineConfig, devices } from "@playwright/test";
import { loadCreds } from "./tests/prod/creds";

// Faz 9: ÜRETİM-EŞDEĞERİ yığına (Caddy -> Next server.mjs -> ASP.NET Production -> PostgreSQL) karşı tarayıcı E2E.
// Bu yapılandırma yığını BAŞLATMAZ (webServer yok): yığın dışarıda çalışıyor olmalı. Kimlik bilgileri depoda tutulmaz;
// PROD_CREDS_FILE ile verilen ortam dosyasından çalışma zamanında okunur (bkz. tests/prod/creds.ts).
// HTTPS sertifikası YEREL CA'dan gelir (herkese açık güvenilir sertifika DEĞİL): ignoreHTTPSErrors bu yüzden açıktır.
const creds = loadCreds();

export default defineConfig({
  testDir: "./tests/prod",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 120_000,
  expect: { timeout: 15_000 },
  forbidOnly: true,
  outputDir: "test-results/prod/artifacts",
  reporter: [["list"], ["html", { outputFolder: "test-results/prod/report", open: "never" }]],
  use: {
    baseURL: creds.PROD_BASE_URL,
    ignoreHTTPSErrors: true, // yerel CA
    ...devices["Desktop Chrome"],
    trace: "off", // iz dosyaları istek gövdelerini (giriş bilgisi) saklayabilir
    screenshot: "only-on-failure",
  },
});

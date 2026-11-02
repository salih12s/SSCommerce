import { defineConfig, devices } from "@playwright/test";

const PORT = 3200;

export default defineConfig({
  testDir: "./tests/e2e",
  globalSetup: "./tests/e2e/start-api.mjs",
  fullyParallel: true,
  workers: 4,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  forbidOnly: !!process.env.CI,
  reporter: [["list"]],
  use: {
    baseURL: `http://localhost:${PORT}`,
    ...devices["Desktop Chrome"],
  },
  // İki proje: "main" paralel koşar ve paylaşılan katalog verisini yalnız okur. "mutating" fiyat/stok/aktiflik fixture'ını değiştirir;
  // `dependencies` ile main bittikten sonra başlar ve tek dosya içinde seri (serial) koşar, böylece başka spec değişmiş veri görmez.
  // "ordering" (Faz 7) aynı düzende bir adım sonra gelir: sipariş stok düşürdüğü için paylaşılan katalogu okuyan hiçbir spec onunla yarışmaz.
  // Not: Playwright worker sayısı proje başına ayarlanamaz; tek worker garantisi "tek dosya + serial mod" ile sağlanır.
  projects: [
    { name: "main", testIgnore: /(mutations|orders)\.spec\.ts/ },
    { name: "mutating", testMatch: /mutations\.spec\.ts/, dependencies: ["main"], fullyParallel: false },
    // Siparişler stok TÜKETİR: tek dosya, yalnız main ve mutating bittikten sonra, tek worker'da sırayla koşar. Her test kullandığı stoğu fixture ile geri koyar.
    { name: "ordering", testMatch: /orders\.spec\.ts/, dependencies: ["mutating"], fullyParallel: false },
  ],
  webServer: {
    // Sunucu mevcut üretim derlemesini 3200'de açar: yerelde `npm run test:e2e` önce build alır,
    // CI'da build adımı bir kez çalışır ve ardından `npm run e2e` gelir.
    command: `npm run start -- -p ${PORT}`,
    env: { API_BASE_URL: "http://127.0.0.1:5081" },
    url: `http://localhost:${PORT}`,
    reuseExistingServer: false,
    timeout: 60_000,
  },
});

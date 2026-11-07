import { defineConfig, devices } from "@playwright/test";

const PORT = 3200;
// E2E API'nin günlük sipariş sınırı (Ordering:MaxOrdersPerRollingDay). Sunucu varsayılanı 10'dur; Faz 7 sayfalama testi tek kullanıcıyla 11 sipariş verir (sayfa boyutu 10),
// bu yüzden E2E için 11. Açık sipariş sınırı (3) varsayılanda kalır. admin-limits.spec.ts bu değeri aynı ortam değişkeninden okur. E2eHost değişkeni API sürecine iletir.
process.env.E2E_MAX_ORDERS_PER_DAY ??= "11";

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
    // admin-access ve admin-responsive paylaşılan kataloğu yalnız okur (kendi hesaplarını kurar), bu yüzden main'dedir. Diğer admin-* dosyaları katalog/stok yazar: aşağıda zincirlenir.
    { name: "main", testIgnore: /(mutations|orders|admin-catalog|admin-stock|admin-fulfilment|admin-limits)\.spec\.ts/ },
    { name: "mutating", testMatch: /mutations\.spec\.ts/, dependencies: ["main"], fullyParallel: false },
    // Siparişler stok TÜKETİR: tek dosya, yalnız main ve mutating bittikten sonra, tek worker'da sırayla koşar. Her test kullandığı stoğu fixture ile geri koyar.
    { name: "ordering", testMatch: /orders\.spec\.ts/, dependencies: ["mutating"], fullyParallel: false },
    // Faz 8 yönetim yazma spec'leri: her biri kendi benzersiz kategori/ürün/varyantını kurar ve pasife alarak bırakır, ama vitrin katalogunu (yeni ürünler) ve stok hareketlerini değiştirir.
    // Bu yüzden vitrin projelerinden SONRA, birbirinin ardından (tek dosya = tek worker) koşar; her proje bir öncekine bağlıdır.
    { name: "admin-catalog", testMatch: /admin-catalog\.spec\.ts/, dependencies: ["ordering"], fullyParallel: false },
    { name: "admin-stock", testMatch: /admin-stock\.spec\.ts/, dependencies: ["admin-catalog"], fullyParallel: false },
    { name: "admin-fulfilment", testMatch: /admin-fulfilment\.spec\.ts/, dependencies: ["admin-stock"], fullyParallel: false },
    { name: "admin-limits", testMatch: /admin-limits\.spec\.ts/, dependencies: ["admin-fulfilment"], fullyParallel: false },
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

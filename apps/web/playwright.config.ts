import { defineConfig, devices } from "@playwright/test";

const PORT = 3200;

export default defineConfig({
  testDir: "./tests/e2e",
  fullyParallel: true,
  reporter: [["list"]],
  use: {
    baseURL: `http://localhost:${PORT}`,
    ...devices["Desktop Chrome"],
  },
  webServer: {
    // `npm run test:e2e` önce build alır; sunucu üretim derlemesini 3200'de açar.
    command: `npm run start -- -p ${PORT}`,
    url: `http://localhost:${PORT}`,
    reuseExistingServer: false,
    timeout: 60_000,
  },
});

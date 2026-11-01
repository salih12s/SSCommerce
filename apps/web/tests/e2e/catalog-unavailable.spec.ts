import { expect, test } from "@playwright/test";
import { spawn, type ChildProcess } from "node:child_process";

test.describe("catalog connection failure", () => {
  test.describe.configure({ mode: "serial" });
  let web: ChildProcess;
  const origin = "http://127.0.0.1:3201";
  test.beforeAll(async () => {
    web = spawn(process.execPath, ["server.mjs", "-p", "3201"], {
      cwd: process.cwd(), windowsHide: true, stdio: ["ignore", "ignore", "pipe"],
      env: { ...process.env, API_BASE_URL: "http://127.0.0.1:1" },
    });
    web.stderr?.resume();
    let ready = false;
    for (let i = 0; i < 100 && web.exitCode === null; i++) {
      try { ready = (await fetch(`${origin}/giris`)).ok; } catch { /* Startup */ }
      if (ready) break;
      await new Promise((resolve) => setTimeout(resolve, 200));
    }
    expect(ready).toBe(true);
  });
  test.afterAll(async () => {
    if (!web || web.exitCode !== null) return;
    await new Promise<void>((resolve) => { web.once("exit", () => resolve()); web.kill(); });
  });
  for (const path of ["/", "/arama?q=kulaklik", "/kategori/oyun", "/urun/ton-kulakustu-kulaklik"]) {
    test(`no fixture fallback when API is unreachable: ${path}`, async ({ page }) => {
      await page.goto(`${origin}${path}`);
      await expect(page.locator("main")).toContainText(/şu anda yüklenemedi/i);
      await expect(page.locator("main article:has(h3)")).toHaveCount(0);
    });
  }
});

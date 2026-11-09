import { spawn } from "node:child_process";

if (!process.env.SSCOMMERCE_TEST_POSTGRES) throw new Error("SSCOMMERCE_TEST_POSTGRES is required for real PostgreSQL browser tests.");
const env = { ...process.env, API_BASE_URL: "http://127.0.0.1:5081" };
const args = process.argv.slice(2);
async function run(script, argv) {
  return await new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [script, ...argv], { env, stdio: "inherit", windowsHide: true });
    child.once("error", reject);
    child.once("exit", (code) => resolve(code ?? 1));
  });
}
if (args.includes("--build")) {
  const code = await run("node_modules/next/dist/bin/next", ["build"]);
  if (code !== 0) process.exit(code);
}
process.exitCode = await run("node_modules/@playwright/test/cli.js", ["test", ...args.filter((v) => v !== "--build")]);

import { spawn } from "node:child_process";
import { once } from "node:events";
import { fileURLToPath } from "node:url";
import path from "node:path";
import readline from "node:readline";

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../../..");
const project = path.join(repo, "backend/tools/SSCommerce.E2eHost/SSCommerce.E2eHost.csproj");
const dll = path.join(repo, "backend/tools/SSCommerce.E2eHost/bin/Release/net10.0/SSCommerce.E2eHost.dll");

function runBuild() {
  return new Promise((resolve, reject) => {
    const build = spawn("dotnet", ["build", project, "-c", "Release"], {
      cwd: repo,
      stdio: ["ignore", "pipe", "pipe"],
      windowsHide: true,
    });
    let output = "";
    for (const stream of [build.stdout, build.stderr]) {
      stream.setEncoding("utf8");
      stream.on("data", (chunk) => { output = (output + chunk).slice(-12_000); });
    }
    build.once("error", reject);
    build.once("exit", (code) => {
      if (code === 0) resolve();
      else reject(new Error(`E2E host build failed (exit ${code}).\n${output}`));
    });
  });
}

export default async function startApi() {
  if (!process.env.SSCOMMERCE_TEST_POSTGRES) {
    throw new Error("SSCOMMERCE_TEST_POSTGRES is required for browser E2E tests.");
  }

  await runBuild();
  const host = spawn("dotnet", [dll], {
    cwd: repo,
    stdio: ["pipe", "pipe", "pipe"],
    windowsHide: true,
  });
  // The child may exit between the status check and the teardown write.
  host.stdin.on("error", () => { });

  let ready = false;
  let cleaned = false;
  let stderr = "";
  const lines = readline.createInterface({ input: host.stdout });
  lines.on("line", (line) => {
    if (line === "E2E_API_READY") ready = true;
    if (line === "E2E_DB_CLEANED") cleaned = true;
  });
  host.stderr.setEncoding("utf8");
  host.stderr.on("data", (chunk) => { stderr = (stderr + chunk).slice(-2_000); });

  async function stop() {
    if (host.exitCode !== null) return;
    const exited = once(host, "close");
    host.stdin.end("stop\n");
    let timer;
    const timeout = new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error("E2E host cleanup timed out.")), 20_000);
    });
    try {
      await Promise.race([exited, timeout]);
    } catch (error) {
      host.kill();
      throw error;
    } finally {
      clearTimeout(timer);
    }
  }

  try {
    const start = Date.now();
    while (!ready && host.exitCode === null && Date.now() - start < 90_000) {
      await new Promise((resolve) => setTimeout(resolve, 100));
    }
    if (!ready) {
      throw new Error(`E2E API did not start. ${stderr.trim()}`);
    }
    console.log("E2E API ready at http://127.0.0.1:5081 (isolated PostgreSQL).");
  } catch (error) {
    await stop();
    throw error;
  }

  return async () => {
    await stop();
    if (!cleaned) throw new Error(`E2E database cleanup was not confirmed. ${stderr.trim()}`);
  };
}

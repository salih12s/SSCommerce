import { spawn } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs";
import net from "node:net";
import path from "node:path";

// VT-3: E2E API'sinin geçici veritabanına `--seed-showcase` komutunu çalıştırır.
// Sorun: E2eHost veritabanı adını (sscommerce_test_<guid>) yalnız kendi içinde tutar ve dışarı vermez. Bu dosya
//   1) SSCOMMERCE_TEST_POSTGRES'ten yalnız sunucu bilgisini okur (kullanıcı/parola/adres),
//   2) `pg_stat_activity` üzerinden API'nin şu an bağlı olduğu tek `sscommerce_test_*` veritabanını bulur (küçük, bağımlılıksız bir PostgreSQL istemcisiyle),
//   3) Release API derlemesini `--seed-showcase` ile aynı veritabanına karşı çalıştırır.
// Hiçbir parola/bağlantı dizesi günlüğe, hata iletisine ya da dosyaya yazılmaz. Komutun kendi güvenlik kuralları (yalnız Development + loopback sunucu) geçerlidir.

const repo = path.resolve(__dirname, "../../../..");
const apiDll = path.join(
  repo,
  "backend/src/SSCommerce.Api/bin/Release/net10.0/SSCommerce.Api.dll",
);

type Server = { host: string; port: number; user: string; password: string };

function parseSource(source: string): Server {
  const map = new Map<string, string>();
  for (const part of source.split(";")) {
    const i = part.indexOf("=");
    if (i > 0)
      map.set(part.slice(0, i).trim().toLowerCase(), part.slice(i + 1).trim());
  }
  const pick = (...keys: string[]) =>
    keys.map((k) => map.get(k)).find((v) => v !== undefined);
  return {
    host: pick("host", "server") ?? "127.0.0.1",
    port: Number(pick("port") ?? 5432),
    user: pick("username", "user id", "userid", "user", "uid") ?? "postgres",
    password: pick("password", "pwd") ?? "",
  };
}

/** Kaynak dizeden Database anahtarını atıp verilen veritabanını ekler. */
function withDatabase(source: string, database: string): string {
  const kept = source
    .split(";")
    .filter(
      (part) =>
        part.trim() && !/^\s*(database|initial catalog)\s*=/i.test(part),
    );
  return `${kept.join(";")};Database=${database}`;
}

const int32 = (n: number) => {
  const b = Buffer.alloc(4);
  b.writeInt32BE(n);
  return b;
};
const cstr = (s: string) =>
  Buffer.concat([Buffer.from(s, "utf8"), Buffer.from([0])]);
const message = (type: string, body: Buffer) =>
  Buffer.concat([Buffer.from(type), int32(body.length + 4), body]);
const hmac = (key: Buffer, data: string | Buffer) =>
  crypto.createHmac("sha256", key).update(data).digest();

/** Çok küçük PostgreSQL istemcisi: yalnız parola/md5/SCRAM-SHA-256 doğrulaması ve tek bir basit sorgu (metin sonuçlar). */
async function query(server: Server, sql: string): Promise<string[][]> {
  return await new Promise((resolve, reject) => {
    const socket = net.connect(server.port, server.host);
    const rows: string[][] = [];
    let buffer = Buffer.alloc(0);
    let sent = false;
    let nonce = "";
    let clientFirstBare = "";
    let serverSignature: Buffer | null = null;
    const fail = (why: string) => {
      socket.destroy();
      reject(new Error(`PostgreSQL sorgusu başarısız (${why}).`));
    };
    socket.setTimeout(10_000, () => fail("zaman aşımı"));
    socket.on("error", () => fail("bağlantı"));
    socket.on("connect", () => {
      const params = Buffer.concat([
        cstr("user"),
        cstr(server.user),
        cstr("database"),
        cstr("postgres"),
        Buffer.from([0]),
      ]);
      socket.write(
        Buffer.concat([int32(params.length + 8), int32(196608), params]),
      );
    });
    socket.on("data", (chunk) => {
      buffer = Buffer.concat([buffer, chunk]);
      while (buffer.length >= 5) {
        const type = String.fromCharCode(buffer[0]);
        const length = buffer.readInt32BE(1);
        if (buffer.length < 1 + length) return;
        const body = buffer.subarray(5, 1 + length);
        buffer = buffer.subarray(1 + length);
        if (type === "R") {
          const code = body.readInt32BE(0);
          if (code === 3) socket.write(message("p", cstr(server.password)));
          else if (code === 5) {
            const md5 = (d: string | Buffer) =>
              crypto.createHash("md5").update(d).digest("hex");
            socket.write(
              message(
                "p",
                cstr(
                  "md5" +
                    md5(
                      Buffer.concat([
                        Buffer.from(md5(server.password + server.user)),
                        body.subarray(4, 8),
                      ]),
                    ),
                ),
              ),
            );
          } else if (code === 10) {
            nonce = crypto.randomBytes(18).toString("base64");
            clientFirstBare = `n=,r=${nonce}`;
            const first = Buffer.from(`n,,${clientFirstBare}`);
            socket.write(
              message(
                "p",
                Buffer.concat([
                  cstr("SCRAM-SHA-256"),
                  int32(first.length),
                  first,
                ]),
              ),
            );
          } else if (code === 11) {
            const serverFirst = body.subarray(4).toString();
            const fields = Object.fromEntries(
              serverFirst.split(",").map((f) => [f[0], f.slice(2)]),
            );
            if (!fields.r?.startsWith(nonce)) return fail("SCRAM nonce");
            const salted = crypto.pbkdf2Sync(
              server.password,
              Buffer.from(fields.s, "base64"),
              Number(fields.i),
              32,
              "sha256",
            );
            const clientKey = hmac(salted, "Client Key");
            const storedKey = crypto
              .createHash("sha256")
              .update(clientKey)
              .digest();
            const withoutProof = `c=biws,r=${fields.r}`;
            const authMessage = `${clientFirstBare},${serverFirst},${withoutProof}`;
            const signature = hmac(storedKey, authMessage);
            const proof = Buffer.from(
              clientKey.map((b, i) => b ^ signature[i]),
            );
            serverSignature = hmac(hmac(salted, "Server Key"), authMessage);
            socket.write(
              message(
                "p",
                Buffer.from(`${withoutProof},p=${proof.toString("base64")}`),
              ),
            );
          } else if (code === 12) {
            const got = body.subarray(4).toString().replace(/^v=/, "");
            if (!serverSignature || got !== serverSignature.toString("base64"))
              return fail("SCRAM sunucu imzası");
          } else if (code !== 0)
            return fail(`kimlik doğrulama yöntemi ${code}`);
        } else if (type === "Z") {
          if (!sent) {
            sent = true;
            socket.write(message("Q", cstr(sql)));
          } else {
            socket.write(message("X", Buffer.alloc(0)));
            socket.end();
            resolve(rows);
          }
        } else if (type === "D") {
          const count = body.readInt16BE(0);
          let offset = 2;
          const row: string[] = [];
          for (let i = 0; i < count; i++) {
            const len = body.readInt32BE(offset);
            offset += 4;
            row.push(
              len < 0
                ? ""
                : body.subarray(offset, offset + len).toString("utf8"),
            );
            offset += Math.max(len, 0);
          }
          rows.push(row);
        } else if (type === "E") return fail("sunucu hatası");
      }
    });
  });
}

/** API'nin şu an bağlı olduğu E2E veritabanı. Tam olarak bir tane değilse açık hata (yanlış veritabanına yazmamak için tahmin yok). */
async function findE2eDatabase(source: string): Promise<string> {
  const server = parseSource(source);
  const safe = (rows: string[][]) =>
    rows
      .map((r) => r[0])
      .filter((n) => /^sscommerce_test_[0-9a-f]{32}$/.test(n));
  // Önce etkin bağlantısı olanlar (artık kalan eski veritabanları bağlantısızdır); sonuç yoksa tüm test veritabanları.
  let names = safe(
    await query(
      server,
      "SELECT DISTINCT datname FROM pg_stat_activity WHERE datname LIKE 'sscommerce\\_test\\_%'",
    ),
  );
  if (names.length === 0)
    names = safe(
      await query(
        server,
        "SELECT datname FROM pg_database WHERE datname LIKE 'sscommerce\\_test\\_%'",
      ),
    );
  if (names.length !== 1)
    throw new Error(
      `E2E veritabanı belirlenemedi: sscommerce_test_* veritabanı sayısı ${names.length}, 1 bekleniyordu.`,
    );
  return names[0];
}

/** `--seed-showcase` komutunu çalıştırır; çıkış kodu 0 değilse komutun (parolasız) çıktısıyla hata verir. İdempotenttir. */
export async function seedShowcase(): Promise<void> {
  const source = process.env.SSCOMMERCE_TEST_POSTGRES;
  if (!source) throw new Error("SSCOMMERCE_TEST_POSTGRES gerekli.");
  if (!fs.existsSync(apiDll))
    throw new Error(
      "Release API derlemesi yok (E2eHost derlemesi onu üretir).",
    );
  const database = await findE2eDatabase(source);
  const output = await new Promise<{ code: number; text: string }>(
    (resolve, reject) => {
      const child = spawn("dotnet", [apiDll, "--seed-showcase"], {
        cwd: repo,
        windowsHide: true,
        stdio: ["ignore", "pipe", "pipe"],
        env: {
          ...process.env,
          ASPNETCORE_ENVIRONMENT: "Development",
          DOTNET_ENVIRONMENT: "Development",
          ConnectionStrings__Postgres: withDatabase(source, database),
          Security__AllowedOrigins__0: "http://localhost:3200",
          Security__AllowedOrigins__1: "http://127.0.0.1:3200",
          Security__AssumeHttps: "false",
        },
      });
      let text = "";
      for (const stream of [child.stdout, child.stderr]) {
        stream.setEncoding("utf8");
        stream.on("data", (c: string) => {
          text = (text + c).slice(-4_000);
        });
      }
      child.once("error", reject);
      child.once("exit", (code) => resolve({ code: code ?? 1, text }));
    },
  );
  if (output.code !== 0)
    throw new Error(
      `--seed-showcase çıkış kodu ${output.code}.\n${output.text}`,
    );
}

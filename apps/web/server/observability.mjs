import { randomUUID } from "node:crypto";

// Korelasyon kimliği: yalnız güvenli biçimdeki gelen değer kabul edilir (günlük/başlık enjeksiyonu ve uzun değer riski yok).
const REQUEST_ID = /^[A-Za-z0-9._-]{8,64}$/;
const MAX_PATH = 200;

export function resolveRequestId(incoming) {
  const value = Array.isArray(incoming) ? incoming[0] : incoming;
  return typeof value === "string" && REQUEST_ID.test(value) ? value : randomUUID().replaceAll("-", "");
}

/** Günlüğe yalnız yol girer: sorgu dizesi, parça, çerez, başlık, gövde ve IP asla. */
export function logPath(rawUrl) {
  const text = typeof rawUrl === "string" ? rawUrl : "/";
  const end = text.search(/[?#]/);
  return (end === -1 ? text : text.slice(0, end)).slice(0, MAX_PATH);
}

export function accessLogEntry({ request, response, requestId, startedAt, now = Date.now() }) {
  const status = response.statusCode;
  const aborted = !response.writableFinished;
  return {
    ts: new Date(now).toISOString(),
    level: status >= 500 ? "error" : aborted ? "warn" : "info",
    msg: "http_request",
    method: String(request.method ?? "").slice(0, 16),
    path: logPath(request.url),
    status,
    durationMs: Math.max(0, Math.round(now - startedAt)),
    requestId,
  };
}

export function writeLog(entry, write = (line) => process.stdout.write(`${line}\n`)) {
  write(JSON.stringify(entry));
}

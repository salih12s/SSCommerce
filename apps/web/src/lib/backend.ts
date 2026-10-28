// Sunucu tarafı backend health okuma yardımcısı. İş mantığı içermez.

export type HealthResult =
  | { state: "up"; httpStatus: number; body: string }
  | { state: "down"; httpStatus: number; body: string }
  | { state: "unreachable"; reason: string };

const TIMEOUT_MS = 2500;
const MAX_BODY_CHARS = 1000;

function apiBaseUrl(): string {
  return (process.env.API_BASE_URL ?? "http://localhost:5080").replace(/\/+$/, "");
}

/** `path`: "/health/live" veya "/health/ready". 2xx dışındaki her yanıt "down" sayılır. */
export async function readHealth(path: "/health/live" | "/health/ready"): Promise<HealthResult> {
  try {
    const res = await fetch(`${apiBaseUrl()}${path}`, {
      cache: "no-store",
      signal: AbortSignal.timeout(TIMEOUT_MS),
    });
    const body = (await res.text()).slice(0, MAX_BODY_CHARS);
    return res.ok
      ? { state: "up", httpStatus: res.status, body }
      : { state: "down", httpStatus: res.status, body };
  } catch (error) {
    const reason =
      error instanceof Error && (error.name === "TimeoutError" || error.name === "AbortError")
        ? `${TIMEOUT_MS} ms içinde yanıt gelmedi`
        : "bağlantı kurulamadı";
    return { state: "unreachable", reason };
  }
}

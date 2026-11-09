import { apiGet, apiSend, type ApiResult, type SendOptions } from "@/lib/api";

// Yönetim alanı API yardımcıları. Tek ek davranış: herhangi bir yanıt 401 ise dinleyenler (AdminShell) haberdar edilir; oturum sayfa açıkken de bitebilir.
const listeners = new Set<() => void>();
export function onSessionLost(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; }
function watch<T>(result: ApiResult<T>): ApiResult<T> {
  if (!result.ok && result.status === 401) listeners.forEach((listener) => listener());
  return result;
}

export const aGet = async <T,>(path: string) => watch(await apiGet<T>(path));
export const aSend = async <T,>(method: "POST" | "PUT" | "PATCH" | "DELETE", path: string, body?: unknown, options?: SendOptions) => watch(await apiSend<T>(method, path, body, options));

export const enc = encodeURIComponent;
/** Boş değerleri atlayan sorgu dizgesi. */
export function qs(params: Record<string, string | number | boolean | null | undefined>): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) if (value !== undefined && value !== null && value !== "") query.set(key, String(value));
  const text = query.toString();
  return text ? `?${text}` : "";
}

export const isConflict = (result: { code: string }) => result.code === "version_conflict";

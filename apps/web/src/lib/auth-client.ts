export type Account = { id: string; email: string; emailConfirmed: boolean; roles: string[]; capabilities: { admin: boolean; warehouse: boolean } };
type Problem = { title?: string; code?: string; errors?: Record<string, string[]> };
export type AuthResult<T> = { ok: true; data: T } | { ok: false; status: number; message: string; errors: Record<string, string[]> };
const api = "/api/backend/api/v1/auth";

async function readProblem(response: Response): Promise<Problem> {
  try { return await response.json() as Problem; } catch { return {}; }
}
function message(status: number, problem: Problem, action?: string) {
  if (status === 401) return action === "login" ? "E-posta veya parola hatalı. Lütfen tekrar deneyin." : "Oturumun sona erdi. Lütfen yeniden giriş yap.";
  if (status === 403) return "İstek güvenlik kontrolünden geçmedi. Sayfayı yenileyip tekrar deneyin.";
  if (status === 409) return "Bu e-posta adresi kullanılamıyor.";
  if (status === 429) return "Çok fazla deneme yapıldı. Lütfen biraz sonra tekrar deneyin.";
  return problem.title ?? "İşlem tamamlanamadı. Lütfen tekrar deneyin.";
}
export async function fetchAccount(): Promise<AuthResult<Account>> {
  try {
    const response = await fetch(`${api}/me`, { credentials: "same-origin", cache: "no-store" });
    if (response.ok) return { ok: true, data: await response.json() as Account };
    const problem = await readProblem(response);
    return { ok: false, status: response.status, message: message(response.status, problem), errors: problem.errors ?? {} };
  } catch { return { ok: false, status: 0, message: "Sunucuya bağlanılamadı.", errors: {} }; }
}
export async function authMutation<T>(action: "register" | "login" | "logout", body?: { email: string; password: string }): Promise<AuthResult<T>> {
  try {
    const tokenResponse = await fetch(`${api}/csrf`, { credentials: "same-origin", cache: "no-store" });
    if (!tokenResponse.ok) return { ok: false, status: tokenResponse.status, message: "Güvenlik doğrulaması alınamadı. Tekrar deneyin.", errors: {} };
    const { token } = await tokenResponse.json() as { token: string };
    const response = await fetch(`${api}/${action}`, {
      method: "POST", credentials: "same-origin", cache: "no-store",
      headers: { "X-CSRF-TOKEN": token, ...(body ? { "Content-Type": "application/json" } : {}) },
      body: body ? JSON.stringify(body) : undefined,
    });
    if (!response.ok) {
      const problem = await readProblem(response);
      return { ok: false, status: response.status, message: message(response.status, problem, action), errors: problem.errors ?? {} };
    }
    // The next mutation always obtains a fresh token for the current identity.
    return { ok: true, data: response.status === 204 ? undefined as T : await response.json() as T };
  } catch { return { ok: false, status: 0, message: "Sunucuya bağlanılamadı.", errors: {} }; }
}

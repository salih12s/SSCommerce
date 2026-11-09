import { NextResponse, type NextRequest } from "next/server";
import { loginHref, safeNext } from "@/lib/safe-next";

// cacheComponents streams route bodies. Resolve missing resources before streaming so HTTP status stays 404.
// /admin: oturum ve rol ön kontrolü (aşağıda). Backend her uçta yetkiyi yine kendisi uygular; bu kapı yalnız doğru HTTP durumunu ve yönlendirmeyi verir.
export const config = { matcher: ["/kategori/:slug", "/urun/:slug", "/admin/:path*"] };
const base = (process.env.API_BASE_URL ?? "http://localhost:5080").replace(/\/+$/, "");
const NO_STORE = "private, no-store";

type Role = "Admin" | "Warehouse";
const isStaffRole = (value: unknown): value is Role => value === "Admin" || value === "Warehouse";

/** /auth/me gövdesi beklenen biçimde mi? Değilse güvenilmez ve istek olduğu gibi geçer. Rol listesi yalnız dizi + metinlerden oluşuyorsa kabul edilir. */
function redirectTo(request: NextRequest, path: string) {
  const response = NextResponse.redirect(new URL(path, request.url), 307);
  response.headers.set("Cache-Control", NO_STORE);
  return response;
}

// Next, rewrite yanıtının durum kodunu yok sayar (200 döner); gerçek HTTP 403 için yanıt doğrudan burada üretilir. Sayfa durağandır: kişisel veri, betik ve dış kaynak içermez.
const forbiddenHtml = (nonce?: string) => `<!doctype html><html lang="tr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="robots" content="noindex,nofollow"><title>Erişim yok | SSCommerce</title>
<style${nonce ? ` nonce="${nonce}"` : ""}>body{margin:0;background:#f6f1e8;color:#1b1d21;font:16px/1.55 system-ui,"Segoe UI",sans-serif}main{max-width:36rem;margin:0 auto;padding:4rem 1rem}h1{font-size:2rem;line-height:1.15;margin:1rem 0 .75rem}p{color:#5a554c}.stamp{display:inline-block;transform:rotate(-2deg);border:2px solid #a63400;border-radius:6px;padding:.1rem .6rem;font-weight:800;color:#a63400}a{display:inline-flex;align-items:center;min-height:44px;padding:0 1.25rem;margin:.5rem .75rem 0 0;border-radius:6px;font-weight:600;text-decoration:none;background:#16181b;color:#f4efe6}a.alt{background:#fffcf6;color:#1b1d21;border:1px solid #d8cebd}a:focus-visible{outline:3px solid #1b1d21;outline-offset:3px}</style></head>
<body><main><span class="stamp">403</span><h1>Bu alana erişimin yok</h1><p>Yönetim alanı yalnızca yönetici ve depo personeli içindir. Depo personeli yalnızca sipariş ekranlarını görür. Yetki gerekiyorsa hesap kimliğini Hesabım sayfasından kopyalayıp bir yöneticiye ilet.</p><a href="/hesabim">Hesabım</a><a class="alt" href="/">Mağazaya dön</a></main></body></html>`;
// Satır içi <style> için CSP nonce'u: server.mjs isteğe `content-security-policy` başlığını yazar (Next'in kendi betikleri için yaptığı gibi).
const cspNonce = (request: NextRequest) => /'nonce-([A-Za-z0-9+/=_-]+)'/.exec(request.headers.get("content-security-policy") ?? "")?.[1];
function forbiddenPage(request: NextRequest) {
  return new NextResponse(forbiddenHtml(cspNonce(request)), { status: 403, headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": NO_STORE } });
}

function rolesOf(body: unknown): string[] | null {
  if (typeof body !== "object" || body === null) return null;
  const { id, roles } = body as { id?: unknown; roles?: unknown };
  if (typeof id !== "string" || !Array.isArray(roles) || !roles.every((r) => typeof r === "string")) return null;
  return roles as string[];
}

const onOrdersPage = (pathname: string) => pathname === "/admin/siparisler" || pathname.startsWith("/admin/siparisler/");

async function guardAdmin(request: NextRequest) {
  const { pathname } = request.nextUrl;
  const query = new URLSearchParams(request.nextUrl.searchParams);
  query.delete("_rsc"); // Next'in iç RSC parametresi dönüş adresine taşınmaz
  const search = query.size > 0 ? `?${query.toString()}` : "";
  const headers: Record<string, string> = {};
  // Yalnız çerez ve server.mjs'in ürettiği istemci IP'si iletilir (başka başlık yok).
  const cookie = request.headers.get("cookie");
  if (cookie) headers.cookie = cookie;
  const forwarded = request.headers.get("x-forwarded-for");
  if (forwarded) headers["x-forwarded-for"] = forwarded;

  const pass = () => NextResponse.next({ headers: { "Cache-Control": NO_STORE } });
  let response: Response;
  try {
    response = await fetch(`${base}/api/v1/auth/me`, { headers, redirect: "manual", cache: "no-store", signal: AbortSignal.timeout(2000) });
  } catch { return pass(); } // ağ hatası ya da zaman aşımı: çıkış gibi görünmesin, sayfa kendi hata durumunu gösterir
  if (response.status === 401) {
    // Göreli Location: istek sahte Host ile gelse bile dış adrese yönlenmez.
    return redirectTo(request, loginHref(safeNext(`${pathname}${search}`)));
  }
  if (response.status !== 200) return pass(); // 429, 5xx, beklenmeyen: geçir
  let roles: string[] | null;
  try { roles = rolesOf(await response.json()); } catch { return pass(); }
  if (roles === null) return pass();

  const staff = roles.filter(isStaffRole);
  const forbidden = () => forbiddenPage(request);
  if (staff.length === 0) return forbidden();
  if (!staff.includes("Admin")) {
    // Depo: yalnız sipariş ekranları. /admin kökü sipariş listesine gider.
    if (pathname === "/admin" || pathname === "/admin/") return redirectTo(request, "/admin/siparisler");
    if (!onOrdersPage(pathname)) return forbidden();
  }
  return pass();
}

export async function proxy(request: NextRequest) {
  const pathname = request.nextUrl.pathname;
  if (pathname === "/admin" || pathname.startsWith("/admin/")) return guardAdmin(request);
  const [kind, encoded] = pathname.split("/").filter(Boolean);
  let slug: string;
  try { slug = decodeURIComponent(encoded ?? ""); }
  catch { return NextResponse.rewrite(new URL("/bulunamadi", request.url)); }
  const path = kind === "kategori"
    ? `/api/v1/products?category=${encodeURIComponent(slug)}&pageSize=1`
    : `/api/v1/products/${encodeURIComponent(slug)}`;
  try {
    const response = await fetch(`${base}${path}`, { cache: "no-store", signal: AbortSignal.timeout(5000) });
    if (response.status === 404) return NextResponse.rewrite(new URL("/bulunamadi", request.url));
  } catch { /* Page displays the catalog availability error. */ }
  return NextResponse.next();
}

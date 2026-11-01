import { NextResponse, type NextRequest } from "next/server";

// cacheComponents streams route bodies. Resolve missing resources before streaming so HTTP status stays 404.
export const config = { matcher: ["/kategori/:slug", "/urun/:slug"] };
const base = (process.env.API_BASE_URL ?? "http://localhost:5080").replace(/\/+$/, "");

export async function proxy(request: NextRequest) {
  const [kind, encoded] = request.nextUrl.pathname.split("/").filter(Boolean);
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

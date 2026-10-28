import { NextResponse, type NextRequest } from "next/server";
import { getCategory } from "@/content/demo-catalog";

// cacheComponents açıkken dynamicParams kullanılamaz ve sayfa içindeki notFound() akış başladıktan
// sonra çalıştığı için 200 döner. Bilinmeyen kategori slug'ı burada, akıştan önce 404'e çevrilir.
export const config = { matcher: "/kategori/:slug" };

export function proxy(request: NextRequest) {
  const slug = decodeURIComponent(request.nextUrl.pathname.split("/")[2] ?? "");
  if (!getCategory(slug)) {
    return NextResponse.rewrite(new URL("/bulunamadi", request.url));
  }
}

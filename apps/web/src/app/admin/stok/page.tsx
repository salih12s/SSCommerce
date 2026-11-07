import type { Metadata } from "next";
import { Suspense } from "react";
import { StockAdmin } from "@/components/admin/StockAdmin";

export const metadata: Metadata = { title: "Stok" };
export default function AdminStockPage() {
  return <Suspense fallback={<p role="status" className="py-6 text-fg-muted">Stok yükleniyor…</p>}><StockAdmin /></Suspense>;
}

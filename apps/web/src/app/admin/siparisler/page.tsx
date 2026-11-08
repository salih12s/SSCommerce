import type { Metadata } from "next";
import { Suspense } from "react";
import { OrdersAdminList } from "@/features/admin/components/OrdersAdmin";

export const metadata: Metadata = { title: "Siparişler" };
export default function AdminOrdersPage() {
  return <Suspense fallback={<p role="status" className="py-6 text-fg-muted">Siparişler yükleniyor…</p>}><OrdersAdminList /></Suspense>;
}

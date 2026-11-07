import type { Metadata } from "next";
import { Suspense } from "react";
import { OrderAdminDetail } from "@/components/admin/OrdersAdmin";

export const metadata: Metadata = { title: "Sipariş" };
type Props = { params: Promise<{ id: string }> };
// Yalnız rota kimliği sunucudan geçer; sipariş içeriği tarayıcıda okunur.
async function Detail({ params }: Props) {
  const { id } = await params;
  return <OrderAdminDetail key={id} id={id} />;
}
export default function AdminOrderPage({ params }: Props) {
  return <Suspense fallback={<p role="status" className="py-6 text-fg-muted">Sipariş yükleniyor…</p>}><Detail params={params} /></Suspense>;
}

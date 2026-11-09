import type { Metadata } from "next";
import { Suspense } from "react";
import { OrderDetailView } from "@/features/orders/components/OrderDetailView";
export const metadata: Metadata = { title: "Sipariş", robots: { index: false } };
type Props = { params: Promise<{ id: string }> };
// Yalnız rota kimliği (kişisel veri değil) sunucudan geçer; sipariş içeriği ve sorgu parametreleri okunmaz, sipariş tarayıcıda alınır.
async function Detail({ params }: Props) {
  const { id } = await params;
  return <OrderDetailView key={id} id={id} />;
}
export default function OrderPage({ params }: Props) {
  return <Suspense fallback={<div className="mx-auto max-w-3xl px-4 py-10 sm:px-6 md:py-14"><h1 className="font-display text-title font-extrabold">Sipariş</h1><p role="status" className="mt-6">Sipariş yükleniyor…</p></div>}><Detail params={params} /></Suspense>;
}

import type { Metadata } from "next";
import { Suspense } from "react";
import { OrdersView } from "@/components/OrdersView";
export const metadata: Metadata = { title: "Siparişlerim", robots: { index: false } };
// Sipariş verisi sunucuda okunmaz: sayfa kabuğu herkes için aynıdır, liste tarayıcıda oturum çerezi ile alınır. useSearchParams Suspense ister.
export default function OrdersPage() {
  return <Suspense fallback={<div className="mx-auto max-w-3xl px-4 py-10 sm:px-6 md:py-14"><h1 className="font-display text-title font-extrabold">Siparişlerim</h1><p role="status" className="mt-6">Siparişlerin yükleniyor…</p></div>}><OrdersView /></Suspense>;
}

import type { Metadata } from "next";
import { Suspense } from "react";
import { ProductEditor } from "@/components/admin/ProductEditor";

export const metadata: Metadata = { title: "Ürün" };
type Props = { params: Promise<{ id: string }> };
async function Editor({ params }: Props) {
  const { id } = await params;
  return <ProductEditor key={id} id={id} />;
}
export default function AdminProductPage({ params }: Props) {
  return <Suspense fallback={<p role="status" className="py-6 text-fg-muted">Ürün yükleniyor…</p>}><Editor params={params} /></Suspense>;
}

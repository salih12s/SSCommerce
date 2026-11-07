import type { Metadata } from "next";
import { Suspense } from "react";
import { AdminShell } from "@/components/admin/AdminShell";

export const metadata: Metadata = {
  title: { default: "Yönetim", template: "%s | SSC Yönetim" },
  robots: { index: false, follow: false },
};

// Vitrin çerçevesi (üst bölge, alt bilgi, mobil alt gezinti) burada çizilmez. Kabuk oturumu ve rolü tarayıcıda okur; HTML'de kişisel veri yoktur.
export default function AdminLayout({ children }: LayoutProps<"/admin">) {
  return (
    <Suspense fallback={<p role="status" className="px-4 py-10 text-fg-muted">Yönetim yükleniyor…</p>}>
      <AdminShell>{children}</AdminShell>
    </Suspense>
  );
}

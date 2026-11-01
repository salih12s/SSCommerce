import type { Metadata } from "next";
import { Suspense } from "react";
import { AuthForm } from "@/components/AuthForm";
import { safeNext } from "@/lib/safe-next";
export const metadata: Metadata = { title: "Kayıt ol" };
// `next` yalnızca aynı site içi göreli yol olabilir (safeNext); aksi hâlde /hesabim'e düşer.
async function Form({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const raw = (await searchParams).next;
  return <AuthForm mode="register" next={safeNext(Array.isArray(raw) ? raw[0] : raw)} />;
}
export default function RegisterPage({ searchParams }: PageProps<"/kayit">) {
  return <div className="mx-auto max-w-lg px-4 py-12 sm:px-6"><h1 className="font-display text-title font-extrabold">Hesap oluştur</h1><p className="mt-2 text-fg-muted">Kayıt sonrası oturumun açılır. E-posta doğrulaması henüz sunulmuyor.</p><Suspense fallback={<p role="status" className="mt-7 min-h-72">Form yükleniyor…</p>}><Form searchParams={searchParams} /></Suspense></div>;
}

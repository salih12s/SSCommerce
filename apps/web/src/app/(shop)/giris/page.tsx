import type { Metadata } from "next";
import { Suspense } from "react";
import { AuthForm } from "@/features/auth/components/AuthForm";
import { safeNext } from "@/lib/safe-next";
export const metadata: Metadata = { title: "Giriş yap" };
// `next` yalnızca aynı site içi göreli yol olabilir (safeNext); aksi hâlde /hesabim'e düşer.
async function Form({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const raw = (await searchParams).next;
  return <AuthForm mode="login" next={safeNext(Array.isArray(raw) ? raw[0] : raw)} />;
}
export default function LoginPage({ searchParams }: PageProps<"/giris">) {
  return <div className="mx-auto max-w-lg px-4 py-12 sm:px-6"><h1 className="font-display text-title font-extrabold">Giriş yap</h1><p className="mt-2 text-fg-muted">SSCommerce demo hesabına giriş yap.</p><Suspense fallback={<p role="status" className="mt-7 min-h-72">Form yükleniyor…</p>}><Form searchParams={searchParams} /></Suspense></div>;
}

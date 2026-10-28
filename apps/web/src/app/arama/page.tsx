import type { Metadata } from "next";
import Link from "next/link";
import { Suspense } from "react";

export const metadata: Metadata = { title: "Arama" };

async function Term({ searchParams }: { searchParams: PageProps<"/arama">["searchParams"] }) {
  const { q } = await searchParams;
  const raw = Array.isArray(q) ? q[0] : q;
  const term = (raw ?? "").trim().slice(0, 100);
  return (
    <>
      <p className="mt-4 text-lg">
        {term ? (
          <>
            Aranan: <span className="font-mono font-medium break-all">{term}</span>
          </>
        ) : (
          "Henüz bir arama terimi girilmedi."
        )}
      </p>
      <p className="mt-4 max-w-xl rounded-md border border-dashed border-fg-muted bg-card px-4 py-3 text-fg-muted">
        Arama yakında. Katalog Faz 3&apos;te açıldığında sonuçlar burada listelenecek.
      </p>
    </>
  );
}

export default function SearchPage(props: PageProps<"/arama">) {
  return (
    <div className="mx-auto max-w-6xl px-4 py-section sm:px-6">
      <h1 className="font-display text-title font-extrabold">Arama</h1>
      <Suspense fallback={<p className="mt-4 text-fg-muted">Yükleniyor…</p>}>
        <Term searchParams={props.searchParams} />
      </Suspense>
      <p className="mt-10">
        <Link href="/" className="font-medium text-accent-deep underline underline-offset-4">
          Ana sayfaya dön
        </Link>
      </p>
    </div>
  );
}

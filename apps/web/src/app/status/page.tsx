import { Suspense } from "react";
import { connection } from "next/server";
import { readHealth, type HealthResult } from "@/lib/backend";

export const metadata = { title: "Sistem durumu" };

// Not: cacheComponents açıkken `dynamic = 'force-dynamic'` kaldırılmıştır; istek anında
// çalışmak için connection() + Suspense kullanılır (bkz. Next docs: migrating-to-cache-components).

function describe(result: HealthResult): { label: string; detail: string } {
  switch (result.state) {
    case "up":
      return { label: "Çalışıyor", detail: `HTTP ${result.httpStatus}${result.body ? ` — ${result.body}` : ""}` };
    case "down":
      return { label: "Hazır değil", detail: `HTTP ${result.httpStatus}${result.body ? ` — ${result.body}` : ""}` };
    case "unreachable":
      return { label: "Backend'e ulaşılamadı", detail: result.reason };
  }
}

function Row({ title, result }: { title: string; result: HealthResult }) {
  const { label, detail } = describe(result);
  return (
    <li className="rounded-md border border-line bg-card p-4">
      <p className="text-sm text-fg-muted">{title}</p>
      <p className="mt-1 text-lg font-display font-bold" data-state={result.state}>
        {label}
      </p>
      <p className="mt-1 break-words font-mono text-sm text-fg">{detail}</p>
    </li>
  );
}

async function StatusList() {
  await connection();
  const [live, ready] = await Promise.all([readHealth("/health/live"), readHealth("/health/ready")]);
  return (
    <ul className="mt-6 flex flex-col gap-4">
      <Row title="Süreç (/health/live)" result={live} />
      <Row title="Veritabanı bağlantısı (/health/ready)" result={ready} />
    </ul>
  );
}

export default function StatusPage() {
  return (
    <div className="mx-auto w-full max-w-xl px-4 py-section sm:px-6">
      <h1 className="font-display text-title font-extrabold">Sistem durumu</h1>
      <Suspense fallback={<p className="mt-6 text-fg-muted">Kontrol ediliyor…</p>}>
        <StatusList />
      </Suspense>
    </div>
  );
}

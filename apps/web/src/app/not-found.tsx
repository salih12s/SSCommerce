import Link from "next/link";
import { StoreShell } from "@/features/shell/components/StoreShell";
import { btnPrimary } from "@/components/ui/styles";

export default function NotFound() {
  return (
    <StoreShell>
      <div className="mx-auto max-w-xl px-4 py-section text-center sm:px-6">
        <h1 className="font-display text-title font-extrabold">Sayfa bulunamadı</h1>
        <p className="mt-3 text-fg-muted">Aradığın adres yok ya da kaldırılmış olabilir.</p>
        <Link href="/" className={`${btnPrimary} mt-6`}>Ana sayfaya dön</Link>
      </div>
    </StoreShell>
  );
}

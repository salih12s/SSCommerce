"use client";

import { useEffect, useRef } from "react";
import { btnPrimary, btnSecondary } from "./ui";

// Yerel <dialog>: showModal odağı içeri alır, Esc kapatır, arka plan etkisizleşir. Kapanınca odak çağıran tarafta yönetilir.
export function ConfirmDialog({ open, title, description, confirmLabel, busy, onConfirm, onCancel }: {
  open: boolean; title: string; description: string; confirmLabel: string; busy: boolean; onConfirm: () => void; onCancel: () => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
  }, [open]);
  return (
    <dialog ref={ref} aria-labelledby="onay-baslik" aria-describedby="onay-aciklama" onCancel={(event) => { event.preventDefault(); if (!busy) onCancel(); }} className="m-auto w-[min(26rem,calc(100vw-2rem))] rounded-xl border border-line bg-card p-6 text-fg shadow-lift backdrop:bg-ink/50">
      <h2 id="onay-baslik" className="font-display text-xl font-bold">{title}</h2>
      <p id="onay-aciklama" className="mt-2 text-fg-muted">{description}</p>
      <div className="mt-5 flex flex-wrap gap-3">
        <button type="button" onClick={onCancel} disabled={busy} className={btnSecondary}>Vazgeç</button>
        <button type="button" onClick={onConfirm} disabled={busy} className={btnPrimary}>{busy ? "Siliniyor…" : confirmLabel}</button>
      </div>
    </dialog>
  );
}

"use client";

import type { ReactNode } from "react";
import type { ApiFailure, ApiResult } from "@/lib/api";
import type { Remote } from "@/lib/admin/use-remote";
import { btnPrimary, btnSecondary } from "../ui";

// Yönetim ekranlarının ortak parçaları. Dil: operasyon defteri. Mono "künye" etiketleri, kesik çizgili ayraçlar, metin taşıyan durum çipleri (renk tek başına anlam taşımaz).
export const fieldBase = "w-full rounded-md border border-line bg-paper px-3 text-base text-fg disabled:opacity-60 aria-[invalid=true]:border-red-700";
export const inputClass = `${fieldBase} h-11`;
export const textareaClass = `${fieldBase} py-2 leading-snug`;
export const btnSmall = "inline-flex min-h-11 items-center justify-center gap-1.5 rounded-md border border-line bg-card px-3 py-1.5 text-sm font-semibold text-fg transition-transform duration-150 ease-out hover:bg-paper-2 active:scale-[0.97] disabled:cursor-not-allowed disabled:opacity-60 disabled:active:scale-100 md:min-h-9 motion-reduce:transition-none";
export const btnDanger = "inline-flex min-h-11 items-center justify-center gap-2 rounded-md bg-red-800 px-5 py-2.5 font-semibold text-white transition-transform duration-150 ease-out hover:bg-red-900 active:scale-[0.97] disabled:cursor-not-allowed disabled:opacity-60 disabled:active:scale-100 motion-reduce:transition-none";
export const sheet = "rounded-lg border border-line bg-card";

export function PageHead({ title, lead, actions }: { title: string; lead?: ReactNode; actions?: ReactNode }) {
  return <div className="mb-5 flex flex-wrap items-end justify-between gap-x-6 gap-y-3 border-b-2 border-dashed border-line pb-4">
    <div className="min-w-0">
      <h1 className="font-display text-3xl font-extrabold tracking-tight md:text-4xl">{title}</h1>
      {lead && <p className="mt-1 max-w-prose text-fg-muted">{lead}</p>}
    </div>
    {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
  </div>;
}

export function Section({ title, id, note, actions, children }: { title: string; id: string; note?: ReactNode; actions?: ReactNode; children: ReactNode }) {
  return <section aria-labelledby={id} className="mt-8">
    <div className="mb-3 flex flex-wrap items-end justify-between gap-x-4 gap-y-1">
      <div className="min-w-0"><h2 id={id} className="font-display text-xl font-bold">{title}</h2>{note && <p className="text-sm text-fg-muted">{note}</p>}</div>
      {actions}
    </div>
    {children}
  </section>;
}

type Tone = "neutral" | "ok" | "warn" | "danger" | "accent";
const chipTone: Record<Tone, string> = {
  neutral: "border-line bg-paper text-fg-muted",
  ok: "border-emerald-700/40 bg-emerald-50 text-emerald-900",
  warn: "border-amber-500/60 bg-amber-50 text-amber-950",
  danger: "border-red-700/40 bg-red-50 text-red-900",
  accent: "border-accent-deep/50 bg-orange-50 text-accent-deep",
};
export function Chip({ tone = "neutral", children }: { tone?: Tone; children: ReactNode }) {
  return <span className={`inline-flex items-center whitespace-nowrap rounded-full border px-2 py-0.5 text-xs font-semibold ${chipTone[tone]}`}>{children}</span>;
}
export const ActiveChip = ({ active }: { active: boolean }) => <Chip tone={active ? "ok" : "neutral"}>{active ? "Satışta" : "Pasif"}</Chip>;

/** SKU, slug, kimlik gibi teknik kodlar: ürün künyesinin devamı (mono, kesik çizgili çerçeve). */
export function Kunye({ children, title }: { children: ReactNode; title?: string }) {
  return <span title={title} className="inline-block max-w-full whitespace-nowrap rounded border border-dashed border-ink/35 bg-paper px-1.5 py-0.5 font-mono text-xs leading-snug">{children}</span>;
}

export function Notice({ tone = "neutral", title, children, live = "status", action }: { tone?: "neutral" | "ok" | "warn" | "danger"; title?: string; children?: ReactNode; live?: "status" | "alert" | "off"; action?: ReactNode }) {
  const toneClass = { neutral: "border-line bg-paper-2", ok: "border-emerald-700/40 bg-emerald-50 text-emerald-950", warn: "border-amber-400 bg-amber-50 text-amber-950", danger: "border-red-400 bg-red-50 text-red-950" }[tone];
  return <div role={live === "off" ? undefined : live} className={`rounded-md border p-3 text-sm ${toneClass}`}>
    {title && <p className="font-semibold">{title}</p>}
    {children && <div className={title ? "mt-1" : ""}>{children}</div>}
    {action && <div className="mt-2 flex flex-wrap gap-2">{action}</div>}
  </div>;
}

export function Loading({ label = "Yükleniyor…" }: { label?: string }) {
  return <p role="status" className="py-6 text-fg-muted">{label}</p>;
}

export function Empty({ title, children }: { title: string; children?: ReactNode }) {
  return <div className="rounded-lg border-2 border-dashed border-line px-4 py-8 text-center"><p className="font-semibold">{title}</p>{children && <div className="mt-1 text-sm text-fg-muted">{children}</div>}</div>;
}

export function FailureBox({ failure, onRetry }: { failure: ApiFailure; onRetry?: () => void }) {
  const text = failure.status === 403 ? "Bu işlem için yetkin yok." : failure.status === 401 ? "Oturumun sona erdi. Yeniden giriş yapman gerekiyor." : failure.message;
  return <Notice tone="danger" live="alert" title="İşlem tamamlanamadı" action={onRetry && failure.status !== 403 && failure.status !== 401 ? <button type="button" onClick={onRetry} className={btnSmall}>Tekrar dene</button> : undefined}>{text}</Notice>;
}

/** Okuma sonucunu çizer: ilk yükleme, hata, veri. Veri varken yeniden yükleme sırasında eski içerik soluk kalır (aria-busy). */
export function Loaded<T>({ remote, label, children }: { remote: Remote<T>; label?: string; children: (data: T) => ReactNode }) {
  const { result, loading, reload } = remote;
  if (result === null) return <Loading label={label} />;
  if (!result.ok) return <FailureBox failure={result} onRetry={reload} />;
  return <div aria-busy={loading} className={loading ? "opacity-60 transition-opacity duration-150" : "transition-opacity duration-150"}>{children(result.data)}</div>;
}

/** Sürüm çakışması: sessizce üzerine yazılmaz; kullanıcı güncel kaydı yükler. */
export function ConflictNotice({ onReload }: { onReload: () => void }) {
  return <Notice tone="warn" live="alert" title="Bu kayıt sizden sonra değişti" action={<button type="button" onClick={onReload} className={btnSmall}>Güncel kaydı yükle</button>}>
    Başka biri bu kaydı sen düzenlerken güncelledi. Yaptığın değişiklik kaydedilmedi; güncel kaydı yükleyip değişikliği yeniden yap.
  </Notice>;
}

/** İlk alan hatası (ValidationProblem `errors` anahtarları alan adlarıdır; büyük/küçük harf farkı tolere edilir). */
export function fieldError(failure: ApiFailure | null | undefined, name: string): string | undefined {
  if (!failure) return undefined;
  const key = Object.keys(failure.errors).find((k) => k.toLowerCase() === name.toLowerCase());
  return key ? failure.errors[key]?.[0] : undefined;
}

export function Field({ label, id, hint, error, children, className = "" }: { label: string; id: string; hint?: ReactNode; error?: string; children: ReactNode; className?: string }) {
  return <div className={className}>
    <label htmlFor={id} className="mb-1 block text-sm font-semibold">{label}</label>
    {children}
    {hint && !error && <p id={`${id}-hint`} className="mt-1 text-xs text-fg-muted">{hint}</p>}
    {error && <p id={`${id}-error`} className="mt-1 text-sm text-red-800">{error}</p>}
  </div>;
}
export const describedBy = (id: string, hint: boolean, error: boolean) => (error ? `${id}-error` : hint ? `${id}-hint` : undefined);

export function CheckField({ id, label, checked, onChange, disabled, hint }: { id: string; label: string; checked: boolean; onChange: (value: boolean) => void; disabled?: boolean; hint?: string }) {
  return <div className="flex min-h-11 items-start gap-3">
    <input id={id} type="checkbox" checked={checked} disabled={disabled} onChange={(event) => onChange(event.target.checked)} aria-describedby={hint ? `${id}-hint` : undefined} className="mt-1.5 size-5 shrink-0 accent-[var(--color-ink)]" />
    <label htmlFor={id} className="pt-1"><span className="font-semibold">{label}</span>{hint && <span id={`${id}-hint`} className="block text-xs text-fg-muted">{hint}</span>}</label>
  </div>;
}

export type Column<T> = { key: string; header: string; cell: (row: T) => ReactNode; align?: "right"; primary?: boolean; className?: string };

/** Yoğun tablo. Dar ekranda satırlar "fiş" gibi üst üste dizilir (başlık sütunu etiket olur); yatay sayfa kaydırması oluşmaz. */
export function DataTable<T>({ caption, rows, columns, rowKey, rowClass }: { caption: string; rows: T[]; columns: Column<T>[]; rowKey: (row: T) => string; rowClass?: (row: T) => string }) {
  return <div className={`${sheet} overflow-x-auto`}>
    <table role="table" className="w-full border-collapse text-left text-sm max-md:block">
      <caption className="sr-only">{caption}</caption>
      <thead role="rowgroup" className="max-md:sr-only">
        <tr role="row" className="border-b border-line bg-paper-2">
          {columns.map((c) => <th key={c.key} role="columnheader" scope="col" className={`px-3 py-2 font-semibold text-fg-muted ${c.align === "right" ? "text-right" : ""} ${c.className ?? ""}`}>{c.header}</th>)}
        </tr>
      </thead>
      <tbody role="rowgroup" className="max-md:block">
        {rows.map((row) => <tr key={rowKey(row)} role="row" className={`border-b border-line/70 last:border-b-0 max-md:block max-md:border-b-2 max-md:border-dashed max-md:px-3 max-md:py-2.5 ${rowClass?.(row) ?? ""}`}>
          {columns.map((c) => <td key={c.key} role="cell" data-label={c.header}
            className={`px-3 py-2 align-top ${c.align === "right" ? "text-right tabular-nums" : ""} ${c.className ?? ""} max-md:flex max-md:items-baseline max-md:justify-between max-md:gap-4 max-md:px-0 max-md:py-1 max-md:text-right ${c.primary ? "max-md:justify-start max-md:text-left" : "max-md:before:shrink-0 max-md:before:text-left max-md:before:font-normal max-md:before:text-fg-muted max-md:before:content-[attr(data-label)]"}`}>
            {c.cell(row)}
          </td>)}
        </tr>)}
      </tbody>
    </table>
  </div>;
}

export function Pager({ page, totalPages, totalCount, onPage, busy }: { page: number; totalPages: number; totalCount: number; onPage: (page: number) => void; busy?: boolean }) {
  if (totalPages <= 1) return <p className="mt-2 text-sm text-fg-muted tabular-nums">{totalCount} kayıt</p>;
  return <nav aria-label="Sayfalar" className="mt-3 flex flex-wrap items-center justify-between gap-3">
    <button type="button" className={btnSmall} disabled={busy || page <= 1} onClick={() => onPage(page - 1)}>Önceki</button>
    <p className="text-sm text-fg-muted tabular-nums" aria-live="polite">Sayfa {page} / {totalPages} · {totalCount} kayıt</p>
    <button type="button" className={btnSmall} disabled={busy || page >= totalPages} onClick={() => onPage(page + 1)}>Sonraki</button>
  </nav>;
}

export function FormActions({ children }: { children: ReactNode }) {
  return <div className="mt-4 flex flex-wrap items-center gap-3">{children}</div>;
}
export const SubmitButton = ({ busy, children, busyLabel = "Kaydediliyor…", disabled }: { busy: boolean; children: ReactNode; busyLabel?: string; disabled?: boolean }) =>
  <button type="submit" disabled={busy || disabled} className={btnPrimary}>{busy ? busyLabel : children}</button>;
export const CancelButton = ({ onClick, disabled }: { onClick: () => void; disabled?: boolean }) => <button type="button" onClick={onClick} disabled={disabled} className={btnSecondary}>Vazgeç</button>;

/** Okuma sonucu sayfa nesnesi mi? Değilse (sözleşme ihlali) boş sayfa döner; ekran çökmez. */
export function pagedOf<T>(result: ApiResult<{ items?: T[]; page?: number; pageSize?: number; totalCount?: number; totalPages?: number }>) {
  if (!result.ok) return null;
  const d = result.data;
  return { items: Array.isArray(d.items) ? d.items : [], page: d.page ?? 1, pageSize: d.pageSize ?? 0, totalCount: d.totalCount ?? 0, totalPages: d.totalPages ?? 1 };
}

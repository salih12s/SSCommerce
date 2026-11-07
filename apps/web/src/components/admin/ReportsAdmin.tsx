"use client";

import Link from "next/link";
import { useState, type FormEvent } from "react";
import { aGet, qs } from "@/lib/admin/client";
import { actionLabel, formatDateTime, targetLabel } from "@/lib/admin/format";
import type { AuditRow, LowStockRow, Paged } from "@/lib/admin/types";
import { useRemote } from "@/lib/admin/use-remote";
import { isOrderId } from "@/lib/orders";
import { btnSmall, DataTable, Empty, Field, inputClass, Kunye, Loaded, PageHead, Pager, Section, pagedOf, type Column } from "./kit";

const TARGETS = ["category", "product", "variant", "warehouse", "user", "order"] as const;
const PAGE_SIZE = 20;

export function ReportsAdmin() {
  return <>
    <PageHead title="Raporlar" lead="Düşük stoklu varyantlar ve yönetim işlemlerinin değiştirilemez denetim kaydı." />
    <LowStock />
    <Audit />
  </>;
}

function LowStock() {
  const [threshold, setThreshold] = useState(5);
  const [text, setText] = useState("5");
  const [error, setError] = useState("");
  const [page, setPage] = useState(1);
  const query = qs({ threshold, page, pageSize: PAGE_SIZE });
  const remote = useRemote(() => aGet<Paged<LowStockRow>>(`/admin/reports/low-stock${query}`), query);
  function apply(event: FormEvent) {
    event.preventDefault();
    const n = Number(text);
    if (!Number.isInteger(n) || n < 1 || n > 1000) return setError("Eşik 1 ile 1000 arasında bir tam sayı olmalı.");
    setError(""); setThreshold(n); setPage(1);
  }
  const columns: Column<LowStockRow>[] = [
    { key: "sku", header: "SKU", primary: true, cell: (r) => <Kunye>{r.sku}</Kunye> },
    { key: "p", header: "Ürün", cell: (r) => <span className="break-words"><span className="font-semibold">{r.productName}</span><span className="block text-fg-muted">{r.displayName}</span></span> },
    { key: "on", header: "Toplam stok", align: "right", cell: (r) => <span className={`font-mono font-medium ${r.onHandTotal === 0 ? "text-red-800" : ""}`}>{r.onHandTotal}{r.onHandTotal === 0 ? " (tükendi)" : ""}</span> },
    { key: "act", header: "İşlem", cell: (r) => <Link aria-label={`Stok gir: ${r.sku}`} href={`/admin/stok?q=${encodeURIComponent(r.sku)}`} className={btnSmall}>Stok gir</Link> },
  ];
  return <Section id="dusuk-stok" title="Düşük stok" note="Satışa açık varyantlar; en az stoklu önce. Eşik dahil olmak üzere o kadar ya da daha az kalanlar listelenir.">
    <form onSubmit={apply} className="mb-3 flex flex-wrap items-end gap-3" noValidate>
      <Field label="Stok eşiği (adet)" id="rapor-esik" error={error}><input id="rapor-esik" inputMode="numeric" value={text} onChange={(e) => setText(e.target.value)} aria-invalid={!!error} aria-describedby={error ? "rapor-esik-error" : undefined} className={`${inputClass} w-32`} /></Field>
      <button type="submit" className={btnSmall}>Uygula</button>
    </form>
    <Loaded remote={remote} label="Rapor hazırlanıyor…">{(raw) => {
      const data = pagedOf<LowStockRow>({ ok: true, data: raw });
      if (!data || data.items.length === 0) return <Empty title={`${threshold} adet ve altında stoklu varyant yok`}>Eşiği yükselterek daha fazla varyanta bak.</Empty>;
      return <>
        <p className="mb-2 text-sm text-fg-muted">Eşik: en çok {threshold} adet</p>
        <DataTable caption="Düşük stoklu varyantlar" rows={data.items} columns={columns} rowKey={(r) => r.variantId} />
        <Pager page={data.page} totalPages={data.totalPages} totalCount={data.totalCount} onPage={setPage} busy={remote.loading} />
      </>;
    }}</Loaded>
  </Section>;
}

function Audit() {
  const [targetType, setTargetType] = useState("");
  const [targetId, setTargetId] = useState("");
  const [applied, setApplied] = useState({ targetType: "", targetId: "" });
  const [error, setError] = useState("");
  const [page, setPage] = useState(1);
  const query = qs({ targetType: applied.targetType, targetId: applied.targetId, page, pageSize: PAGE_SIZE });
  const remote = useRemote(() => aGet<Paged<AuditRow>>(`/admin/audit${query}`), query);
  function apply(event: FormEvent) {
    event.preventDefault();
    const id = targetId.trim().toLowerCase();
    if (id !== "" && !isOrderId(id)) return setError("Hedef kimliği geçerli bir UUID olmalı.");
    setError(""); setApplied({ targetType, targetId: id }); setPage(1);
  }
  const columns: Column<AuditRow>[] = [
    { key: "t", header: "Zaman", primary: true, cell: (r) => <time dateTime={r.occurredAt} className="whitespace-nowrap">{formatDateTime(r.occurredAt)}</time> },
    { key: "a", header: "Yapan", cell: (r) => <span className="break-all">{r.actorEmail ?? "—"}</span> },
    { key: "act", header: "İşlem", cell: (r) => <span><span className="font-semibold">{actionLabel[r.action] ?? r.action}</span><span className="block"><Kunye>{r.action}</Kunye></span></span> },
    { key: "target", header: "Hedef", cell: (r) => <span>{targetLabel[r.targetType] ?? r.targetType}<span className="block"><Kunye>{r.targetId}</Kunye></span></span> },
    { key: "ch", header: "Değişiklik", cell: (r) => <Changes value={r.changes} /> },
  ];
  return <Section id="denetim" title="Denetim kaydı" note="En yeni önce. Kayıtlar eklenir, değiştirilemez ya da silinemez. Parola, e-posta, telefon ve adres kayıtta tutulmaz.">
    <form onSubmit={apply} className="mb-3 flex flex-wrap items-end gap-3" noValidate>
      <Field label="Hedef türü" id="denetim-tur"><select id="denetim-tur" value={targetType} onChange={(e) => setTargetType(e.target.value)} className={inputClass}><option value="">Tümü</option>{TARGETS.map((t) => <option key={t} value={t}>{targetLabel[t]}</option>)}</select></Field>
      <Field label="Hedef kimliği" id="denetim-id" error={error} className="min-w-0 flex-1 basis-64"><input id="denetim-id" value={targetId} onChange={(e) => setTargetId(e.target.value)} autoComplete="off" spellCheck={false} aria-invalid={!!error} aria-describedby={error ? "denetim-id-error" : undefined} className={`${inputClass} font-mono`} /></Field>
      <button type="submit" className={btnSmall}>Süz</button>
    </form>
    <Loaded remote={remote} label="Denetim kaydı yükleniyor…">{(raw) => {
      const data = pagedOf<AuditRow>({ ok: true, data: raw });
      if (!data || data.items.length === 0) return <Empty title="Kayıt yok">Bu ölçüte uyan denetim kaydı bulunmuyor.</Empty>;
      return <>
        <DataTable caption="Denetim kaydı" rows={data.items} columns={columns} rowKey={(r) => r.id} />
        <Pager page={data.page} totalPages={data.totalPages} totalCount={data.totalCount} onPage={setPage} busy={remote.loading} />
      </>;
    }}</Loaded>
  </Section>;
}

/** Değişiklik nesnesi düz metin olarak gösterilir (React metni kaçırır; HTML olarak yazılmaz). */
function Changes({ value }: { value: Record<string, unknown> }) {
  const text = JSON.stringify(value, null, 1);
  if (!value || Object.keys(value).length === 0) return <span className="text-fg-muted">—</span>;
  return <details><summary className="cursor-pointer text-sm font-semibold text-accent-deep">Göster</summary><pre className="mt-1 max-w-xs overflow-x-auto whitespace-pre-wrap break-all rounded border border-line bg-paper p-2 font-mono text-xs">{text}</pre></details>;
}

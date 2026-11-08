"use client";

import { useState, type FormEvent } from "react";
import { aGet, aSend, enc } from "@/features/admin/client";
import { formatDateTime } from "@/features/admin/format";
import { listOf, type Warehouse } from "@/features/admin/types";
import { useRemote } from "@/features/admin/use-remote";
import type { ApiFailure } from "@/lib/api";
import { btnSecondary } from "@/components/ui/styles";
import { btnSmall, CancelButton, CheckField, DataTable, describedBy, Empty, FailureBox, Field, fieldError, FormActions, inputClass, Kunye, Loaded, Notice, PageHead, Section, SubmitButton, Chip, type Column } from "./kit";

type Editing = { kind: "new" } | { kind: "edit"; id: string } | null;

export function WarehousesAdmin() {
  const remote = useRemote(() => aGet<Warehouse[] | { items: Warehouse[] }>("/admin/warehouses"), "depolar");
  const [editing, setEditing] = useState<Editing>(null);
  const [saved, setSaved] = useState("");
  return <>
    <PageHead title="Depolar" lead="Depo kodu oluşturulduktan sonra değişmez. Pasif depo stok girişi almaz ve satışta sayılmaz." actions={<button type="button" onClick={() => { setSaved(""); setEditing({ kind: "new" }); }} className={btnSecondary}>Yeni depo</button>} />
    {saved && <div className="mb-4"><Notice tone="ok">{saved}</Notice></div>}
    <Loaded remote={remote} label="Depolar yükleniyor…">{(raw) => {
      const rows = listOf(raw);
      const current = editing?.kind === "edit" ? rows.find((w) => w.id === editing.id) ?? null : null;
      const done = (message: string) => { setSaved(message); setEditing(null); remote.reload(); };
      const columns: Column<Warehouse>[] = [
        { key: "code", header: "Kod", primary: true, cell: (w) => <Kunye>{w.code}</Kunye> },
        { key: "name", header: "Ad", cell: (w) => <span className="font-semibold break-words">{w.name}</span> },
        { key: "st", header: "Durum", cell: (w) => <Chip tone={w.isActive ? "ok" : "neutral"}>{w.isActive ? "Aktif" : "Pasif"}</Chip> },
        { key: "upd", header: "Güncellendi", cell: (w) => <time dateTime={w.updatedAt} className="whitespace-nowrap text-fg-muted">{formatDateTime(w.updatedAt)}</time> },
        { key: "act", header: "İşlem", cell: (w) => <button aria-label={`Düzenle: ${w.name}`} type="button" className={btnSmall} onClick={() => { setSaved(""); setEditing({ kind: "edit", id: w.id }); }}>Düzenle</button> },
      ];
      return <>
        {editing?.kind === "new" && <Section id="yeni-depo" title="Yeni depo"><WarehouseForm warehouse={null} onDone={done} onCancel={() => setEditing(null)} /></Section>}
        {editing?.kind === "edit" && current && <Section id="duzenle-depo" title={`Düzenle: ${current.name}`}><WarehouseForm key={`${current.id}-${current.updatedAt}`} warehouse={current} onDone={done} onCancel={() => setEditing(null)} /></Section>}
        <div className="mt-6">{rows.length === 0 ? <Empty title="Henüz depo yok">Stok girişi yapabilmek için bir depo oluştur.</Empty> : <DataTable caption="Depolar" rows={rows} columns={columns} rowKey={(w) => w.id} />}</div>
      </>;
    }}</Loaded>
  </>;
}

function WarehouseForm({ warehouse, onDone, onCancel }: { warehouse: Warehouse | null; onDone: (message: string) => void; onCancel: () => void }) {
  const [code, setCode] = useState(warehouse?.code ?? "");
  const [name, setName] = useState(warehouse?.name ?? "");
  const [isActive, setActive] = useState(warehouse?.isActive ?? true);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiFailure | null>(null);
  const [localError, setLocalError] = useState("");
  async function submit(event: FormEvent) {
    event.preventDefault();
    setFailure(null); setLocalError("");
    if (name.trim() === "") return setLocalError("Depo adı gerekli.");
    if (!warehouse && code.trim() === "") return setLocalError("Depo kodu gerekli.");
    setBusy(true);
    // Depo uçlarında sürüm alanı yoktur (Faz 5 sözleşmesi aynen kalır); yalnız değişen alanlar gönderilir.
    const result = warehouse
      ? await aSend<Warehouse>("PATCH", `/admin/warehouses/${enc(warehouse.id)}`, { ...(name.trim() !== warehouse.name ? { name: name.trim() } : {}), ...(isActive !== warehouse.isActive ? { isActive } : {}) })
      : await aSend<Warehouse>("POST", "/admin/warehouses", { code: code.trim(), name: name.trim() });
    setBusy(false);
    if (result.ok) onDone(warehouse ? `“${name.trim()}” kaydedildi.` : `“${name.trim()}” oluşturuldu.`); else setFailure(result);
  }
  const codeErr = fieldError(failure, "code") ?? (failure?.code === "warehouse_code_taken" ? failure.message : undefined);
  const nameErr = fieldError(failure, "name");
  const general = failure && !codeErr && !nameErr && Object.keys(failure.errors).length === 0 ? failure : null;
  const unchanged = warehouse !== null && name.trim() === warehouse.name && isActive === warehouse.isActive;
  return <form onSubmit={(e) => void submit(e)} noValidate aria-busy={busy} className="max-w-xl rounded-lg border border-line bg-card p-4 sm:p-5">
    <div className="grid gap-4 sm:grid-cols-2">
      <Field label="Kod" id="d-kod" error={codeErr} hint={warehouse ? "Oluşturulduktan sonra değişmez." : "Örn. IST-01 (en çok 32 karakter)."}>
        <input id="d-kod" value={code} maxLength={32} disabled={!!warehouse} onChange={(e) => setCode(e.target.value)} aria-invalid={!!codeErr} aria-describedby={describedBy("d-kod", true, !!codeErr)} className={`${inputClass} font-mono`} />
      </Field>
      <Field label="Ad" id="d-ad" error={nameErr}>
        <input id="d-ad" value={name} maxLength={100} onChange={(e) => setName(e.target.value)} aria-invalid={!!nameErr} aria-describedby={describedBy("d-ad", false, !!nameErr)} className={inputClass} />
      </Field>
    </div>
    {warehouse && <div className="mt-3"><CheckField id="d-aktif" label="Aktif" checked={isActive} onChange={setActive} hint="Pasif depoya stok girilemez; mevcut bakiyesi satışta sayılmaz." /></div>}
    {localError && <p role="alert" className="mt-3 text-sm text-red-800">{localError}</p>}
    {general && <div className="mt-4"><FailureBox failure={general} /></div>}
    <FormActions><SubmitButton busy={busy} disabled={unchanged}>{warehouse ? "Kaydet" : "Depo oluştur"}</SubmitButton><CancelButton onClick={onCancel} disabled={busy} /></FormActions>
  </form>;
}

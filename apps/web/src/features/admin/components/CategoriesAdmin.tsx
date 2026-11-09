"use client";

import { useMemo, useState, type FormEvent } from "react";
import { aGet, aSend, enc } from "@/features/admin/client";
import { formatDateTime, slugify } from "@/features/admin/format";
import { listOf, type Category } from "@/features/admin/types";
import { useRemote } from "@/features/admin/use-remote";
import type { ApiFailure } from "@/lib/api";
import { btnSecondary } from "@/components/ui/styles";
import { ActiveChip, btnSmall, CheckField, ConflictNotice, DataTable, Empty, Field, FailureBox, FormActions, Kunye, Loaded, Notice, PageHead, Section, SubmitButton, CancelButton, fieldError, inputClass, describedBy, type Column } from "./kit";

export type Node = { category: Category; depth: number };

/** Düz listeyi ağaç sırasına çevirir (üst önce, kardeşler sunucu sırasıyla). Üstü listede olmayan kayıt kök sayılır. */
export function flatten(all: Category[]): Node[] {
  const ids = new Set(all.map((c) => c.id));
  const children = new Map<string | null, Category[]>();
  for (const c of all) {
    const key = c.parentId && ids.has(c.parentId) ? c.parentId : null;
    children.set(key, [...(children.get(key) ?? []), c]);
  }
  const out: Node[] = [];
  const seen = new Set<string>();
  const walk = (parent: string | null, depth: number) => {
    for (const c of children.get(parent) ?? []) {
      if (seen.has(c.id)) continue; // sözleşme ihlali (döngü) olsa bile sonsuz döngü yok
      seen.add(c.id); out.push({ category: c, depth }); walk(c.id, depth + 1);
    }
  };
  walk(null, 0);
  return out;
}
function descendantsOf(all: Category[], id: string): Set<string> {
  const result = new Set<string>([id]);
  let grew = true;
  while (grew) { grew = false; for (const c of all) if (c.parentId && result.has(c.parentId) && !result.has(c.id)) { result.add(c.id); grew = true; } }
  return result;
}

type Editing = { kind: "new" } | { kind: "edit"; id: string } | null;

export function CategoriesAdmin() {
  const remote = useRemote(() => aGet<Category[] | { items: Category[] }>("/admin/categories"), "kategoriler");
  const [editing, setEditing] = useState<Editing>(null);
  const [saved, setSaved] = useState("");
  return <>
    <PageHead title="Kategoriler" lead="Kategori ağacı en çok 5 düzey olabilir. Adres (slug) oluşturulduktan sonra değişmez; yanlış kategori silinmez, pasife alınır."
      actions={<button type="button" onClick={() => { setSaved(""); setEditing({ kind: "new" }); }} className={btnSecondary}>Yeni kategori</button>} />
    {saved && <div className="mb-4"><Notice tone="ok">{saved}</Notice></div>}
    <Loaded remote={remote} label="Kategoriler yükleniyor…">{(raw) => {
      const all = listOf(raw);
      return <Body all={all} editing={editing} setEditing={setEditing} onSaved={(message) => { setSaved(message); setEditing(null); remote.reload(); }} reload={remote.reload} />;
    }}</Loaded>
  </>;
}

function Body({ all, editing, setEditing, onSaved, reload }: { all: Category[]; editing: Editing; setEditing: (e: Editing) => void; onSaved: (message: string) => void; reload: () => void }) {
  const nodes = useMemo(() => flatten(all), [all]);
  const current = editing?.kind === "edit" ? all.find((c) => c.id === editing.id) ?? null : null;
  const columns: Column<Node>[] = [
    { key: "name", header: "Kategori", primary: true, cell: ({ category, depth }) => <span className="inline-flex items-baseline gap-1.5 break-words" style={{ paddingLeft: `${depth * 1.25}rem` }}>
      {depth > 0 && <span aria-hidden="true" className="font-mono text-fg-muted">└</span>}<span className="font-semibold">{category.name}</span>{depth > 0 && <span className="sr-only"> (alt kategori, düzey {depth + 1})</span>}</span> },
    { key: "slug", header: "Adres", cell: ({ category }) => <Kunye>{category.slug}</Kunye> },
    { key: "sort", header: "Sıra", align: "right", cell: ({ category }) => category.sortOrder },
    { key: "count", header: "Ürün", align: "right", cell: ({ category }) => category.productCount },
    { key: "st", header: "Durum", cell: ({ category }) => <ActiveChip active={category.isActive} /> },
    { key: "upd", header: "Güncellendi", cell: ({ category }) => <time dateTime={category.updatedAt} className="whitespace-nowrap text-fg-muted">{formatDateTime(category.updatedAt)}</time> },
    { key: "act", header: "İşlem", cell: ({ category }) => <button aria-label={`Düzenle: ${category.name}`} type="button" className={btnSmall} onClick={() => setEditing({ kind: "edit", id: category.id })}>Düzenle</button> },
  ];
  return <>
    {editing?.kind === "new" && <Section id="yeni-kategori" title="Yeni kategori"><CategoryForm all={all} category={null} onSaved={onSaved} onCancel={() => setEditing(null)} reload={reload} /></Section>}
    {editing?.kind === "edit" && current && <Section id="duzenle-kategori" title={`Düzenle: ${current.name}`}><CategoryForm key={`${current.id}-${current.version}`} all={all} category={current} onSaved={onSaved} onCancel={() => setEditing(null)} reload={reload} /></Section>}
    {editing?.kind === "edit" && !current && <Notice tone="warn">Bu kategori listede artık yok.</Notice>}
    <div className="mt-6">
      {nodes.length === 0 ? <Empty title="Henüz kategori yok">Ürün eklemeden önce en az bir kategori oluştur.</Empty> : <DataTable caption="Kategori ağacı" rows={nodes} columns={columns} rowKey={(n) => n.category.id} />}
    </div>
  </>;
}

function CategoryForm({ all, category, onSaved, onCancel, reload }: { all: Category[]; category: Category | null; onSaved: (m: string) => void; onCancel: () => void; reload: () => void }) {
  const [name, setName] = useState(category?.name ?? "");
  const [slug, setSlug] = useState(category?.slug ?? "");
  const [slugTouched, setSlugTouched] = useState(false);
  const [parentId, setParentId] = useState(category?.parentId ?? "");
  const [sortOrder, setSortOrder] = useState(String(category?.sortOrder ?? 0));
  const [isActive, setActive] = useState(category?.isActive ?? true);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiFailure | null>(null);
  const [localError, setLocalError] = useState("");
  const blocked = useMemo(() => (category ? descendantsOf(all, category.id) : new Set<string>()), [all, category]);
  const options = useMemo(() => flatten(all).filter((n) => !blocked.has(n.category.id)), [all, blocked]);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setFailure(null); setLocalError("");
    const order = Number(sortOrder);
    if (name.trim() === "") return setLocalError("Kategori adı gerekli.");
    if (!Number.isInteger(order) || order < 0 || order > 100000) return setLocalError("Sıra 0 ile 100000 arasında bir tam sayı olmalı.");
    setBusy(true);
    const parent = parentId === "" ? null : parentId;
    const result = category
      ? await aSend<Category>("PATCH", `/admin/categories/${enc(category.id)}`, {
        version: category.version,
        ...(name.trim() !== category.name ? { name: name.trim() } : {}),
        ...(parent !== category.parentId ? { parentId: parent } : {}),
        ...(order !== category.sortOrder ? { sortOrder: order } : {}),
        ...(isActive !== category.isActive ? { isActive } : {}),
      })
      : await aSend<Category>("POST", "/admin/categories", { name: name.trim(), slug: slug.trim(), ...(parent ? { parentId: parent } : {}), sortOrder: order, isActive });
    setBusy(false);
    if (result.ok) onSaved(category ? `“${name.trim()}” kaydedildi.` : `“${name.trim()}” oluşturuldu.`);
    else setFailure(result);
  }
  const slugErr = fieldError(failure, "slug") ?? (failure?.code === "category_slug_taken" ? failure.message : undefined);
  const generalError = failure && !["version_conflict", "category_slug_taken"].includes(failure.code) && Object.keys(failure.errors).length === 0 ? failure : null;
  return <form onSubmit={(e) => void submit(e)} className="max-w-2xl rounded-lg border border-line bg-card p-4 sm:p-5" noValidate aria-busy={busy}>
    <div className="grid gap-4 sm:grid-cols-2">
      <Field label="Ad" id="k-ad" error={fieldError(failure, "name")}>
        <input id="k-ad" value={name} maxLength={80} onChange={(e) => { setName(e.target.value); if (!category && !slugTouched) setSlug(slugify(e.target.value)); }} aria-invalid={!!fieldError(failure, "name")} aria-describedby={describedBy("k-ad", false, !!fieldError(failure, "name"))} className={inputClass} />
      </Field>
      <Field label="Adres (slug)" id="k-slug" error={slugErr} hint={category ? "Oluşturulduktan sonra değişmez." : "Küçük harf, rakam ve tire."}>
        <input id="k-slug" value={slug} maxLength={120} disabled={!!category} onChange={(e) => { setSlug(e.target.value); setSlugTouched(true); }} aria-invalid={!!slugErr} aria-describedby={describedBy("k-slug", true, !!slugErr)} className={`${inputClass} font-mono`} />
      </Field>
      <Field label="Üst kategori" id="k-ust" error={fieldError(failure, "parentId")}>
        <select id="k-ust" value={parentId} onChange={(e) => setParentId(e.target.value)} className={inputClass}>
          <option value="">Yok (ana kategori)</option>
          {options.map((n) => <option key={n.category.id} value={n.category.id}>{`${"— ".repeat(n.depth)}${n.category.name}`}</option>)}
        </select>
      </Field>
      <Field label="Sıra" id="k-sira" error={fieldError(failure, "sortOrder")} hint="Küçük sayı önce gelir.">
        <input id="k-sira" inputMode="numeric" value={sortOrder} onChange={(e) => setSortOrder(e.target.value)} aria-describedby="k-sira-hint" className={inputClass} />
      </Field>
    </div>
    <div className="mt-3"><CheckField id="k-aktif" label="Satışta (aktif)" checked={isActive} onChange={setActive} hint="Pasif kategori ve altındaki ürünler vitrinde görünmez." /></div>
    {localError && <p role="alert" className="mt-3 text-sm text-red-800">{localError}</p>}
    {failure?.code === "version_conflict" && <div className="mt-4"><ConflictNotice onReload={reload} /></div>}
    {generalError && <div className="mt-4"><FailureBox failure={generalError} /></div>}
    <FormActions><SubmitButton busy={busy}>{category ? "Kaydet" : "Kategori oluştur"}</SubmitButton><CancelButton onClick={onCancel} disabled={busy} /></FormActions>
  </form>;
}

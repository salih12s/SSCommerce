"use client";

import Link from "next/link";
import { useState, type FormEvent } from "react";
import { aGet, qs } from "@/lib/admin/client";
import { formatDateTime } from "@/lib/admin/format";
import { listOf, type Category, type Paged, type ProductSummary } from "@/lib/admin/types";
import { useRemote } from "@/lib/admin/use-remote";
import { formatPriceMinor } from "@/lib/format";
import { Thumb } from "../OrderParts";
import { btnPrimary } from "../ui";
import { flatten } from "./CategoriesAdmin";
import { ActiveChip, btnSmall, Chip, DataTable, Empty, Field, inputClass, Kunye, Loaded, PageHead, Pager, pagedOf, type Column } from "./kit";

const PAGE_SIZE = 20;

export function ProductsAdmin() {
  const [text, setText] = useState("");
  const [applied, setApplied] = useState({ q: "", categoryId: "", status: "all" });
  const [categoryId, setCategoryId] = useState("");
  const [status, setStatus] = useState("all");
  const [page, setPage] = useState(1);
  const categories = useRemote(() => aGet<Category[] | { items: Category[] }>("/admin/categories"), "kategoriler");
  const query = qs({ q: applied.q, categoryId: applied.categoryId, status: applied.status, page, pageSize: PAGE_SIZE });
  const remote = useRemote(() => aGet<Paged<ProductSummary>>(`/admin/products${query}`), query);
  const cats = categories.result?.ok ? flatten(listOf(categories.result.data)) : [];
  function apply(event: FormEvent) {
    event.preventDefault();
    setApplied({ q: text.trim(), categoryId, status }); setPage(1);
  }
  const columns: Column<ProductSummary>[] = [
    { key: "img", header: "Görsel", cell: (p) => <Thumb url={p.primaryImageUrl} alt={null} size={44} /> },
    { key: "name", header: "Ürün", primary: true, cell: (p) => <span className="break-words"><Link href={`/admin/urunler/${p.id}`} className="font-semibold underline underline-offset-2">{p.name}</Link>{p.isDemo && <> <Chip>demo</Chip></>}<span className="block"><Kunye>{p.slug}</Kunye></span></span> },
    { key: "cat", header: "Kategori", cell: (p) => p.categoryName },
    { key: "st", header: "Durum", cell: (p) => <ActiveChip active={p.isActive} /> },
    { key: "var", header: "Varyant", align: "right", cell: (p) => <span className="font-mono">{p.activeVariantCount}/{p.variantCount}<span className="sr-only"> aktif</span></span> },
    { key: "price", header: "En düşük fiyat", align: "right", cell: (p) => p.minPriceMinor === null ? <span className="text-fg-muted">—</span> : <span className="font-mono">{formatPriceMinor(p.minPriceMinor)}</span> },
    { key: "upd", header: "Güncellendi", cell: (p) => <time dateTime={p.updatedAt} className="whitespace-nowrap text-fg-muted">{formatDateTime(p.updatedAt)}</time> },
  ];
  return <>
    <PageHead title="Ürünler" lead="Pasif ürünler vitrinde görünmez. Fiyat değişikliği yalnız yeni önizlemeleri etkiler; verilmiş siparişler değişmez." actions={<Link href="/admin/urunler/yeni" className={btnPrimary}>Yeni ürün</Link>} />
    <form onSubmit={apply} role="search" className="mb-4 flex flex-wrap items-end gap-3">
      <Field label="Ara" id="urun-ara" className="min-w-0 flex-1 basis-56"><input id="urun-ara" type="search" value={text} onChange={(e) => setText(e.target.value)} maxLength={100} autoComplete="off" className={inputClass} /></Field>
      <Field label="Kategori" id="urun-kategori"><select id="urun-kategori" value={categoryId} onChange={(e) => setCategoryId(e.target.value)} className={inputClass}><option value="">Tümü</option>{cats.map((n) => <option key={n.category.id} value={n.category.id}>{`${"— ".repeat(n.depth)}${n.category.name}`}</option>)}</select></Field>
      <Field label="Durum" id="urun-durum"><select id="urun-durum" value={status} onChange={(e) => setStatus(e.target.value)} className={inputClass}><option value="all">Tümü</option><option value="active">Satışta</option><option value="inactive">Pasif</option></select></Field>
      <button type="submit" className={btnSmall}>Süz</button>
    </form>
    <Loaded remote={remote} label="Ürünler yükleniyor…">{(raw) => {
      const data = pagedOf<ProductSummary>({ ok: true, data: raw });
      if (!data || data.items.length === 0) return <Empty title={applied.q || applied.categoryId || applied.status !== "all" ? "Ölçüte uyan ürün yok" : "Henüz ürün yok"}>{applied.q || applied.categoryId || applied.status !== "all" ? "Süzgeci değiştirip tekrar dene." : "İlk ürünü oluşturmak için “Yeni ürün”e bas. Önce bir kategori gerekir."}</Empty>;
      return <><DataTable caption="Ürünler" rows={data.items} columns={columns} rowKey={(p) => p.id} /><Pager page={data.page} totalPages={data.totalPages} totalCount={data.totalCount} onPage={setPage} busy={remote.loading} /></>;
    }}</Loaded>
  </>;
}

import type { Metadata } from "next";
import Link from "next/link";
import { Suspense } from "react";
import { ProductCard } from "@/components/ProductCard";
import { getCategories, getProducts } from "@/lib/catalog";

export const metadata: Metadata = { title: "Ürün ara", description: "SSCommerce demo ürünlerini kategori, fiyat ve adla arayın.", alternates: { canonical: "/arama" } };
type Query = Record<string, string | string[] | undefined>;
const one = (value: string | string[] | undefined) => Array.isArray(value) ? value[0] ?? "" : value ?? "";
const options = [
  ["newest", "En yeni"], ["price_asc", "Fiyat: düşükten yükseğe"],
  ["price_desc", "Fiyat: yüksekten düşüğe"], ["name_asc", "Ada göre"],
] as const;
function pageHref(query: URLSearchParams, page: number) {
  const copy = new URLSearchParams(query);
  copy.set("page", String(page));
  return `/arama?${copy.toString()}`;
}
function toMinor(raw: string) {
  if (!raw) return "";
  if (!/^\d+(?:[.,]\d{1,2})?$/.test(raw)) return raw;
  const [units, fraction = ""] = raw.replace(",", ".").split(".");
  return (BigInt(units) * BigInt(100) + BigInt(fraction.padEnd(2, "0"))).toString();
}

async function Results({ searchParams }: { searchParams: Promise<Query> }) {
  const raw = await searchParams;
  const q = one(raw.q).trim().slice(0, 100);
  const category = one(raw.category);
  const sort = one(raw.sort) || "newest";
  const page = one(raw.page) || "1";
  const pageSize = ["6", "12", "24"].includes(one(raw.pageSize)) ? one(raw.pageSize) : "12";
  const minPrice = one(raw.minPrice);
  const maxPrice = one(raw.maxPrice);
  const apiQuery = new URLSearchParams({ page, pageSize, sort });
  if (q) apiQuery.set("q", q);
  if (category) apiQuery.set("category", category);
  if (minPrice) apiQuery.set("minPriceMinor", toMinor(minPrice));
  if (maxPrice) apiQuery.set("maxPriceMinor", toMinor(maxPrice));
  const [categories, products] = await Promise.all([getCategories(), getProducts(apiQuery)]);
  const pageQuery = new URLSearchParams({ q, category, sort, minPrice, maxPrice, pageSize });
  return <>
    <form action="/arama" method="get" className="mt-6 grid gap-3 rounded-xl border border-line bg-card p-4 sm:grid-cols-2 lg:grid-cols-6">
      <label className="flex flex-col gap-1 text-sm font-semibold lg:col-span-2">Arama
        <input name="q" type="search" maxLength={100} defaultValue={q} placeholder="Ürün adı ara" className="h-11 rounded-md border border-line bg-paper px-3 font-normal" />
      </label>
      <label className="flex flex-col gap-1 text-sm font-semibold">Kategori
        <select name="category" defaultValue={category} className="h-11 rounded-md border border-line bg-paper px-3 font-normal">
          <option value="">Tümü</option>
          {categories.state === "ok" && categories.data.map((c) => <option key={c.id} value={c.slug}>{c.name}</option>)}
          {category && (categories.state !== "ok" || !categories.data.some((c) => c.slug === category)) && <option value={category}>{category}</option>}
        </select>
      </label>
      <label className="flex flex-col gap-1 text-sm font-semibold">En düşük ₺
        <input name="minPrice" type="number" min="0" step="0.01" defaultValue={minPrice} className="h-11 rounded-md border border-line bg-paper px-3 font-normal" />
      </label>
      <label className="flex flex-col gap-1 text-sm font-semibold">En yüksek ₺
        <input name="maxPrice" type="number" min="0" step="0.01" defaultValue={maxPrice} className="h-11 rounded-md border border-line bg-paper px-3 font-normal" />
      </label>
      <label className="flex flex-col gap-1 text-sm font-semibold">Sıralama
        <select name="sort" defaultValue={sort} className="h-11 rounded-md border border-line bg-paper px-3 font-normal">
          {options.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
        </select>
      </label>
      <label className="flex flex-col gap-1 text-sm font-semibold">Sayfa başına
        <select name="pageSize" defaultValue={pageSize} className="h-11 rounded-md border border-line bg-paper px-3 font-normal"><option value="6">6 ürün</option><option value="12">12 ürün</option><option value="24">24 ürün</option></select>
      </label>
      <div className="sm:col-span-2 lg:col-span-6"><button type="submit" className="rounded-md bg-ink px-5 py-2.5 font-semibold text-on-ink">Ürünleri göster</button></div>
    </form>
    {products.state === "ok" ? <>
      <p className="mt-6 text-fg-muted" role="status">{products.data.totalCount} ürün bulundu.</p>
      {products.data.items.length > 0 ? <ul className="mt-4 grid grid-cols-2 gap-3 sm:grid-cols-3 sm:gap-4 lg:grid-cols-4 xl:grid-cols-6">
        {products.data.items.map((p) => <li key={p.id}><ProductCard product={p} /></li>)}
      </ul> : <p className="mt-4 rounded-md border border-line bg-card p-4">Aradığınız koşullara uygun ürün bulunamadı.</p>}
      {products.data.totalPages > 1 && <nav aria-label="Sonuç sayfaları" className="mt-8 flex flex-wrap items-center gap-3">
        {products.data.page > 1 && <Link href={pageHref(pageQuery, products.data.page - 1)} className="rounded-md border border-line bg-card px-4 py-2">Önceki</Link>}
        <span>Sayfa {products.data.page} / {products.data.totalPages}</span>
        {products.data.page < products.data.totalPages && <Link href={pageHref(pageQuery, products.data.page + 1)} className="rounded-md border border-line bg-card px-4 py-2">Sonraki</Link>}
      </nav>}
    </> : products.state === "invalid" ? <div role="alert" className="mt-6 rounded-md border border-line bg-card p-4">Filtreleri kontrol edin. {Object.values(products.errors).flat().join(" ")}</div>
    : products.state === "not-found" ? <p role="status" className="mt-6">Kategori bulunamadı.</p>
    : <p role="status" className="mt-6 rounded-md border border-line bg-card p-4">Ürünler şu anda yüklenemedi. Lütfen daha sonra tekrar deneyin.</p>}
  </>;
}

export default function SearchPage({ searchParams }: PageProps<"/arama">) {
  return <div className="mx-auto max-w-7xl px-4 py-10 sm:px-6 md:py-section">
    <h1 className="font-display text-title font-extrabold">Ürün ara</h1>
    <Suspense fallback={<p role="status" className="mt-5 text-fg-muted">Ürünler yükleniyor…</p>}><Results searchParams={searchParams} /></Suspense>
  </div>;
}

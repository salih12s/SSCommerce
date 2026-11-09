import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";
import { Suspense } from "react";
import { ProductCard } from "@/features/catalog/components/ProductCard";
import { categories } from "@/content/demo-catalog";
import { getCategories, getProducts } from "@/features/catalog/catalog";

export function generateStaticParams() { return categories.map((c) => ({ slug: c.slug })); }
export async function generateMetadata({ params }: PageProps<"/kategori/[slug]">): Promise<Metadata> {
  const { slug } = await params;
  const category = categories.find((c) => c.slug === slug);
  return { title: category?.name ?? "Kategori", description: category?.blurb ?? "SSCommerce demo ürün kategorisi.", alternates: { canonical: `/kategori/${slug}` } };
}

async function CategoryResults({ params, searchParams }: { params: PageProps<"/kategori/[slug]">["params"]; searchParams: PageProps<"/kategori/[slug]">["searchParams"] }) {
  const { slug } = await params;
  const query = await searchParams;
  const page = Array.isArray(query.page) ? query.page[0] : query.page;
  const sort = Array.isArray(query.sort) ? query.sort[0] : query.sort;
  const rawPageSize = Array.isArray(query.pageSize) ? query.pageSize[0] : query.pageSize;
  const pageSize = ["6", "12", "24"].includes(rawPageSize ?? "") ? rawPageSize! : "12";
  const apiQuery = new URLSearchParams({ category: slug, page: page || "1", pageSize, sort: sort || "newest" });
  const [categoryResult, result] = await Promise.all([getCategories(), getProducts(apiQuery)]);
  if (result.state === "not-found") notFound();
  const category = categoryResult.state === "ok" ? categoryResult.data.find((c) => c.slug === slug) : undefined;
  if (categoryResult.state === "ok" && !category) notFound();
  const title = category?.name ?? categories.find((c) => c.slug === slug)?.name ?? "Kategori";
  const current = result.state === "ok" ? result.data.page : 1;
  return <>
    <h1 className="font-display text-title font-extrabold">{title}</h1>
    <p className="mt-3 text-fg-muted">Demo ürünler ve örnek fiyatlar. Ödeme henüz açık değil.</p>
    <form method="get" className="mt-5 flex flex-wrap items-center gap-2">
      <label htmlFor="sort" className="font-medium">Sıralama</label>
      <select id="sort" name="sort" defaultValue={sort || "newest"} className="h-11 rounded-md border border-line bg-card px-3">
        <option value="newest">En yeni</option><option value="price_asc">Fiyat: düşükten yükseğe</option>
        <option value="price_desc">Fiyat: yüksekten düşüğe</option><option value="name_asc">Ada göre</option>
      </select>
      <label htmlFor="pageSize" className="font-medium">Sayfa başına</label>
      <select id="pageSize" name="pageSize" defaultValue={pageSize} className="h-11 rounded-md border border-line bg-card px-3"><option value="6">6</option><option value="12">12</option><option value="24">24</option></select>
      <button type="submit" className="rounded-md bg-ink px-4 py-2.5 font-semibold text-on-ink">Uygula</button>
    </form>
    <p className="mt-3"><Link href={`/arama?category=${encodeURIComponent(slug)}`} className="text-accent-deep underline underline-offset-4">Bu kategoride fiyat veya adla filtrele</Link></p>
    {result.state === "ok" ? <>
      <p role="status" className="mt-6 text-fg-muted">{result.data.totalCount} ürün</p>
      {result.data.items.length > 0 ? <ul className="mt-4 grid grid-cols-2 gap-3 sm:grid-cols-3 sm:gap-4 lg:grid-cols-4 xl:grid-cols-6">
        {result.data.items.map((p) => <li key={p.id}><ProductCard product={p} /></li>)}
      </ul> : <p className="mt-5 rounded-md border border-line bg-card p-4">Bu kategoride henüz ürün yok.</p>}
      {result.data.totalPages > 1 && <nav aria-label="Kategori sayfaları" className="mt-8 flex items-center gap-3">
        {current > 1 && <Link href={`?sort=${encodeURIComponent(sort || "newest")}&pageSize=${pageSize}&page=${current - 1}`} className="rounded-md border border-line bg-card px-4 py-2">Önceki</Link>}
        <span>Sayfa {current} / {result.data.totalPages}</span>
        {current < result.data.totalPages && <Link href={`?sort=${encodeURIComponent(sort || "newest")}&pageSize=${pageSize}&page=${current + 1}`} className="rounded-md border border-line bg-card px-4 py-2">Sonraki</Link>}
      </nav>}
    </> : result.state === "invalid" ? <p role="alert" className="mt-6 rounded-md border border-line bg-card p-4">Sorgu geçersiz. {Object.values(result.errors).flat().join(" ")}</p>
    : <p role="status" className="mt-6 rounded-md border border-line bg-card p-4">Ürünler şu anda yüklenemedi. Lütfen daha sonra tekrar deneyin.</p>}
    <p className="mt-10"><Link href="/" className="font-medium text-accent-deep underline underline-offset-4">Ana sayfaya dön</Link></p>
  </>;
}
export default function CategoryPage({ params, searchParams }: PageProps<"/kategori/[slug]">) {
  return <div className="mx-auto max-w-7xl px-4 py-10 sm:px-6 md:py-section">
    <Suspense fallback={<p role="status">Kategori yükleniyor…</p>}><CategoryResults params={params} searchParams={searchParams} /></Suspense>
  </div>;
}

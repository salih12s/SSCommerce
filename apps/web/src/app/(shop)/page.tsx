import Link from "next/link";
import { Suspense } from "react";
import { Banners } from "@/components/Banners";
import { CategoryCard } from "@/components/CategoryCard";
import { Hero } from "@/components/Hero";
import { ProductCard } from "@/components/ProductCard";
import { ProductTabs, type TabDef, type TabItem } from "@/components/ProductTabs";
import { ServiceStrip } from "@/components/ServiceStrip";
import { categories } from "@/content/demo-catalog";
import { getCategories, getProducts } from "@/lib/catalog";

const gridClass = "grid grid-cols-2 gap-3 sm:grid-cols-3 sm:gap-4 lg:grid-cols-4 xl:grid-cols-6";

async function CatalogSections() {
  const [categoryResult, productResult] = await Promise.all([
    getCategories(),
    getProducts(new URLSearchParams({ page: "1", pageSize: "18" })),
  ]);
  const liveSlugs = new Set(categoryResult.state === "ok" ? categoryResult.data.map((c) => c.slug) : []);
  const items = productResult.state === "ok" ? productResult.data.items : [];
  const tabCategories = categoryResult.state === "ok"
    ? categoryResult.data.filter((c) => items.filter((p) => p.category.slug === c.slug).length >= 2)
    : [];
  const tabs: TabDef[] = [{ id: "tumu", label: "Tümü" }, ...tabCategories.map((c) => ({ id: c.slug, label: c.name }))];
  const tabItems: TabItem[] = items.map((p) => ({ key: p.id, groups: [p.category.slug], node: <ProductCard product={p} /> }));
  return <>
    <section id="kategoriler" aria-labelledby="kategoriler-baslik" className="scroll-mt-4 pt-8 md:pt-10">
      <div className="mx-auto max-w-7xl px-4 sm:px-6">
        <h2 id="kategoriler-baslik" className="font-display text-2xl font-extrabold sm:text-3xl">Kategoriler</h2>
        {categoryResult.state !== "ok" && <p role="status" className="mt-3 text-fg-muted">Kategoriler şu anda yüklenemedi. Lütfen daha sonra tekrar deneyin.</p>}
        <ul className="mt-4 grid grid-cols-4 gap-2 sm:gap-3 xl:grid-cols-8">
          {categories.map((c) => <li key={c.slug}><CategoryCard category={c} available={liveSlugs.has(c.slug)} /></li>)}
        </ul>
      </div>
    </section>
    <Banners availableSlugs={liveSlugs} />
    <section id="urunler" aria-labelledby="urunler-baslik" className="scroll-mt-4 py-8 md:py-12">
      <div className="mx-auto max-w-7xl px-4 sm:px-6">
        <div className="flex flex-wrap items-end justify-between gap-x-6 gap-y-1">
          <h2 id="urunler-baslik" className="font-display text-2xl font-extrabold sm:text-3xl">Ürünler</h2>
          <p className="text-[0.9375rem] text-fg-muted">Demo ürünler, örnek fiyatlar.</p>
        </div>
        {productResult.state === "ok" ? <>
          <div className="mt-3"><ProductTabs tabs={tabs} items={tabItems} label="Ürün grupları" gridClass={gridClass} /></div>
          {items.length === 0 && <p className="mt-5 text-fg-muted">Henüz listelenecek ürün yok.</p>}
          <p className="mt-6"><Link href="/arama" className="font-semibold text-accent-deep underline underline-offset-4">Tüm ürünleri ara ve filtrele</Link></p>
        </> : <p role="status" className="mt-5 rounded-md border border-line bg-card p-4 text-fg-muted">Ürünler şu anda yüklenemedi. Lütfen daha sonra tekrar deneyin.</p>}
      </div>
    </section>
  </>;
}

export default function Home() {
  return <>
    <Hero />
    <ServiceStrip />
    <Suspense fallback={<p role="status" className="mx-auto max-w-7xl px-4 py-10 text-fg-muted">Katalog yükleniyor…</p>}>
      <CatalogSections />
    </Suspense>
  </>;
}

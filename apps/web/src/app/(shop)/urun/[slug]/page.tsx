import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";
import { Suspense } from "react";
import { AddToCart } from "@/features/cart/components/AddToCart";
import { DeliveryInfo } from "@/features/catalog/components/DeliveryInfo";
import { ProductGallery } from "@/features/catalog/components/ProductGallery";
import { SimilarProducts } from "@/features/catalog/components/SimilarProducts";
import { StockLabel } from "@/features/catalog/components/StockLabel";
import { FavoriteButton } from "@/features/favorites/components/FavoriteButton";
import { getProduct } from "@/features/catalog/catalog";
import { formatPriceMinor } from "@/lib/format";

type ProductRouteProps = { params: Promise<{ slug: string }> };
export async function generateMetadata({ params }: ProductRouteProps): Promise<Metadata> {
  const { slug } = await params;
  const result = await getProduct(slug);
  return { title: result.state === "ok" ? result.data.name : "Ürün", description: result.state === "ok" ? (result.data.description ?? `${result.data.name} demo ürünü.`) : undefined, alternates: { canonical: `/urun/${slug}` } };
}
async function Detail({ params }: ProductRouteProps) {
  const { slug } = await params;
  const result = await getProduct(slug);
  if (result.state === "not-found") notFound();
  if (result.state !== "ok") return <p role="status" className="rounded-md border border-line bg-card p-4">Ürün şu anda yüklenemedi. Lütfen daha sonra tekrar deneyin.</p>;
  const product = result.data;
  return <>
    <nav aria-label="Konum" className="mb-6 text-sm text-fg-muted"><Link href="/">Ana sayfa</Link> / <Link href={`/kategori/${product.category.slug}`}>{product.category.name}</Link> / {product.name}</nav>
    <div className="grid gap-8 md:grid-cols-2">
      <ProductGallery images={product.images} />
      <div>
        <div className="relative pr-12">
          <p className="text-sm text-fg-muted">{product.category.name}</p>
          <h1 className="mt-2 font-display text-title font-extrabold">{product.name}</h1>
          <FavoriteButton productId={product.id} productName={product.name} />
        </div>
        {product.isDemo && <p className="mt-3 inline-block rounded-md border border-line bg-card px-3 py-1 text-sm">Demo ürün</p>}
        <p className="mt-6 font-display text-3xl font-bold tabular-nums">{formatPriceMinor(product.startingPriceMinor)} <span className="text-sm font-normal text-fg-muted">başlangıç fiyatı</span></p>
        <p className="mt-3" data-testid="product-stock"><StockLabel inStock={product.inStock} large /></p>
        {product.description && <p className="mt-6 leading-relaxed">{product.description}</p>}
        <DeliveryInfo />
        {product.variants.length > 0 && <AddToCart slug={slug} productName={product.name} variants={product.variants.map((v) => ({ id: v.id, displayName: v.displayName, priceMinor: v.priceMinor, inStock: v.inStock, availableQuantity: v.availableQuantity }))} />}
      </div>
    </div>
    {product.specifications && Object.keys(product.specifications).length > 0 && <section aria-labelledby="ozellikler" className="mt-10 max-w-2xl">
      <h2 id="ozellikler" className="font-display text-xl font-bold">Özellikler</h2>
      <dl className="mt-3 divide-y divide-line rounded-md border border-line bg-card px-4">{Object.entries(product.specifications).map(([key, value]) => <div key={key} className="grid grid-cols-2 gap-4 py-3"><dt className="text-fg-muted">{key}</dt><dd>{value}</dd></div>)}</dl>
    </section>}
    <Suspense fallback={null}><SimilarProducts categorySlug={product.category.slug} currentSlug={product.slug} /></Suspense>
  </>;
}
export default function ProductPage({ params }: ProductRouteProps) {
  return <div className="mx-auto max-w-7xl px-4 py-10 max-md:pb-28 sm:px-6 md:py-section"><Suspense fallback={<p role="status">Ürün yükleniyor…</p>}><Detail params={params} /></Suspense></div>;
}

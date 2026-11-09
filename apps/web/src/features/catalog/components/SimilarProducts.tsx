import { getProducts } from "@/features/catalog/catalog";
import { ProductCard } from "./ProductCard";

const MAX_SIMILAR = 4;

// "Benzer ürünler": aynı kategoriden en çok 4 ürün, bakılan ürün hariç. API'den 5 istenir (mevcut ürün listede olabilir), mevcut çıkarılır, 4'e kırpılır.
// Katalog verisi kişisel olmadığından sunucuda okunur. Liste boşsa ya da API hata verirse bölüm hiç çizilmez; ürün sayfası etkilenmez.
export async function SimilarProducts({ categorySlug, currentSlug }: { categorySlug: string; currentSlug: string }) {
  const result = await getProducts(new URLSearchParams({ category: categorySlug, pageSize: String(MAX_SIMILAR + 1) }));
  if (result.state !== "ok") return null;
  const items = result.data.items.filter((item) => item.slug !== currentSlug).slice(0, MAX_SIMILAR);
  if (items.length === 0) return null;
  return <section aria-labelledby="benzer-urunler" data-testid="similar-products" className="mt-12">
    <h2 id="benzer-urunler" className="font-display text-xl font-bold">Benzer ürünler</h2>
    <ul className="mt-4 grid grid-cols-2 gap-3 sm:gap-4 lg:grid-cols-4">
      {items.map((item) => <li key={item.id}><ProductCard product={item} /></li>)}
    </ul>
  </section>;
}

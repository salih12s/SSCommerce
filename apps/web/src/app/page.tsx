import { CategoryCard } from "@/components/CategoryCard";
import { CollectionSection } from "@/components/CollectionSection";
import { Hero } from "@/components/Hero";
import { ProductCard } from "@/components/ProductCard";
import { TrustSection } from "@/components/TrustSection";
import { categories, products } from "@/content/demo-catalog";

export default function Home() {
  return (
    <>
      <Hero />

      <section id="kategoriler" aria-labelledby="kategoriler-baslik" className="pb-section">
        <div className="mx-auto max-w-6xl px-4 sm:px-6">
          <h2 id="kategoriler-baslik" className="font-display text-title font-extrabold">
            Dört ana kategori
          </h2>
          <ul className="mt-8 grid grid-cols-2 gap-3 sm:gap-5 lg:grid-cols-4">
            {categories.map((c) => (
              <li key={c.slug}>
                <CategoryCard category={c} />
              </li>
            ))}
          </ul>
        </div>
      </section>

      <CollectionSection />

      <section aria-labelledby="urunler-baslik" className="py-section">
        <div className="mx-auto max-w-6xl px-4 sm:px-6">
          <h2 id="urunler-baslik" className="font-display text-title font-extrabold">
            Vitrinden örnekler
          </h2>
          <p className="mt-3 max-w-xl text-fg-muted">
            Sekiz örnek ürün. Her kart bir ürün künyesi gibi: kod, teknik özellik, demo fiyat.
          </p>
          <ul className="mt-10 grid grid-cols-2 gap-3 sm:gap-5 lg:grid-cols-4">
            {products.map((p) => (
              <li key={p.code}>
                <ProductCard product={p} />
              </li>
            ))}
          </ul>
        </div>
      </section>

      <TrustSection />
    </>
  );
}

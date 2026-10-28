import { collections, getProduct, type DemoCollection } from "@/content/demo-catalog";
import { formatPriceMinor } from "@/lib/format";

// Koleksiyon bir "parça listesi" gibi okunur: ürün kodu, ad, demo fiyat.
function Collection({ collection }: { collection: DemoCollection }) {
  const items = collection.productCodes.flatMap((code) => {
    const product = getProduct(code);
    return product ? [product] : [];
  });
  return (
    <article aria-labelledby={`koleksiyon-${collection.slug}`} className="flex flex-col rounded-md bg-ink p-6 text-on-ink sm:p-8">
      <h3 id={`koleksiyon-${collection.slug}`} className="font-display text-2xl font-bold">
        {collection.title}
      </h3>
      <p className="mt-2 text-on-ink-muted">{collection.blurb}</p>
      <ul className="mt-6 divide-y divide-white/10 border-t border-white/10">
        {items.map((p) => (
          <li key={p.code} className="flex flex-wrap items-baseline justify-between gap-x-4 py-3">
            <span>
              <span className="block font-mono text-[0.8125rem] text-on-ink-muted">{p.code}</span>
              <span className="font-medium">{p.name}</span>
            </span>
            <span className="font-mono text-sm tabular-nums">{formatPriceMinor(p.priceMinor)}</span>
          </li>
        ))}
      </ul>
      <p className="mt-auto pt-5 text-sm text-on-ink-muted">Demo fiyatlar. Koleksiyon sayfası yakında.</p>
    </article>
  );
}

export function CollectionSection() {
  return (
    <section aria-labelledby="koleksiyonlar-baslik" className="bg-paper-2 py-section">
      <div className="mx-auto max-w-6xl px-4 sm:px-6">
        <h2 id="koleksiyonlar-baslik" className="max-w-2xl font-display text-title font-extrabold">
          Birlikte iyi çalışan setler
        </h2>
        <p className="mt-3 max-w-xl text-fg-muted">Örnek koleksiyonlar; içerik ve fiyatlar demodur.</p>
        <div className="mt-10 grid gap-5 lg:grid-cols-3">
          {collections.map((c) => (
            <Collection key={c.slug} collection={c} />
          ))}
        </div>
      </div>
    </section>
  );
}

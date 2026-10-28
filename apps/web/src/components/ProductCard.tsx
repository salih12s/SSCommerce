import { formatPriceMinor } from "@/lib/format";
import { getCategory, type DemoProduct } from "@/content/demo-catalog";
import { ProductArt } from "./art";

export function ProductCard({ product }: { product: DemoProduct }) {
  const category = getCategory(product.categorySlug);
  return (
    <article className="flex h-full flex-col overflow-hidden rounded-md border border-line bg-card shadow-card">
      <div className="border-b border-line bg-grid bg-paper-2/70 px-2 pt-2 pb-3 sm:px-3 sm:pt-3 sm:pb-5">
        <div className="flex items-start justify-between gap-1">
          <span className="font-mono text-[0.75rem] text-fg-muted sm:text-[0.8125rem]">{product.code}</span>
          <span className="rounded-sm border border-accent-deep px-1 py-px text-[0.75rem] leading-tight font-semibold whitespace-nowrap text-accent-deep sm:px-1.5">
            Örnek ürün
          </span>
        </div>
        <ProductArt kind={product.art} alt={product.artAlt} className="mx-auto mt-2 h-16 w-auto sm:mt-4 sm:h-28" />
      </div>
      <div className="flex flex-1 flex-col gap-1.5 p-3 sm:gap-2 sm:p-5">
        {category && <p className="hidden text-[0.8125rem] text-fg-muted sm:block">{category.name}</p>}
        <h3 className="line-clamp-2 font-display text-base leading-snug font-bold sm:text-lg">{product.name}</h3>
        <p className="font-mono text-[0.75rem] leading-snug break-words text-fg sm:text-[0.8125rem]">
          {product.specs.join(" · ")}
        </p>
        <div className="mt-auto pt-2 sm:pt-3">
          <p className="font-display text-xl font-bold tabular-nums sm:text-2xl">{formatPriceMinor(product.priceMinor)}</p>
          <p className="text-[0.8125rem] text-fg-muted">Demo fiyat</p>
        </div>
        <p className="mt-1 flex min-h-11 items-center justify-center rounded-sm border border-dashed border-fg-muted px-2 text-center text-sm font-medium text-fg-muted sm:mt-2">
          Satış yakında
        </p>
      </div>
    </article>
  );
}

import Image from "next/image";
import Link from "next/link";
import { formatPriceMinor } from "@/lib/format";
import type { ProductListItem } from "@/lib/catalog";
import { ArrowIcon, HeartIcon } from "./Icons";
import { StockLabel } from "./StockLabel";

// Beyaz kart: fotoğraf (hepsi 4:3), kategori, ad, stok durumu, fiyat. Varyant seçimi ürün sayfasında olduğundan sağ alttaki
// simge ürün sayfasına götürür (sepete eklemez). Favori düğmesi pasiftir: aria-disabled ve adında "yakında" vardır; hiçbir şey yapmaz. İndirim, puan ve rozet yok (demo veri).
const softButton =
  "grid size-10 cursor-not-allowed place-items-center rounded-full text-fg-muted";

export function ProductCard({ product }: { product: ProductListItem }) {
  return (
    <article className="relative flex h-full flex-col overflow-hidden rounded-lg border border-line bg-card shadow-card">
      <div className="relative aspect-[4/3] bg-paper-2">
        {product.primaryImage ? <Image
          src={product.primaryImage.url}
          alt={product.primaryImage.altText}
          fill
          sizes="(min-width: 1280px) 190px, (min-width: 1024px) 25vw, (min-width: 640px) 33vw, 50vw"
          className="object-cover"
        /> : <span className="grid h-full place-items-center text-sm text-fg-muted">Görsel yok</span>}
        <button
          type="button"
          aria-disabled="true"
          aria-label={`${product.name} favorilere ekle, yakında`}
          className={`${softButton} absolute top-1.5 right-1.5 z-10 bg-card/90`}
        >
          <HeartIcon />
        </button>
      </div>
      <div className="flex flex-1 flex-col gap-1 p-3">
        <p className="text-[0.8125rem] leading-tight text-fg-muted">{product.category.name}</p>
        <h3 className="line-clamp-2 font-display text-[0.9375rem] leading-snug font-bold sm:text-base"><Link href={`/urun/${product.slug}`} className="after:absolute after:inset-0 hover:underline">{product.name}</Link></h3>
        <StockLabel inStock={product.inStock} className="mt-0.5" />
        <div className="mt-auto flex items-center justify-between gap-2 pt-2">
          <p className="font-display text-lg font-bold tabular-nums sm:text-xl">{formatPriceMinor(product.startingPriceMinor)}</p>
          <Link
            href={`/urun/${product.slug}`}
            aria-label={`${product.name} için seçenekleri gör`}
            className="relative z-10 grid size-10 shrink-0 place-items-center rounded-full bg-paper-2 text-fg transition-transform duration-150 ease-out active:scale-95"
          >
            <ArrowIcon />
          </Link>
        </div>
      </div>
    </article>
  );
}

import Image from "next/image";
import Link from "next/link";
import { formatPriceMinor } from "@/lib/format";
import type { ProductListItem } from "@/features/catalog/catalog";
import { ArrowIcon } from "@/components/ui/Icons";
import { FavoriteButton } from "@/features/favorites/components/FavoriteButton";
import { StockLabel } from "./StockLabel";

// Beyaz kart: fotoğraf (hepsi 4:3), kategori, ad, stok durumu, fiyat. Varyant seçimi ürün sayfasında olduğundan sağ alttaki
// simge ürün sayfasına götürür (sepete eklemez). Köşedeki kalp bir istemci adacığıdır (favoriler tarayıcıda okunur); kartın kendisi sunucu bileşenidir. İndirim, puan ve rozet yok (demo veri).

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
        <FavoriteButton productId={product.id} productName={product.name} />
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

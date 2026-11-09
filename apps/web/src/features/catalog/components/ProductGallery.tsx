"use client";

import Image from "next/image";
import { useRef, useState } from "react";

export type GalleryImage = { url: string; altText: string; isPrimary: boolean; sortOrder: number };

/** Birincil görsel önce, sonrası `sortOrder` sırasıyla (eşitlikte API sırası korunur). */
export function orderImages(images: readonly GalleryImage[]): GalleryImage[] {
  return images
    .map((image, index) => ({ image, index }))
    .sort((a, b) => Number(b.image.isPrimary) - Number(a.image.isPrimary) || a.image.sortOrder - b.image.sortOrder || a.index - b.index)
    .map((entry) => entry.image);
}

// Ürün galerisi (istemci adacığı): büyük görsel + küçük resim şeridi. Dil: küçük resimler asılı etiket gibidir (AccountNav ile aynı):
// seçili olan düz çizgili koyu kenarlıklı, diğerleri kesik çizgili. Tek görselde şerit yoktur; görsel yoksa boş durum gösterilir.
// Klavye: şerit tek tab durağıdır (roving tabindex); ←/→ (↑/↓) önceki/sonraki, Home/End ilk/son görsele geçer ve odağı taşır.
// Seçili küçük resim aria-current="true" alır; görselin değiştiği, görünmez bir canlı bölgeyle ("Görsel 2 / 5") bildirilir.
export function ProductGallery({ images }: { images: readonly GalleryImage[] }) {
  const ordered = orderImages(images);
  const [index, setIndex] = useState(0);
  const buttons = useRef<(HTMLButtonElement | null)[]>([]);

  if (ordered.length === 0) {
    return <div data-testid="product-gallery" className="relative aspect-[4/3] overflow-hidden rounded-xl border border-line bg-paper-2"><span className="grid h-full place-items-center text-fg-muted">Ürün görseli yok</span></div>;
  }
  const count = ordered.length;
  const active = ordered[Math.min(index, count - 1)];
  const at = Math.min(index, count - 1);

  function go(next: number) {
    setIndex(next);
    buttons.current[next]?.focus();
  }
  function onKeyDown(event: React.KeyboardEvent) {
    if (event.altKey || event.ctrlKey || event.metaKey) return;
    let next = -1;
    if (event.key === "ArrowRight" || event.key === "ArrowDown") next = (at + 1) % count;
    else if (event.key === "ArrowLeft" || event.key === "ArrowUp") next = (at - 1 + count) % count;
    else if (event.key === "Home") next = 0;
    else if (event.key === "End") next = count - 1;
    if (next < 0) return;
    event.preventDefault();
    go(next);
  }

  return <div data-testid="product-gallery" className="min-w-0">
    <div className="relative aspect-[4/3] overflow-hidden rounded-xl border border-line bg-paper-2">
      <Image key={`${at}-${active.url}`} src={active.url} alt={active.altText} fill priority={at === 0} sizes="(min-width: 768px) 50vw, 100vw" data-testid="gallery-main" className="animate-gallery-in object-cover" />
      {count > 1 && <span aria-hidden="true" className="absolute bottom-2 left-2 rounded-md border border-dashed border-line bg-card/95 px-2 py-0.5 font-mono text-xs tabular-nums shadow-card">{at + 1} / {count}</span>}
    </div>
    {count > 1 && <>
      <p role="status" className="sr-only" data-testid="gallery-live">Görsel {at + 1} / {count}</p>
      <ul aria-label="Ürün görselleri" onKeyDown={onKeyDown} data-testid="gallery-thumbs" className="no-scrollbar mt-3 flex gap-2 overflow-x-auto p-1">
        {ordered.map((image, i) => {
          const current = i === at;
          return <li key={`${i}-${image.url}`} className="shrink-0">
            <button
              ref={(node) => { buttons.current[i] = node; }}
              type="button"
              tabIndex={current ? 0 : -1}
              aria-current={current ? "true" : undefined}
              aria-label={`Görsel ${i + 1} / ${count}: ${image.altText}`}
              data-testid="gallery-thumb"
              onClick={() => setIndex(i)}
              className={`relative block size-16 overflow-hidden rounded-md border-2 bg-paper-2 transition-colors duration-150 ease-out motion-reduce:transition-none sm:size-20 ${current ? "border-solid border-ink" : "border-dashed border-line hover:border-fg-muted"}`}
            >
              <Image src={image.url} alt="" fill sizes="80px" className="object-cover" />
            </button>
          </li>;
        })}
      </ul>
    </>}
  </div>;
}

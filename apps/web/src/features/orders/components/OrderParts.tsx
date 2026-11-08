import Image from "next/image";
import { labelOf } from "@/features/orders/orders";
import { formatDateTimeIstanbul } from "@/lib/format";

// Sipariş ekranlarının ortak parçaları. Dil: "fiş" (ürün künyesinin devamı): mono sipariş numarası, kesik çizgili ayraç, damga gibi durum etiketi.
const stampTone: Record<string, string> = {
  placed: "border-accent-deep text-accent-deep",
  processing: "border-ink text-ink",
  shipped: "border-ink text-ink",
  completed: "border-emerald-800 text-emerald-900",
  cancelled: "border-fg-muted text-fg-muted",
};

/** Durum damgası. Renk tek başına anlam taşımaz: metin her zaman yazılıdır. */
export function StatusStamp({ status }: { status: string }) {
  return <span data-testid="order-status" data-status={status} className={`inline-block shrink-0 -rotate-2 rounded-md border-2 px-2.5 py-0.5 font-display text-sm font-bold ${stampTone[status] ?? "border-line text-fg"}`}>{labelOf(status)}</span>;
}

export const formatOrderDate = (iso: string) => formatDateTimeIstanbul(iso);

/** Küçük ürün görseli; görsel yoksa boş kutu (içerik metni yanında olduğundan alt metin boş bırakılabilir). */
export function Thumb({ url, alt, size = 56 }: { url: string | null; alt: string | null; size?: number }) {
  return <span className="relative block shrink-0 overflow-hidden rounded-md border border-line bg-paper-2" style={{ width: size, height: size }}>
    {url ? <Image src={url} alt={alt ?? ""} fill sizes={`${size}px`} className="object-cover" /> : null}
  </span>;
}

/** Fişin kesik çizgisi: iki yanda kart içine doğru yarım daire çentik. Yalnızca görsel. */
export function Perforation() {
  return <div aria-hidden="true" className="relative my-5 border-t-2 border-dashed border-line">
    <span className="absolute -top-2.5 -left-[calc(1.875rem+1px)] size-5 rounded-full border border-line bg-paper [clip-path:inset(0_0_0_50%)] sm:-left-[calc(2.125rem+1px)]" />
    <span className="absolute -top-2.5 -right-[calc(1.875rem+1px)] size-5 rounded-full border border-line bg-paper [clip-path:inset(0_50%_0_0)] sm:-right-[calc(2.125rem+1px)]" />
  </div>;
}

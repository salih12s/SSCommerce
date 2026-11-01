// Stok durumu etiketi. Yalnızca API'nin söylediğini gösterir; satış açık demek değildir.
// availableQuantity en çok 20'dir ("en az bu kadar"): kesin depo miktarı olarak gösterilmez, yalnızca 1..5 arası "Son N adet" olur.
export const LOW_STOCK_MAX = 5;

export function stockText(inStock: boolean, availableQuantity?: number): string {
  if (!inStock) return "Stokta yok";
  if (availableQuantity !== undefined && availableQuantity >= 1 && availableQuantity <= LOW_STOCK_MAX) return `Son ${availableQuantity} adet`;
  return "Stokta";
}

export function StockLabel({ inStock, availableQuantity, large = false, className = "" }: { inStock: boolean; availableQuantity?: number; large?: boolean; className?: string }) {
  return (
    <span data-stock={inStock ? "in" : "out"} className={`inline-flex items-center gap-1.5 ${large ? "text-base" : "text-[0.8125rem]"} leading-tight font-medium ${inStock ? "text-fg" : "text-fg-muted"} ${className}`}>
      <span aria-hidden="true" className={`size-2 shrink-0 rounded-full border border-current ${inStock ? "bg-current" : "bg-transparent"}`} />
      {stockText(inStock, availableQuantity)}
    </span>
  );
}

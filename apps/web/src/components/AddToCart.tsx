"use client";

import Link from "next/link";
import { useState } from "react";
import { addToCart, UNCONFIRMED_MESSAGE, useCart } from "@/lib/cart";
import { formatPriceMinor } from "@/lib/format";
import { loginHref, registerHref } from "@/lib/safe-next";
import { QuantityStepper } from "./QuantityStepper";
import { StockLabel } from "./StockLabel";
import { btnPrimary, btnLink } from "./ui";

export type PickerVariant = { id: string; displayName: string; priceMinor: number; inStock: boolean; availableQuantity: number };
type Message = { kind: "ok" | "error"; text: string };
const MAX_LINE = 20;

// Ürün sayfasında varyant seçimi + adet + "Sepete ekle". Stok metni API'den gelir; availableQuantity en çok 20'dir ("en az bu kadar"),
// bu yüzden adet sınırı olarak kullanılır ama kesin depo miktarı diye gösterilmez. Gerçek karar sunucudadır (409 insufficient_stock).
export function AddToCart({ slug, productName, variants }: { slug: string; productName: string; variants: PickerVariant[] }) {
  const cart = useCart();
  const firstAvailable = variants.find((v) => v.inStock && v.availableQuantity > 0);
  const [selectedId, setSelectedId] = useState(firstAvailable?.id ?? "");
  const [quantity, setQuantity] = useState(1);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<Message | null>(null);
  const selected = variants.find((v) => v.id === selectedId);
  const max = selected ? Math.min(selected.availableQuantity, MAX_LINE) : 1;
  const known = cart.status === "ready" || cart.status === "error" || (cart.status === "loading" && cart.cart !== null);
  const here = `/urun/${slug}`;

  function choose(variant: PickerVariant) {
    setSelectedId(variant.id);
    setQuantity((q) => Math.min(q, Math.max(1, Math.min(variant.availableQuantity, MAX_LINE))));
    setMessage(null);
  }
  async function add() {
    if (!selected || busy) return;
    setBusy(true); setMessage(null);
    const result = await addToCart(selected.id, quantity);
    setBusy(false);
    if (result.ok) { setMessage({ kind: "ok", text: `Sepete eklendi: ${productName}, ${selected.displayName}, ${quantity} adet.` }); return; }
    if (result.code === "insufficient_stock") {
      const n = Number(result.extra.availableQuantity);
      setMessage({ kind: "error", text: Number.isFinite(n) && n > 0 && n < MAX_LINE ? `Şu an en fazla ${n} adet görünüyor; sepetindeki adetle birlikte bunu aşamazsın.` : "İstenen adet için yeterli stok görünmüyor." });
      return;
    }
    if (result.status === 401) return;
    // Ağ/5xx: istek sunucuda uygulanmış olabilir; sepet yeniden okundu (üst çubuktaki sayı güncel). "Eklenmedi" denmez.
    setMessage({ kind: "error", text: "unconfirmed" in result && result.unconfirmed ? UNCONFIRMED_MESSAGE : result.message });
  }

  return (
    <section aria-labelledby="varyantlar" className="mt-8">
      <h2 id="varyantlar" className="font-display text-xl font-bold">Seçenekler</h2>
      <ul className="mt-3 space-y-2">
        {variants.map((variant) => {
          const selectable = variant.inStock && variant.availableQuantity > 0;
          return (
            <li key={variant.id}>
              <label className={`flex min-h-12 flex-wrap items-center justify-between gap-x-3 gap-y-1 rounded-md border bg-card p-3 ${selectedId === variant.id ? "border-ink ring-1 ring-ink" : "border-line"} ${selectable ? "cursor-pointer" : "cursor-not-allowed"}`}>
                <span className="flex items-center gap-3">
                  <input type="radio" name="varyant" value={variant.id} checked={selectedId === variant.id} disabled={!selectable} onChange={() => choose(variant)} className="size-5 accent-[var(--color-ink)]" />
                  <span>{variant.displayName}</span>
                </span>
                <span className="flex flex-wrap items-center gap-x-4 gap-y-1">
                  <StockLabel inStock={variant.inStock} availableQuantity={variant.availableQuantity} />
                  <span className="font-semibold tabular-nums">{formatPriceMinor(variant.priceMinor)}</span>
                </span>
              </label>
            </li>
          );
        })}
      </ul>

      {cart.status === "anonymous" ? (
        <div className="mt-5 rounded-md border border-line bg-card p-4" data-testid="signin-required">
          <p className="font-semibold">Sepete eklemek için giriş yapmalısın.</p>
          <p className="mt-1 text-fg-muted">Sepetin hesabında saklanır. Giriş yaptıktan sonra bu ürüne dönersin.</p>
          <div className="mt-3 flex flex-wrap items-center gap-x-5">
            <Link href={loginHref(here)} className={btnPrimary}>Giriş yap</Link>
            <Link href={registerHref(here)} className={btnLink}>Hesap oluştur</Link>
          </div>
        </div>
      ) : (
        <div className="mt-5 flex flex-wrap items-center gap-4">
          {selected && <QuantityStepper label="Adet" value={quantity} max={max} busy={busy} onChange={setQuantity} />}
          <button type="button" onClick={add} disabled={!selected || busy || !known} className={btnPrimary}>
            {!selected ? "Stokta yok" : busy ? "Ekleniyor…" : "Sepete ekle"}
          </button>
        </div>
      )}
      {!selected && cart.status !== "anonymous" && <p className="mt-2 text-fg-muted">Bu ürünün hiçbir seçeneği şu an stokta görünmüyor.</p>}
      <div role="status" className="mt-3 empty:hidden">
        {message?.kind === "ok" && <p className="rounded-md border border-line bg-card p-3">{message.text} <Link href="/sepet" className={btnLink}>Sepete git</Link></p>}
      </div>
      {message?.kind === "error" && <p role="alert" className="mt-3 rounded-md border border-red-300 bg-red-50 p-3 text-red-800">{message.text}</p>}
      <p className="mt-3 text-sm text-fg-muted">Sepet stok ayırmaz; stok ve fiyat sipariş öncesinde değişebilir.</p>
    </section>
  );
}

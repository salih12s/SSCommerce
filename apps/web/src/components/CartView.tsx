"use client";

import Image from "next/image";
import Link from "next/link";
import { Fragment, useEffect, useRef, useState } from "react";
import { loadCart, removeCartItem, setCartQuantity, UNCONFIRMED_MESSAGE, useCart, type Cart, type CartItem } from "@/lib/cart";
import { formatPriceMinor } from "@/lib/format";
import { loginHref } from "@/lib/safe-next";
import { QuantityStepper } from "./QuantityStepper";
import { btnPrimary, btnSecondary, panel } from "./ui";

const MAX_LINE = 20;
type Failure = { itemId: string; text: string };

// Satır durumu metni: yalnızca API'nin söylediği (status, availableQuantity, priceChanged).
export function statusNote(item: CartItem): string | null {
  if (item.status === "out_of_stock") return "Bu ürün şu an stokta görünmüyor.";
  if (item.status === "unavailable") return "Bu ürün artık satışta değil. Fiyat gösterilmez ve ara toplama girmez.";
  if (item.status === "insufficient_stock") return `Şu an en fazla ${item.availableQuantity} adet görünüyor. Adedi azaltabilir ya da satırı kaldırabilirsin.`;
  return null;
}

function Line({ item, busy, onQuantity, onRemove }: { item: CartItem; busy: boolean; onQuantity: (q: number) => void; onRemove: () => void }) {
  const note = statusNote(item);
  // Adet en çok 20; stok yetersizse artırılamaz ama azaltmak her zaman serbest.
  const max = item.status === "available" ? Math.min(Math.max(item.availableQuantity, item.quantity), MAX_LINE) : item.quantity;
  // Dar ekranda küçük görsel başlığın yanında durur, kalan içerik tam genişliktedir; sm ve üstünde görsel solda iki satırı kaplar.
  return (
    <li data-testid="cart-line" data-status={item.status} className={`${panel} grid grid-cols-[5.5rem_1fr] gap-x-4 gap-y-3 sm:grid-cols-[7rem_1fr]`}>
      <div className="relative aspect-[4/3] w-full self-start overflow-hidden rounded-md bg-paper-2 sm:row-span-2">
        {item.imageUrl ? <Image src={item.imageUrl} alt={item.imageAlt ?? ""} fill sizes="(min-width: 640px) 112px, 88px" className="object-cover" /> : <span className="grid h-full place-items-center px-1 text-center text-xs text-fg-muted">Görsel yok</span>}
      </div>
      <div className="min-w-0">
        <h2 className="font-display text-lg leading-snug font-bold break-words"><Link href={`/urun/${item.productSlug}`} className="hover:underline">{item.productName}</Link></h2>
        <p className="text-fg-muted">{item.variantName}</p>
        <p className="mt-1 font-semibold tabular-nums">
          {item.unitPriceMinor === null ? <span className="font-normal text-fg-muted">Fiyat yok</span> : <>{formatPriceMinor(item.unitPriceMinor)} <span className="text-sm font-normal text-fg-muted">birim fiyat</span></>}
        </p>
      </div>
      <div className="col-span-2 min-w-0 sm:col-span-1">
        {item.priceChanged && item.previousUnitPriceMinor !== null && item.unitPriceMinor !== null && (
          <p data-testid="price-changed" className="mb-2 rounded-md border border-line bg-paper px-3 py-2 text-sm">Fiyat değişti: önceki fiyat {formatPriceMinor(item.previousUnitPriceMinor)}, güncel fiyat {formatPriceMinor(item.unitPriceMinor)}.</p>
        )}
        {note && <p data-testid="line-note" className="mb-2 rounded-md border border-amber-400 bg-amber-50 px-3 py-2 text-sm text-amber-950">{note} Stok durumu değişebilir; sepet stok ayırmaz.</p>}
        <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-3">
          <QuantityStepper label={`${item.productName} adedi`} value={item.quantity} min={1} max={max} busy={busy} onChange={onQuantity} />
          <p className="font-display text-lg font-bold tabular-nums" data-testid="line-total"><span className="sr-only">Satır toplamı </span>{item.lineSubtotalMinor === null ? "—" : formatPriceMinor(item.lineSubtotalMinor)}</p>
        </div>
        <button type="button" id={`kaldir-${item.id}`} disabled={busy} onClick={onRemove} aria-label={`${item.productName}, ${item.variantName} satırını kaldır`} className={`${btnSecondary} mt-3`}>Kaldır</button>
      </div>
    </li>
  );
}

export function CartView() {
  const state = useCart({ refresh: true });
  const cart: Cart | null = "cart" in state ? state.cart : null;
  // Yenileme başarısızsa eldeki sepet "son bilinen"dir: ara toplam ve devam bağlantısı güncelmiş gibi gösterilmez.
  const stale = state.status === "error" && cart !== null;
  const [busyId, setBusyId] = useState<string | null>(null);
  const [failure, setFailure] = useState<Failure | null>(null);
  const [announce, setAnnounce] = useState("");
  const focusRef = useRef<string | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);

  // Silmeden sonra odak bir sonraki satırın "Kaldır" düğmesine, satır kalmadıysa başlığa gider (odak kaybolmaz).
  useEffect(() => {
    const id = focusRef.current;
    if (id === null) return;
    focusRef.current = null;
    (id === "heading" ? headingRef.current : document.getElementById(id))?.focus();
  }, [cart]);

  async function changeQuantity(item: CartItem, quantity: number) {
    if (busyId) return;
    setBusyId(item.id); setFailure(null);
    const result = await setCartQuantity(item.id, quantity);
    setBusyId(null);
    if (result.ok) setAnnounce(`${item.productName} adedi ${quantity} olarak güncellendi. Ara toplam ${formatPriceMinor(result.data.subtotalMinor)}.`);
    else if (result.status !== 401) setFailure({ itemId: item.id, text: "unconfirmed" in result && result.unconfirmed ? UNCONFIRMED_MESSAGE : result.code === "insufficient_stock" ? "Bu adet için yeterli stok görünmüyor; sepetin değişmedi." : `${result.message} Sepetin değişmedi.` });
  }
  async function remove(item: CartItem, index: number) {
    if (busyId) return;
    setBusyId(item.id); setFailure(null);
    const others = (cart?.items ?? []).filter((i) => i.id !== item.id);
    focusRef.current = others.length === 0 ? "heading" : `kaldir-${others[Math.min(index, others.length - 1)].id}`;
    const result = await removeCartItem(item.id);
    setBusyId(null);
    if (result.ok) {
      setAnnounce(`${item.productName} sepetten kaldırıldı. Sepette ${result.data.itemCount} ürün var.`);
    } else if (result.status !== 401) {
      focusRef.current = null;
      const unconfirmed = "unconfirmed" in result && result.unconfirmed;
      setFailure({ itemId: item.id, text: unconfirmed ? UNCONFIRMED_MESSAGE : result.code === "cart_item_not_found" ? "Bu satır sepetinde artık yok. Sepet yenilendi." : `${result.message} Sepetin değişmedi.` });
      if (result.code === "cart_item_not_found") void loadCart(true);
    }
  }

  let body;
  if (state.status === "anonymous") {
    body = <div className={`${panel} mt-6`} data-testid="cart-anonymous"><p className="font-semibold">Sepetini görmek için giriş yapmalısın.</p><p className="mt-1 text-fg-muted">Sepetin hesabında saklanır; giriş yapmadan sepet oluşturulmaz.</p><Link href={loginHref("/sepet")} className={`${btnPrimary} mt-4`}>Giriş yap</Link></div>;
  } else if (!cart && state.status === "error") {
    body = <div role="alert" className={`${panel} mt-6`} data-testid="cart-error"><p className="font-semibold">Sepet yüklenemedi.</p><p className="mt-1 text-fg-muted">{state.message}</p><button type="button" onClick={() => void loadCart(true)} className={`${btnPrimary} mt-4`}>Tekrar dene</button></div>;
  } else if (!cart) {
    body = <p role="status" className="mt-6" data-testid="cart-loading">Sepet yükleniyor…</p>;
  } else if (cart.items.length === 0 && !stale) {
    body = <div className={`${panel} mt-6`} data-testid="cart-empty"><p className="font-semibold">Sepetin boş.</p><p className="mt-1 text-fg-muted">Beğendiğin ürünün sayfasında seçenek ve adet seçip sepete ekleyebilirsin.</p><Link href="/#urunler" className={`${btnPrimary} mt-4`}>Ürünlere göz at</Link></div>;
  } else {
    const hasUnavailable = cart.items.some((i) => i.status === "unavailable");
    body = <div className="mt-6 grid gap-6 lg:grid-cols-[1fr_22rem] lg:items-start">
      <div>
        {stale && <div role="alert" data-testid="cart-stale" className="mb-4 rounded-md border border-red-300 bg-red-50 p-3 text-red-800"><p className="font-semibold">Sepet güncellenemedi; son bilinen sepet gösteriliyor.</p><p className="text-sm">Aşağıdaki fiyat, stok ve adetler güncel olmayabilir. {state.status === "error" ? state.message : ""}</p><button type="button" onClick={() => void loadCart(true)} className={`${btnSecondary} mt-2`}>Tekrar dene</button></div>}
        {!stale && cart.hasIssues && <p data-testid="cart-issues" className="mb-4 rounded-md border border-amber-400 bg-amber-50 p-3 text-amber-950">Bazı satırlarda stok ya da satış durumu sorunu var. Satırlardaki notlara bak.</p>}
        <ul className="space-y-4">
          {cart.items.map((item, index) => <Fragment key={item.id}>
            <Line item={item} busy={busyId === item.id} onQuantity={(q) => void changeQuantity(item, q)} onRemove={() => void remove(item, index)} />
            {failure?.itemId === item.id && <li role="alert" data-testid="line-failure" className="-mt-2 rounded-md border border-red-300 bg-red-50 p-3 text-red-800">{failure.text}</li>}
          </Fragment>)}
        </ul>
        <p className="mt-4 text-sm text-fg-muted">Sepet stok ayırmaz. Stok ve fiyat sipariş öncesinde değişebilir; satır durumları sayfa her açıldığında yeniden okunur.</p>
      </div>
      <aside aria-labelledby="ozet" className={`${panel} lg:sticky lg:top-4`}>
        <h2 id="ozet" className="font-display text-xl font-bold">Sepet özeti</h2>
        {stale ? <p data-testid="summary-stale" className="mt-3 text-fg-muted">Güncel ara toplam alınamadı. Özeti ve teslimat adımını görmek için sepeti yenile.</p> : <>
          <dl className="mt-4 space-y-2">
            <div className="flex justify-between gap-4"><dt>Ürün adedi</dt><dd className="tabular-nums">{cart.itemCount}</dd></div>
            <div className="flex justify-between gap-4"><dt>Ara toplam</dt><dd data-testid="subtotal" className="font-display text-lg font-bold tabular-nums">{formatPriceMinor(cart.subtotalMinor)}</dd></div>
            <div className="flex justify-between gap-4"><dt>Kargo</dt><dd className="text-fg-muted">Henüz hesaplanmadı</dd></div>
          </dl>
          {hasUnavailable && <p className="mt-3 text-sm text-fg-muted">Satışta olmayan satırlar ara toplama girmez.</p>}
          <Link href="/checkout" className={`${btnPrimary} mt-5 w-full`}>Teslimat adresine geç</Link>
          <p className="mt-3 text-sm text-fg-muted">Bir sonraki adımda adresini seçip siparişi onaylarsın. Ödeme alınmaz.</p>
        </>}
      </aside>
    </div>;
  }
  return <div className="mx-auto max-w-5xl px-4 py-10 sm:px-6 md:py-14">
    <h1 ref={headingRef} tabIndex={-1} className="font-display text-title font-extrabold outline-none">Sepetim</h1>
    <p role="status" className="sr-only" data-testid="cart-announce">{announce}</p>
    {body}
  </div>;
}

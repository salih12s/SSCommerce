"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { addToCart, UNCONFIRMED_MESSAGE, useCart } from "@/features/cart/cart";
import { formatPriceMinor } from "@/lib/format";
import { loginHref, registerHref } from "@/lib/safe-next";
import { QuantityStepper } from "./QuantityStepper";
import { StockLabel } from "@/features/catalog/components/StockLabel";
import { btnPrimary, btnLink } from "@/components/ui/styles";

export type PickerVariant = { id: string; displayName: string; priceMinor: number; inStock: boolean; availableQuantity: number };
type Message = { kind: "ok" | "error"; text: string };
const MAX_LINE = 20;

// Ürün sayfasında varyant seçimi + adet + "Sepete ekle". Stok metni API'den gelir; availableQuantity en çok 20'dir ("en az bu kadar"),
// bu yüzden adet sınırı olarak kullanılır ama kesin depo miktarı diye gösterilmez. Gerçek karar sunucudadır (409 insufficient_stock).
// Mobil sabit çubuk (< md) AYNI bileşenin parçasıdır: durum ve add() ortaktır, sepete ekleme mantığı çoğaltılmaz. Asıl form görünür alanın üstünden
// geçilince belirir; sayfa ilk açıldığında (form aşağıdayken) başlığı örtmesin diye gizlidir. Tek seçilebilir varyant varsa "Hızlı ekle" add() çağırır; birden çok seçenek varsa seçimi gerektirdiği için
// asıl formdaki seçili seçeneğe kaydırıp odaklar; misafirde giriş bağlantısıdır. Çubuk gizliyken inert + görünmez: odak sırasına ve erişilebilirlik ağacına girmez.
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

  const sectionRef = useRef<HTMLElement>(null);
  const [passed, setPassed] = useState(false); // form görünür alanın ÜSTÜNDEN geçildi mi (ilk açılışta form aşağıdadır: çubuk gizli)
  const [footerVisible, setFooterVisible] = useState(false);
  useEffect(() => {
    const section = sectionRef.current;
    if (!section) return;
    // Form "geçildi" = formun alt kenarı görünür alanın üstüne çıktı. IntersectionObserver yalnız kesişim DURUMU değişince bildirir; aşağıdan
    // yukarıya tek hamlede atlanınca (bağlantı/ankraj, kaydırma konumu geri yükleme) durum "kesişmiyor"dan "kesişmiyor"a geçer ve bildirim gelmez.
    // Bu yüzden konum kaydırmada ölçülür (rAF ile seyreltilmiş, pasif dinleyici).
    let frame = 0;
    const measure = () => { frame = 0; setPassed(section.getBoundingClientRect().bottom <= 0); };
    const schedule = () => { if (!frame) frame = requestAnimationFrame(measure); };
    measure();
    window.addEventListener("scroll", schedule, { passive: true });
    window.addEventListener("resize", schedule);
    // Kaydırma olmadan yerleşim değişirse (görsel yüklenir, sepet durumu gelip giriş kartı çıkar) konum yeniden ölçülür.
    const layout = typeof ResizeObserver !== "undefined" ? new ResizeObserver(schedule) : null;
    layout?.observe(document.body);
    // Alt bilgi çubuğun üst kenarına ulaşınca çubuk çekilir: çubuk + alt gezinti (~9rem = 144px) payı düşülür.
    const footer = document.querySelector("footer");
    const foot = typeof IntersectionObserver !== "undefined" && footer ? new IntersectionObserver(([entry]) => setFooterVisible(entry.isIntersecting), { rootMargin: "0px 0px -144px 0px" }) : null;
    if (footer) foot?.observe(footer);
    return () => { window.removeEventListener("scroll", schedule); window.removeEventListener("resize", schedule); if (frame) cancelAnimationFrame(frame); layout?.disconnect(); foot?.disconnect(); };
  }, []);
  const selectableCount = variants.filter((v) => v.inStock && v.availableQuantity > 0).length;
  const barMode = !selected ? "none" : cart.status === "anonymous" ? "login" : selectableCount === 1 ? "add" : "choose";
  const barVisible = barMode !== "none" && passed && !footerVisible;
  const lowestPrice = Math.min(...variants.map((v) => v.priceMinor));
  function focusChoice() {
    const target = sectionRef.current?.querySelector<HTMLInputElement>("input[name='varyant']:checked") ?? sectionRef.current?.querySelector<HTMLInputElement>("input[name='varyant']:not(:disabled)");
    const calm = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    (target?.closest("li") ?? sectionRef.current)?.scrollIntoView({ block: "center", behavior: calm ? "auto" : "smooth" });
    target?.focus({ preventScroll: true });
  }

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
    <>
    <section ref={sectionRef} aria-labelledby="varyantlar" className="mt-8">
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

    <div
      role="group"
      aria-label="Hızlı sepete ekle"
      data-testid="sticky-cart-bar"
      data-sticky-bar
      data-visible={barVisible}
      inert={!barVisible}
      className={`fixed inset-x-0 bottom-[calc(4.5rem+env(safe-area-inset-bottom,0px))] z-30 border-t-2 border-dashed border-line bg-card shadow-[0_-8px_24px_-16px_rgb(60_40_20/0.4)] transition-[transform,visibility] duration-200 ease-out motion-reduce:transition-none md:hidden ${barVisible ? "translate-y-0" : "invisible translate-y-full"}`}
    >
      <div className="mx-auto flex max-w-7xl items-center gap-3 px-4 py-2.5 sm:px-6">
        <div className="min-w-0 flex-1">
          <p className="truncate text-sm font-semibold">{productName}</p>
          <p className="font-display text-lg leading-tight font-bold tabular-nums">
            {barMode === "add" && selected ? formatPriceMinor(selected.priceMinor) : <>{formatPriceMinor(lowestPrice)} <span className="font-sans text-xs font-normal text-fg-muted">başlangıç</span></>}
          </p>
        </div>
        {barMode === "login" && <Link href={loginHref(here)} className={`${btnPrimary} min-h-11 shrink-0 px-4 py-2`}>Giriş yap</Link>}
        {barMode === "choose" && <button type="button" onClick={focusChoice} className={`${btnPrimary} min-h-11 shrink-0 px-4 py-2`}>Seçenekleri gör</button>}
        {barMode === "add" && (message?.kind === "ok"
          ? <Link href="/sepet" className={`${btnPrimary} min-h-11 shrink-0 px-4 py-2`}>Sepete git</Link>
          : <button type="button" onClick={() => void add()} disabled={busy || !known} className={`${btnPrimary} min-h-11 shrink-0 px-4 py-2`}>{busy ? "Ekleniyor…" : "Hızlı ekle"}</button>)}
      </div>
      {barMode === "add" && message?.kind === "error" && <p className="px-4 pb-2 text-xs text-red-800 sm:px-6">Sepete eklenemedi. <button type="button" onClick={() => focusChoice()} className="inline-flex min-h-11 items-center font-semibold underline underline-offset-2">Ayrıntıyı gör</button></p>}
    </div>
    </>
  );
}

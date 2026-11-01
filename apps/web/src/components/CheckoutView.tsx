"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { listAddresses, type Address } from "@/lib/addresses";
import { previewCheckout, type CheckoutIssue, type CheckoutPreview } from "@/lib/cart";
import { formatPriceMinor } from "@/lib/format";
import { usePersistedPageshow } from "@/lib/page-restore";
import { loginHref } from "@/lib/safe-next";
import { AddressCard } from "./AddressManager";
import { AddressForm } from "./AddressForm";
import { statusNote } from "./CartView";
import { btnLink, btnPrimary, panel } from "./ui";

type Load = { kind: "loading" } | { kind: "anonymous" } | { kind: "error"; message: string } | { kind: "ready"; items: Address[] };
type Preview = { kind: "idle" } | { kind: "loading" } | { kind: "empty" } | { kind: "error"; message: string } | { kind: "ready"; data: CheckoutPreview };

const issueText: Record<CheckoutIssue["code"], string> = {
  out_of_stock: "Stokta görünmeyen satır var.",
  insufficient_stock: "İstenen adet için yeterli stok görünmeyen satır var.",
  unavailable: "Artık satışta olmayan satır var.",
  price_changed: "Fiyatı değişen satır var.",
};

// Ödeme öncesi özet: sepet ve adres sunucuda birleştirilir, buradaki tutarlar API'nin döndürdüğü kuruş değerlerinden biçimlenir.
// Sipariş verilmez, ödeme alınmaz, stok ayrılmaz; toplam "ödenecek tutar" olarak asla gösterilmez.
export function CheckoutView() {
  const [load, setLoad] = useState<Load>({ kind: "loading" });
  const [selected, setSelected] = useState("");
  const [adding, setAdding] = useState(false);
  const [preview, setPreview] = useState<Preview>({ kind: "idle" });
  const [attempt, setAttempt] = useState(0);

  const apply = (result: Awaited<ReturnType<typeof listAddresses>>, select?: string) => {
    if (!result.ok) { setLoad(result.status === 401 ? { kind: "anonymous" } : { kind: "error", message: result.message }); return; }
    setLoad({ kind: "ready", items: result.data });
    setSelected((current) => select ?? (result.data.some((a) => a.id === current) ? current : result.data[0]?.id ?? ""));
  };
  const refresh = async (select?: string) => apply(await listAddresses(), select);
  useEffect(() => { void listAddresses().then((result) => apply(result)); }, []);

  // bfcache geri yüklemesi: ekrandaki adres ve özet önceki kimliğe ait olabilir; temizle ve yeniden oku.
  usePersistedPageshow(() => { setLoad({ kind: "loading" }); setSelected(""); setPreview({ kind: "idle" }); void listAddresses().then((result) => apply(result)); });

  // Seçim değiştikçe sunucudan özet istenir; eski istek geç dönerse yok sayılır.
  useEffect(() => {
    if (!selected) return;
    let alive = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setPreview({ kind: "loading" });
    previewCheckout(selected).then((result) => {
      if (!alive) return;
      if (result.ok) setPreview({ kind: "ready", data: result.data });
      else if (result.status === 401) setLoad({ kind: "anonymous" });
      else if (result.code === "cart_empty") setPreview({ kind: "empty" });
      else if (result.code === "address_not_found") { setPreview({ kind: "idle" }); void listAddresses().then((list) => apply(list)); }
      else setPreview({ kind: "error", message: result.message });
    });
    return () => { alive = false; };
  }, [selected, attempt]);

  const heading = <h1 className="font-display text-title font-extrabold">Teslimat ve özet</h1>;
  const wrap = (content: React.ReactNode) => <div className="mx-auto max-w-5xl px-4 py-10 sm:px-6 md:py-14">{heading}{content}</div>;
  if (load.kind === "loading") return wrap(<p role="status" className="mt-6">Adresler yükleniyor…</p>);
  if (load.kind === "anonymous") return wrap(<div className={`${panel} mt-6`} data-testid="checkout-anonymous"><p className="font-semibold">Devam etmek için giriş yapmalısın.</p><p className="mt-1 text-fg-muted">Sepetin ve adreslerin hesabında saklanır.</p><Link href={loginHref("/checkout")} className={`${btnPrimary} mt-4`}>Giriş yap</Link></div>);
  if (load.kind === "error") return wrap(<div role="alert" className={`${panel} mt-6`}><p className="font-semibold">Adresler yüklenemedi.</p><p className="mt-1 text-fg-muted">{load.message}</p><button type="button" onClick={() => { setLoad({ kind: "loading" }); void refresh(); }} className={`${btnPrimary} mt-4`}>Tekrar dene</button></div>);

  const addresses = load.items;
  const showForm = addresses.length === 0 || adding;
  return wrap(<div className="mt-6 grid gap-6 lg:grid-cols-[1fr_24rem] lg:items-start">
    <div className="space-y-6">
      {addresses.length > 0 && <div className={panel}><fieldset className="m-0 min-w-0 border-0 p-0">
        <legend className="mb-3 p-0 font-display text-xl font-bold">Teslimat adresi</legend>
        <ul className="mt-3 space-y-3">
          {addresses.map((address) => <li key={address.id}>
            <label className={`flex cursor-pointer items-start gap-3 rounded-md border bg-paper p-3 ${selected === address.id ? "border-ink ring-1 ring-ink" : "border-line"}`}>
              <input type="radio" name="adres" value={address.id} checked={selected === address.id} onChange={() => setSelected(address.id)} className="mt-1 size-5 shrink-0 accent-[var(--color-ink)]" />
              <AddressCard address={address} />
            </label>
          </li>)}
        </ul>
        <div className="mt-3 flex flex-wrap items-center gap-x-5">
          {!adding && <button type="button" onClick={() => setAdding(true)} className={btnLink}>Yeni adres ekle</button>}
          <Link href="/hesabim/adresler" className={btnLink}>Adresleri düzenle</Link>
        </div>
      </fieldset></div>}
      {showForm && <section aria-labelledby="adres-ekle" className={panel}>
        <h2 id="adres-ekle" className="mb-1 font-display text-xl font-bold">{addresses.length === 0 ? "Teslimat adresi ekle" : "Yeni adres"}</h2>
        {addresses.length === 0 && <p className="mb-4 text-fg-muted">Özeti görmek için bir teslimat adresi gerekir.</p>}
        <AddressForm idPrefix="odeme-adres" submitLabel="Adresi kaydet ve seç" onCancel={addresses.length > 0 ? () => setAdding(false) : undefined} onSaved={(address) => { setAdding(false); void refresh(address.id); }} />
      </section>}
    </div>

    <aside aria-labelledby="ozet" className={`${panel} lg:sticky lg:top-4`} aria-busy={preview.kind === "loading"}>
      <h2 id="ozet" className="font-display text-xl font-bold">Sepet özeti</h2>
      <div role="status" className="sr-only" data-testid="preview-announce">{preview.kind === "ready" ? "Özet güncellendi." : ""}</div>
      {preview.kind === "idle" && <p className="mt-3 text-fg-muted">Özet için bir adres seç.</p>}
      {preview.kind === "loading" && <p className="mt-3" data-testid="preview-loading">Özet hazırlanıyor…</p>}
      {preview.kind === "empty" && <div className="mt-3" data-testid="checkout-empty"><p className="font-semibold">Sepetin boş.</p><Link href="/#urunler" className={`${btnPrimary} mt-3`}>Ürünlere göz at</Link></div>}
      {preview.kind === "error" && <div role="alert" className="mt-3"><p>{preview.message}</p><button type="button" onClick={() => setAttempt((n) => n + 1)} className={`${btnPrimary} mt-3`}>Tekrar dene</button></div>}
      {preview.kind === "ready" && <Summary data={preview.data} />}
      <button type="button" aria-disabled="true" data-testid="order-disabled" className={`${btnPrimary} mt-5 w-full`}>Sipariş verme yakında</button>
      <p className="mt-3 text-sm text-fg-muted">Sipariş verme ve ödeme henüz açık değil. Fiyat ve stok sipariş öncesinde değişebilir; sepet stok ayırmaz.</p>
    </aside>
  </div>);
}

function Summary({ data }: { data: CheckoutPreview }) {
  return <div data-testid="preview" data-can-proceed={String(data.canProceed)}>
    <div data-testid="preview-address" className="mt-3 rounded-md border border-line bg-paper p-3 text-sm"><p className="font-semibold">Teslimat: {data.address.label}</p><p>{data.address.recipientName}</p><p className="break-words">{data.address.district} / {data.address.province}</p></div>
    <ul className="mt-3 divide-y divide-line">
      {data.lines.map((line) => <li key={line.id} data-testid="preview-line" data-status={line.status} className="py-3">
        <p className="font-semibold">{line.productName}</p>
        <p className="text-fg-muted">{line.variantName}</p>
        <p className="mt-1 flex justify-between gap-3 tabular-nums"><span>{line.quantity} adet{line.unitPriceMinor !== null ? ` × ${formatPriceMinor(line.unitPriceMinor)}` : ""}</span><span className="font-semibold">{line.lineSubtotalMinor === null ? "—" : formatPriceMinor(line.lineSubtotalMinor)}</span></p>
        {line.priceChanged && line.previousUnitPriceMinor !== null && line.unitPriceMinor !== null && <p className="mt-1 text-sm">Fiyat değişti: önceki fiyat {formatPriceMinor(line.previousUnitPriceMinor)}, güncel fiyat {formatPriceMinor(line.unitPriceMinor)}.</p>}
        {statusNote(line) && <p className="mt-1 rounded-md border border-amber-400 bg-amber-50 px-3 py-2 text-sm text-amber-950">{statusNote(line)}</p>}
      </li>)}
    </ul>
    <dl className="mt-2 space-y-2 border-t border-line pt-3">
      <div className="flex justify-between gap-4"><dt>Ara toplam</dt><dd data-testid="preview-subtotal" className="font-display text-lg font-bold tabular-nums">{formatPriceMinor(data.subtotalMinor)}</dd></div>
      <div className="flex justify-between gap-4"><dt>Kargo</dt><dd data-testid="preview-shipping" className="text-fg-muted">{data.shippingMinor === null ? "Kargo henüz hesaplanmadı" : formatPriceMinor(data.shippingMinor)}</dd></div>
    </dl>
    <p className="mt-2 text-sm text-fg-muted">Kargo hesaplanmadığı için toplam tutar henüz belli değil.</p>
    <p className="mt-1 text-sm text-fg-muted">Özet {new Date(data.calculatedAt).toLocaleTimeString("tr-TR", { hour: "2-digit", minute: "2-digit" })} itibarıyla hesaplandı.</p>
    {!data.canProceed && <div data-testid="preview-issues" className="mt-3 rounded-md border border-amber-400 bg-amber-50 p-3 text-amber-950">
      <p className="font-semibold">Devam etmeden önce sepetini düzenle.</p>
      <ul className="mt-1 list-disc pl-5 text-sm">{Array.from(new Set(data.issues.map((i) => i.code))).map((code) => <li key={code}>{issueText[code] ?? "Sepetinde bir sorun var."}</li>)}</ul>
      <Link href="/sepet" className={`${btnLink} mt-1`}>Sepete dön</Link>
    </div>}
  </div>;
}

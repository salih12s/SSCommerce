"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { formatPriceMinor } from "@/lib/format";
import { cancelOrder, getOrder, isOrderId, isUnknownOutcome, labelOf, takeJustPlaced, type Order } from "@/lib/orders";
import { usePersistedPageshow } from "@/lib/page-restore";
import { loginHref } from "@/lib/safe-next";
import { ConfirmDialog } from "./ConfirmDialog";
import { formatOrderDate, Perforation, StatusStamp, Thumb } from "./OrderParts";
import { btnLink, btnPrimary, btnSecondary, panel } from "./ui";

type State = { kind: "loading" } | { kind: "anonymous" } | { kind: "notfound" } | { kind: "error"; message: string } | { kind: "ready"; order: Order };
type Notice = { tone: "ok" | "warn"; text: string };

// Sipariş ayrıntısı: yalnız oturumdaki kullanıcının siparişi, istemcide okunur. Kimlik rotadan gelir; ?sorgu parametreleri okunmaz.
// Yok ya da başkasının siparişi için tek bir "bulunamadı" görünümü vardır (sunucu da aynı 404'ü döner).
export function OrderDetailView({ id }: { id: string }) {
  const validId = isOrderId(id);
  const [state, setState] = useState<State>(validId ? { kind: "loading" } : { kind: "notfound" });
  const [attempt, setAttempt] = useState(0);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const cancelLock = useRef(false);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const cancelButtonRef = useRef<HTMLButtonElement>(null);
  const focusHeading = useRef(true);
  // Pencere kapanırken <dialog> odağı eski öğesine (kaybolmuş iptal düğmesi) iade eder; odak ancak pencere kapandıktan SONRA başlığa alınır
  // (alt bileşenin efekti üstünkinden önce çalışır).
  const [focusTick, setFocusTick] = useState(0);

  // Okuma. Yenilemede mevcut sipariş ekranda kalır (liste/dönüş titremesi yok); yalnız ilk yükleme "yükleniyor" gösterir.
  useEffect(() => {
    if (!validId) return;
    let alive = true;
    void getOrder(id).then((result) => {
      if (!alive) return;
      if (result.ok) {
        const placed = takeJustPlaced(result.data.id);
        if (placed) setNotice({ tone: "ok", text: placed.replayed ? "Bu sipariş daha önce oluşturulmuştu; aynı sipariş açıldı. Ödeme alınmadı." : "Siparişin alındı. Ödeme alınmadı." });
        setState({ kind: "ready", order: result.data });
      } else if (result.status === 401) setState({ kind: "anonymous" });
      else if (result.status === 404 || result.status === 400) setState({ kind: "notfound" });
      else setState({ kind: "error", message: result.message });
    });
    return () => { alive = false; };
  }, [id, validId, attempt]);

  // Sayfa açılınca odak başlığa taşınır (klavye/ekran okuyucu yeni içeriğin başında başlar).
  useEffect(() => {
    if (state.kind === "ready" && focusHeading.current) { focusHeading.current = false; headingRef.current?.focus({ preventScroll: true }); }
  }, [state.kind]);

  useEffect(() => { if (focusTick > 0) headingRef.current?.focus({ preventScroll: true }); }, [focusTick]);

  // bfcache geri yüklemesi: ekrandaki sipariş önceki kimliğe ait olabilir; temizle ve yeniden oku.
  usePersistedPageshow(() => { if (validId) { setState({ kind: "loading" }); setNotice(null); setConfirming(false); focusHeading.current = true; setAttempt((n) => n + 1); } });

  async function reload(): Promise<boolean> {
    const fresh = await getOrder(id);
    if (fresh.ok) { setState({ kind: "ready", order: fresh.data }); return true; }
    if (fresh.status === 401) setState({ kind: "anonymous" });
    else if (fresh.status === 404) setState({ kind: "notfound" });
    return false;
  }

  // İptal: tekrarı güvenlidir (sunucu zaten iptal edilmişse replayed:true döner, stok iki kez iade edilmez).
  async function confirmCancel() {
    if (cancelLock.current) return;
    cancelLock.current = true; setCancelling(true);
    const result = await cancelOrder(id);
    cancelLock.current = false; setCancelling(false); setConfirming(false);
    if (result.ok) {
      setState({ kind: "ready", order: result.data });
      setNotice({ tone: "ok", text: result.data.replayed ? "Sipariş zaten iptal edilmişti." : "Siparişin iptal edildi. Ürünler stoğa geri döndü." });
      setFocusTick((n) => n + 1);
      return;
    }
    if (result.status === 401) { setState({ kind: "anonymous" }); return; }
    if (result.status === 404) { setState({ kind: "notfound" }); return; }
    if (result.code === "order_transition_invalid") {
      await reload();
      setNotice({ tone: "warn", text: "Siparişin durumu değişmiş; artık iptal edilemiyor. Güncel durum aşağıda." });
    } else if (isUnknownOutcome(result)) {
      const fresh = await reload();
      setNotice({ tone: "warn", text: fresh ? "İptalin sonucu doğrulanamadı; siparişin güncel durumu yüklendi. İptal edilebiliyorsa tekrar deneyebilirsin: tekrar güvenlidir, stok iki kez iade edilmez." : "İptalin sonucu doğrulanamadı. Sayfayı yenileyip siparişin durumuna bak." });
    } else {
      setNotice({ tone: "warn", text: `Sipariş iptal edilemedi. ${result.message}` });
    }
    setFocusTick((n) => n + 1);
  }

  const shell = (content: React.ReactNode) => <div className="mx-auto max-w-3xl px-4 py-10 sm:px-6 md:py-14">{content}</div>;
  const back = <Link href="/siparisler" className={btnLink}>Siparişlerim</Link>;
  if (state.kind === "loading") return shell(<><h1 className="font-display text-title font-extrabold">Sipariş</h1><p role="status" className="mt-6" data-testid="order-loading">Sipariş yükleniyor…</p></>);
  if (state.kind === "anonymous") return shell(<><h1 className="font-display text-title font-extrabold">Sipariş</h1><div className={`${panel} mt-6`} data-testid="order-anonymous"><p className="font-semibold">Siparişi görmek için giriş yapmalısın.</p><Link href={loginHref(`/siparisler/${id}`)} className={`${btnPrimary} mt-4`}>Giriş yap</Link></div></>);
  if (state.kind === "notfound") return shell(<><h1 ref={headingRef} tabIndex={-1} className="font-display text-title font-extrabold outline-none">Sipariş bulunamadı</h1><div className={`${panel} mt-6`} data-testid="order-not-found"><p>Aradığın sipariş yok ya da bu hesaba ait değil.</p><div className="mt-3">{back}</div></div></>);
  if (state.kind === "error") return shell(<><h1 className="font-display text-title font-extrabold">Sipariş</h1><div role="alert" className={`${panel} mt-6`} data-testid="order-error"><p className="font-semibold">Sipariş yüklenemedi.</p><p className="mt-1 text-fg-muted">{state.message}</p><button type="button" onClick={() => { setState({ kind: "loading" }); setAttempt((n) => n + 1); }} className={`${btnPrimary} mt-4`}>Tekrar dene</button></div></>);

  const order = state.order;
  const address = order.deliveryAddress;
  return shell(<>
    <div className="mb-3">{back}</div>
    <h1 ref={headingRef} tabIndex={-1} className="font-display text-title font-extrabold outline-none">Sipariş <span data-testid="order-number" className="font-mono text-[0.85em] font-medium">{order.orderNumber}</span></h1>
    <p role="status" data-testid="order-notice-live" className="sr-only">{notice?.text ?? ""}</p>
    {notice && <p data-testid="order-notice" className={`mt-4 rounded-md border p-3 ${notice.tone === "ok" ? "border-emerald-700 bg-emerald-50 text-emerald-950" : "border-amber-400 bg-amber-50 text-amber-950"}`}>{notice.text}</p>}
    <p data-testid="order-unpaid" className="mt-4 rounded-md border border-line bg-card p-3">Ödeme alınmadı. Bu sipariş ödenmemiş bir kayıttır; kargo henüz hesaplanmadı, bu yüzden genel toplam belli değil.</p>

    <article aria-label={`Sipariş ${order.orderNumber} fişi`} className={`${panel} mt-6`}>
      <div className="flex items-start justify-between gap-3">
        <div><p className="text-sm text-fg-muted">Sipariş tarihi</p><p data-testid="order-date">{formatOrderDate(order.createdAt)}</p></div>
        <StatusStamp status={order.status} />
      </div>
      <Perforation />
      <ul className="divide-y divide-line" data-testid="order-items">
        {order.items.map((item) => <li key={item.id} data-testid="order-item" className="flex gap-3 py-3 first:pt-0">
          <Thumb url={item.imageUrl} alt={item.imageAlt} size={64} />
          <div className="min-w-0 flex-1">
            <p className="font-semibold break-words"><Link href={`/urun/${item.productSlug}`} className="hover:underline">{item.productName}</Link></p>
            <p className="text-fg-muted">{item.variantName}</p>
            <p className="font-mono text-xs text-fg-muted">{item.sku}</p>
            <p className="mt-1 flex justify-between gap-3 tabular-nums"><span>{item.quantity} adet × {formatPriceMinor(item.unitPriceMinor)}</span><span className="font-semibold">{formatPriceMinor(item.lineTotalMinor)}</span></p>
          </div>
        </li>)}
      </ul>
      <Perforation />
      <dl className="space-y-2">
        <div className="flex justify-between gap-4"><dt>Ürün adedi</dt><dd className="tabular-nums">{order.itemCount}</dd></div>
        <div className="flex justify-between gap-4"><dt>Ara toplam</dt><dd data-testid="order-subtotal" className="font-display text-lg font-bold tabular-nums">{formatPriceMinor(order.subtotalMinor)}</dd></div>
        <div className="flex justify-between gap-4"><dt>Kargo</dt><dd data-testid="order-shipping" className="text-fg-muted">{order.shippingMinor === null ? "Henüz hesaplanmadı" : formatPriceMinor(order.shippingMinor)}</dd></div>
        <div className="flex justify-between gap-4"><dt>Ödeme</dt><dd className="text-fg-muted">Alınmadı</dd></div>
      </dl>
    </article>

    <section aria-labelledby="teslimat" className={`${panel} mt-6`}>
      <h2 id="teslimat" className="font-display text-xl font-bold">Teslimat adresi</h2>
      <div data-testid="order-address" className="mt-3 min-w-0">
        <p className="font-semibold">{address.label}</p>
        <p>{address.recipientName}</p>
        <p className="break-words text-fg-muted">{address.streetAddress}</p>
        <p className="break-words text-fg-muted">{address.district} / {address.province}{address.postalCode ? ` ${address.postalCode}` : ""}</p>
        <p className="text-fg-muted tabular-nums">Tel: {address.phone}</p>
      </div>
      <p className="mt-3 text-sm text-fg-muted">Bu, sipariş verdiğin andaki adres kopyasıdır. Adres defterini sonradan değiştirsen ya da silsen de bu sipariş değişmez.</p>
    </section>

    <section aria-labelledby="gecmis" className={`${panel} mt-6`}>
      <h2 id="gecmis" className="font-display text-xl font-bold">Sipariş geçmişi</h2>
      <ol data-testid="order-timeline" className="mt-4 ml-1.5 border-l-2 border-line">
        {order.statusHistory.map((entry, index) => {
          const current = index === order.statusHistory.length - 1;
          return <li key={`${entry.toStatus}-${entry.occurredAt}`} data-status={entry.toStatus} aria-current={current ? "step" : undefined} className="relative pb-5 pl-5 last:pb-0">
            <span aria-hidden="true" className={`absolute top-1.5 -left-[7px] size-3 rounded-full border-2 ${current ? "border-ink bg-ink" : "border-line bg-card"}`} />
            <p className={current ? "font-semibold" : ""}>{labelOf(entry.toStatus)}</p>
            <p className="text-sm text-fg-muted">{formatOrderDate(entry.occurredAt)}</p>
          </li>;
        })}
      </ol>
    </section>

    {order.canCancel
      ? <section aria-labelledby="iptal" className={`${panel} mt-6`}>
        <h2 id="iptal" className="font-display text-xl font-bold">Siparişi iptal et</h2>
        <p className="mt-2 text-fg-muted">Sipariş henüz hazırlanmaya alınmadığı için iptal edebilirsin. İptal edersen ürünler stoğa geri döner.</p>
        <button ref={cancelButtonRef} type="button" data-testid="order-cancel" onClick={() => setConfirming(true)} className={`${btnSecondary} mt-4`}>Siparişi iptal et</button>
      </section>
      : order.status !== "cancelled" && <p data-testid="order-no-cancel" className="mt-6 text-sm text-fg-muted">Sipariş işleme alındığı için artık buradan iptal edilemez.</p>}

    <ConfirmDialog open={confirming} title="Sipariş iptal edilsin mi?" description={`${order.orderNumber} numaralı sipariş iptal edilecek ve ürünler stoğa geri dönecek. Bu işlem geri alınamaz.`} confirmLabel="Siparişi iptal et" busyLabel="İptal ediliyor…" busy={cancelling} onConfirm={() => void confirmCancel()} onCancel={() => { setConfirming(false); cancelButtonRef.current?.focus(); }} />
  </>);
}

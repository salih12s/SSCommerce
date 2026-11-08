"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { formatPriceMinor } from "@/lib/format";
import { listOrders, type OrderSummary, type Paged } from "@/features/orders/orders";
import { usePersistedPageshow } from "@/lib/page-restore";
import { loginHref } from "@/lib/safe-next";
import { formatOrderDate, StatusStamp, Thumb } from "./OrderParts";
import { btnLink, btnPrimary, panel } from "@/components/ui/styles";

type State = { kind: "loading" } | { kind: "anonymous" } | { kind: "error"; message: string; badPage: boolean } | { kind: "ready"; data: Paged<OrderSummary> };

/** ?page= güvenilmez girdi: yalnız 1-9999 arası tam sayı kabul edilir, aksi 1. */
export function parsePage(value: string | null): number {
  return value !== null && /^[1-9][0-9]{0,3}$/.test(value) ? Number(value) : 1;
}
const pageHref = (page: number) => (page <= 1 ? "/siparisler" : `/siparisler?page=${page}`);

// Siparişlerim: yalnız oturumdaki kullanıcının siparişleri, istemcide okunur (HTML'de sipariş verisi yoktur).
export function OrdersView() {
  const page = parsePage(useSearchParams().get("page"));
  const [state, setState] = useState<State>({ kind: "loading" });
  const [attempt, setAttempt] = useState(0);
  const headingRef = useRef<HTMLHeadingElement>(null);

  useEffect(() => {
    let alive = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setState({ kind: "loading" });
    void listOrders(page).then((result) => {
      if (!alive) return;
      if (result.ok) setState({ kind: "ready", data: result.data });
      else if (result.status === 401) setState({ kind: "anonymous" });
      else setState({ kind: "error", message: result.message, badPage: result.code === "validation_failed" });
    });
    return () => { alive = false; };
  }, [page, attempt]);

  // bfcache geri yüklemesi: ekrandaki liste önceki kimliğe ait olabilir; temizle ve yeniden oku.
  usePersistedPageshow(() => { setState({ kind: "loading" }); setAttempt((n) => n + 1); });

  let body;
  if (state.kind === "loading") body = <p role="status" className="mt-6" data-testid="orders-loading">Siparişlerin yükleniyor…</p>;
  else if (state.kind === "anonymous") body = <div className={`${panel} mt-6`} data-testid="orders-anonymous"><p className="font-semibold">Siparişlerini görmek için giriş yapmalısın.</p><p className="mt-1 text-fg-muted">Siparişlerin hesabında saklanır.</p><Link href={loginHref(pageHref(page))} className={`${btnPrimary} mt-4`}>Giriş yap</Link></div>;
  else if (state.kind === "error") body = <div role="alert" className={`${panel} mt-6`} data-testid="orders-error"><p className="font-semibold">{state.badPage ? "Bu sayfa bulunamadı." : "Siparişler yüklenemedi."}</p>{!state.badPage && <p className="mt-1 text-fg-muted">{state.message}</p>}{state.badPage ? <Link href="/siparisler" className={`${btnPrimary} mt-4`}>İlk sayfaya dön</Link> : <button type="button" onClick={() => setAttempt((n) => n + 1)} className={`${btnPrimary} mt-4`}>Tekrar dene</button>}</div>;
  else if (state.data.totalCount === 0) body = <div className={`${panel} mt-6`} data-testid="orders-empty"><p className="font-semibold">Henüz siparişin yok.</p><p className="mt-1 text-fg-muted">Sepetindeki ürünleri teslimat adresiyle onayladığında siparişin burada listelenir.</p><Link href="/#urunler" className={`${btnPrimary} mt-4`}>Ürünlere göz at</Link></div>;
  else if (state.data.items.length === 0) body = <div className={`${panel} mt-6`} data-testid="orders-past-end"><p className="font-semibold">Bu sayfada sipariş yok.</p><Link href="/siparisler" className={`${btnPrimary} mt-4`}>İlk sayfaya dön</Link></div>;
  else {
    const { data } = state;
    body = <>
      <ul className="mt-6 space-y-4" data-testid="orders-list">
        {data.items.map((order) => <li key={order.id}><OrderRow order={order} /></li>)}
      </ul>
      {data.totalPages > 1 && <nav aria-label="Sayfalar" className="mt-6 flex flex-wrap items-center justify-between gap-3" data-testid="orders-pager">
        {data.page > 1 ? <Link href={pageHref(data.page - 1)} className={btnLink}>Önceki sayfa</Link> : <span aria-hidden="true" />}
        <p className="text-fg-muted tabular-nums" aria-current="page">Sayfa {data.page} / {data.totalPages}</p>
        {data.page < data.totalPages ? <Link href={pageHref(data.page + 1)} className={btnLink}>Sonraki sayfa</Link> : <span aria-hidden="true" />}
      </nav>}
    </>;
  }
  return <div className="mx-auto max-w-3xl px-4 py-10 sm:px-6 md:py-14">
    <h1 ref={headingRef} tabIndex={-1} className="font-display text-title font-extrabold outline-none">Siparişlerim</h1>
    <p className="mt-2 text-fg-muted">Ödeme alınmaz; siparişler ödenmemiş birer kayıt olarak tutulur.</p>
    {body}
  </div>;
}

// Liste satırı bir fiş gibidir: mono numara + damga, kesik çizgi, ürün küçük görselleri, adet ve ara toplam. Tüm kart tek bağlantıdır.
function OrderRow({ order }: { order: OrderSummary }) {
  const first = order.previewItems[0];
  const hidden = order.itemCount - order.previewItems.reduce((n, i) => n + i.quantity, 0);
  return <article data-testid="order-row" data-status={order.status} className={`${panel} relative transition-shadow duration-150 ease-out hover:shadow-lift`}>
    <div className="flex items-start justify-between gap-3">
      <div className="min-w-0">
        <h2 className="font-mono text-lg font-medium"><Link href={`/siparisler/${order.id}`} data-testid="order-link" className="after:absolute after:inset-0 hover:underline">{order.orderNumber}<span className="sr-only">, {order.itemCount} ürün</span></Link></h2>
        <p className="text-sm text-fg-muted">{formatOrderDate(order.createdAt)}</p>
      </div>
      <StatusStamp status={order.status} />
    </div>
    <div className="my-4 border-t-2 border-dashed border-line" aria-hidden="true" />
    <div className="flex items-center gap-3">
      <span className="flex shrink-0 -space-x-2">{order.previewItems.map((item, i) => <Thumb key={i} url={item.imageUrl} alt={null} size={44} />)}</span>
      <p className="min-w-0 text-sm"><span className="font-semibold break-words">{first?.productName}</span>{order.previewItems.length > 1 || hidden > 0 ? <span className="text-fg-muted"> ve diğer ürünler</span> : null}</p>
    </div>
    <dl className="mt-3 flex flex-wrap items-baseline justify-between gap-x-6 gap-y-1">
      <div className="flex gap-2"><dt className="text-fg-muted">Ürün adedi</dt><dd className="tabular-nums">{order.itemCount}</dd></div>
      <div className="flex gap-2"><dt className="text-fg-muted">Ara toplam</dt><dd className="font-display text-lg font-bold tabular-nums">{formatPriceMinor(order.subtotalMinor)}</dd></div>
    </dl>
  </article>;
}

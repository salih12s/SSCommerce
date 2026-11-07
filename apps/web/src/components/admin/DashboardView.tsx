"use client";

import Link from "next/link";
import { useState, type FormEvent } from "react";
import { aGet, qs } from "@/lib/admin/client";
import { actorKindLabel, formatAge, formatDateTime } from "@/lib/admin/format";
import type { AdminOrderSummary, Dashboard } from "@/lib/admin/types";
import { useRemote } from "@/lib/admin/use-remote";
import { formatPriceMinor } from "@/lib/format";
import { labelOf } from "@/lib/orders";
import { StatusStamp } from "../OrderParts";
import { Chip, DataTable, Empty, Field, Loaded, PageHead, Section, btnSmall, inputClass, type Column } from "./kit";
import { MovementLedger } from "./Movements";

const PIPELINE = ["placed", "processing", "shipped", "completed"] as const;

// Özet: "günlük defter". Sipariş hattı damgalarla, açık siparişlerin ödenmemiş ara toplamı fiş şeridiyle. Hiçbir yerde ciro/gelir/tahsilat yazmaz:
// ödeme kaydı olmadığı için yalnız ödenmemiş siparişlerin kalem ara toplamı vardır.
export function DashboardView() {
  const [threshold, setThreshold] = useState(5);
  const [agedHours, setAgedHours] = useState(48);
  const remote = useRemote(() => aGet<Dashboard>(`/admin/dashboard${qs({ lowStockThreshold: threshold, agedHours })}`), `${threshold}/${agedHours}`);
  return <>
    <PageHead title="Özet" lead="Siparişlerin, stokun ve kataloğun şu anki durumu. Rakamlar veritabanındaki kayıtların sayımıdır." actions={<Filters threshold={threshold} agedHours={agedHours} onApply={(t, h) => { setThreshold(t); setAgedHours(h); }} />} />
    <Loaded remote={remote} label="Özet hazırlanıyor…">{(d) => <Body d={d} />}</Loaded>
  </>;
}

function Filters({ threshold, agedHours, onApply }: { threshold: number; agedHours: number; onApply: (threshold: number, hours: number) => void }) {
  const [t, setT] = useState(String(threshold));
  const [h, setH] = useState(String(agedHours));
  const [error, setError] = useState("");
  function submit(event: FormEvent) {
    event.preventDefault();
    const nt = Number(t), nh = Number(h);
    if (!Number.isInteger(nt) || nt < 1 || nt > 1000) return setError("Stok eşiği 1 ile 1000 arasında olmalı.");
    if (!Number.isInteger(nh) || nh < 1 || nh > 720) return setError("Bekleme süresi 1 ile 720 saat arasında olmalı.");
    setError(""); onApply(nt, nh);
  }
  return <form onSubmit={submit} className="flex flex-wrap items-end gap-3" aria-label="Özet ölçütleri">
    <Field label="Az stok eşiği (adet)" id="esik"><input id="esik" inputMode="numeric" value={t} onChange={(e) => setT(e.target.value)} className={`${inputClass} w-28`} /></Field>
    <Field label="Eski sipariş (saat)" id="saat"><input id="saat" inputMode="numeric" value={h} onChange={(e) => setH(e.target.value)} className={`${inputClass} w-28`} /></Field>
    <button type="submit" className={btnSmall}>Uygula</button>
    {error && <p role="alert" className="basis-full text-sm text-red-800">{error}</p>}
  </form>;
}

function Body({ d }: { d: Dashboard }) {
  // Sayfa açıldığı andaki saat; yaşlar bu ana göre hesaplanır (veri yenilenince bileşen yeniden kurulmaz, ama özet her yenilemede zaten yeni gelir).
  const [now] = useState(() => Date.now());
  const totalOrders = Object.values(d.orders).reduce((a, b) => a + b, 0);
  const empty = totalOrders === 0 && d.catalog.products === 0;
  return <>
    <p className="-mt-2 mb-4 text-sm text-fg-muted">Özet {formatDateTime(d.generatedAt)} itibarıyla hazırlandı.</p>
    {empty && <div className="mb-6"><Empty title="Henüz kayıt yok">Kategori ve ürün ekleyerek başla. Siparişler, hareketler ve stok sayıları kayıt oluştukça burada görünür.
      <span className="mt-3 flex flex-wrap justify-center gap-4"><Link href="/admin/kategoriler" className="font-semibold text-accent-deep underline">Kategoriler</Link><Link href="/admin/urunler/yeni" className="font-semibold text-accent-deep underline">Yeni ürün</Link></span></Empty></div>}

    <Section id="hat" title="Sipariş hattı" note="Alındı, hazırlanıyor, kargoda, tamamlandı sırasıyla ilerler; iptal ayrı sayılır.">
      <ol className="grid grid-cols-2 gap-3 lg:grid-cols-5" aria-label="Duruma göre sipariş sayıları">
        {PIPELINE.map((status) => <li key={status}><Tile status={status} count={d.orders[status]} /></li>)}
        <li className="col-span-2 lg:col-span-1"><Tile status="cancelled" count={d.orders.cancelled} /></li>
      </ol>
    </Section>

    <Section id="acik" title="Açık siparişler" note="Alındı ve hazırlanıyor durumundaki siparişler.">
      <div className="relative rounded-lg border border-line bg-card p-5 shadow-card sm:p-6">
        <div className="flex flex-wrap items-end justify-between gap-x-8 gap-y-3">
          <div>
            <p className="text-sm font-semibold text-fg-muted">Ödenmemiş sipariş ara toplamı</p>
            <p data-testid="unpaid-subtotal" className="font-mono text-3xl font-medium tabular-nums sm:text-4xl">{formatPriceMinor(d.openOrders.unpaidSubtotalMinor)}</p>
          </div>
          <div className="flex flex-wrap items-center gap-2"><Chip tone="warn">Ödeme alınmadı</Chip><Chip>Kargo ve genel toplam yok</Chip></div>
        </div>
        <div aria-hidden="true" className="my-4 border-t-2 border-dashed border-line" />
        <dl className="grid gap-x-8 gap-y-2 sm:grid-cols-3">
          <div><dt className="text-sm text-fg-muted">Açık sipariş</dt><dd data-testid="open-count" className="font-mono text-xl tabular-nums">{d.openOrders.count}</dd></div>
          <div><dt className="text-sm text-fg-muted">{d.openOrders.agedHours} saatten eski</dt><dd data-testid="aged-count" className="font-mono text-xl tabular-nums">{d.openOrders.agedCount}</dd></div>
          <div><dt className="text-sm text-fg-muted">En eski açık sipariş</dt><dd className="text-base">{d.openOrders.oldestCreatedAt ? <>{formatDateTime(d.openOrders.oldestCreatedAt)} <span className="text-fg-muted">({formatAge(d.openOrders.oldestCreatedAt, now)})</span></> : "Yok"}</dd></div>
        </dl>
        <p className="mt-3 text-xs text-fg-muted">Tutar, açık siparişlerin kalem ara toplamlarının toplamıdır. Ödeme kaydı tutulmadığı için tahsil edilmiş bir tutar yoktur.</p>
      </div>
      <div className="mt-4">
        {d.agedOpenOrders.length === 0 ? <Empty title="Bekleyen eski sipariş yok">{d.openOrders.count === 0 ? "Şu anda açık sipariş yok." : `${d.openOrders.agedHours} saati geçen açık sipariş yok.`}</Empty>
          : <><h3 className="mb-2 font-semibold">En eski açık siparişler</h3><AgedTable rows={d.agedOpenOrders} now={now} /></>}
      </div>
    </Section>

    <Section id="stok" title="Stok ve katalog" note={`Satışa açık varyantlar sayılır (ürün, kategori ve varyant aktif; aktif depolar). Az stok eşiği: en çok ${d.stock.threshold} adet.`}>
      <ul className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Count href="/admin/raporlar" label={`Az stok (1–${d.stock.threshold} adet)`} value={d.stock.lowStockVariantCount} testid="low-stock-count" />
        <Count href="/admin/raporlar" label="Tükenen (0 adet)" value={d.stock.outOfStockVariantCount} testid="out-stock-count" />
        <Count href="/admin/urunler" label={`Ürün (${d.catalog.activeProducts} aktif)`} value={d.catalog.products} testid="product-count" />
        <Count href="/admin/urunler" label={`Varyant (${d.catalog.activeVariants} aktif)`} value={d.catalog.variants} testid="variant-count" />
      </ul>
    </Section>

    <div className="mt-2">
      <Section id="hareketler" title="Son stok hareketleri">
        {d.recentMovements.length === 0 ? <Empty title="Henüz stok hareketi yok">Depo girişi yapıldığında burada görünür.</Empty> : <MovementLedger rows={d.recentMovements} caption="Son stok hareketleri" />}
      </Section>
      <Section id="olaylar" title="Son sipariş olayları">
        {d.recentOrderEvents.length === 0 ? <Empty title="Henüz sipariş olayı yok" /> : <DataTable caption="Son sipariş olayları" rows={d.recentOrderEvents} rowKey={(e) => `${e.orderId}-${e.occurredAt}-${e.toStatus}`} columns={[
          { key: "t", header: "Zaman", primary: true, cell: (e) => <time dateTime={e.occurredAt} className="whitespace-nowrap">{formatDateTime(e.occurredAt)}</time> },
          { key: "n", header: "Sipariş", cell: (e) => <Link href={`/admin/siparisler/${e.orderId}`} className="font-mono underline underline-offset-2">{e.orderNumber}</Link> },
          { key: "s", header: "Geçiş", cell: (e) => <span>{e.fromStatus ? labelOf(e.fromStatus) : "Yeni"} → {labelOf(e.toStatus)}</span> },
          { key: "a", header: "Yapan", cell: (e) => actorKindLabel[e.actorKind] ?? e.actorKind },
        ]} />}
      </Section>
    </div>
  </>;
}

function Tile({ status, count }: { status: string; count: number }) {
  return <Link href={`/admin/siparisler?status=${status}`} className="block rounded-lg border border-line bg-card p-4 transition-shadow duration-150 ease-out hover:shadow-lift">
    <StatusStamp status={status} />
    <p data-testid={`count-${status}`} className="mt-3 font-mono text-4xl font-medium tabular-nums">{count}</p>
    <p className="text-sm text-fg-muted">sipariş</p>
  </Link>;
}
function Count({ href, label, value, testid }: { href: string; label: string; value: number; testid: string }) {
  return <li><Link href={href} className="block rounded-lg border border-line bg-card p-4 transition-shadow duration-150 ease-out hover:shadow-lift">
    <span className="block text-sm text-fg-muted">{label}</span><span data-testid={testid} className="mt-1 block font-mono text-3xl font-medium tabular-nums">{value}</span>
  </Link></li>;
}

export function AgedTable({ rows, now }: { rows: AdminOrderSummary[]; now: number }) {
  const columns: Column<AdminOrderSummary>[] = [
    { key: "no", header: "Sipariş", primary: true, cell: (o) => <Link href={`/admin/siparisler/${o.id}`} className="font-mono font-medium underline underline-offset-2">{o.orderNumber}</Link> },
    { key: "st", header: "Durum", cell: (o) => <StatusStamp status={o.status} /> },
    { key: "d", header: "Alındı", cell: (o) => <time dateTime={o.createdAt}>{formatDateTime(o.createdAt)}</time> },
    { key: "age", header: "Yaş", cell: (o) => <span className="whitespace-nowrap">{formatAge(o.createdAt, now)}</span> },
    { key: "pay", header: "Ödeme", cell: () => <Chip tone="warn">Ödeme alınmadı</Chip> },
    { key: "q", header: "Adet", align: "right", cell: (o) => o.itemCount },
    { key: "sub", header: "Ara toplam", align: "right", cell: (o) => <span className="font-mono">{formatPriceMinor(o.subtotalMinor)}</span> },
  ];
  return <DataTable caption="Açık siparişler" rows={rows} columns={columns} rowKey={(o) => o.id} />;
}

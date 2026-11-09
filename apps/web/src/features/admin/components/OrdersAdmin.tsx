"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useState } from "react";
import { aGet, aSend, enc, qs } from "@/features/admin/client";
import { actorKindLabel, formatAge, formatDateTime } from "@/features/admin/format";
import type { AdminOrder, AdminOrderSummary, DeliveryView, Paged } from "@/features/admin/types";
import type { ApiFailure } from "@/lib/api";
import { useRemote } from "@/features/admin/use-remote";
import { formatPriceMinor } from "@/lib/format";
import { labelOf, statusLabel } from "@/features/orders/orders";
import { ConfirmDialog } from "@/components/ui/ConfirmDialog";
import { StatusStamp } from "@/features/orders/components/OrderParts";
import { btnPrimary } from "@/components/ui/styles";
import { useAdmin } from "./AdminContext";
import { btnDanger, btnSmall, DataTable, Empty, Field, FailureBox, Kunye, Loaded, Notice, PageHead, Pager, Section, fieldError, inputClass, pagedOf, textareaClass, type Column } from "./kit";

const PAGE_SIZE = 20;
const FILTERS = ["open", "", "placed", "processing", "shipped", "completed", "cancelled"] as const;
const isOpen = (status: string) => status === "placed" || status === "processing";

/** ?status= güvenilmez girdi: yalnız bilinen değerler kabul edilir. */
function initialFilter(value: string | null): (typeof FILTERS)[number] {
  return (FILTERS as readonly string[]).includes(value ?? "") && value !== null ? (value as (typeof FILTERS)[number]) : "open";
}

// Sipariş listesi (Depo ve Yönetici). Müşteriyi tanıtan hiçbir alan yoktur. "Açık" = alındı + hazırlanıyor, en eski önce.
export function OrdersAdminList() {
  const [filter, setFilter] = useState(initialFilter(useSearchParams().get("status")));
  const [page, setPage] = useState(1);
  const [now] = useState(() => Date.now());
  const query = qs(filter === "open" ? { open: true, page, pageSize: PAGE_SIZE } : { status: filter, page, pageSize: PAGE_SIZE });
  const remote = useRemote(() => aGet<Paged<AdminOrderSummary>>(`/admin/orders${query}`), query);
  const columns: Column<AdminOrderSummary>[] = [
    { key: "no", header: "Sipariş", primary: true, cell: (o) => <Link href={`/admin/siparisler/${o.id}`} className="font-mono font-medium underline underline-offset-2">{o.orderNumber}</Link> },
    { key: "st", header: "Durum", cell: (o) => <StatusStamp status={o.status} /> },
    { key: "d", header: "Alındı", cell: (o) => <time dateTime={o.createdAt} className="whitespace-nowrap">{formatDateTime(o.createdAt)}</time> },
    { key: "age", header: "Yaş", cell: (o) => isOpen(o.status) ? <span className="whitespace-nowrap">{formatAge(o.createdAt, now)}</span> : <span className="text-fg-muted">—</span> },
    { key: "q", header: "Adet", align: "right", cell: (o) => o.itemCount },
    { key: "sub", header: "Ara toplam", align: "right", cell: (o) => <span className="font-mono">{formatPriceMinor(o.subtotalMinor)}</span> },
    { key: "pay", header: "Ödeme", cell: () => <span className="text-fg-muted">Alınmadı</span> },
  ];
  return <>
    <PageHead title="Siparişler" lead="Ödeme alınmaz; siparişler ödenmemiş birer kayıttır. Yaş, siparişin verildiği andan bu yana geçen süredir." />
    <div className="mb-4 max-w-sm">
      <Field label="Göster" id="durum-suzgec">
        <select id="durum-suzgec" value={filter} onChange={(e) => { setFilter(e.target.value as (typeof FILTERS)[number]); setPage(1); }} className={inputClass}>
          <option value="open">Açık siparişler (en eski önce)</option>
          <option value="">Tüm siparişler</option>
          {FILTERS.filter((f) => f !== "" && f !== "open").map((f) => <option key={f} value={f}>{statusLabel[f]}</option>)}
        </select>
      </Field>
    </div>
    <Loaded remote={remote} label="Siparişler yükleniyor…">{(raw) => {
      const data = pagedOf<AdminOrderSummary>({ ok: true, data: raw });
      if (!data || data.items.length === 0) return <Empty title={data && page > 1 ? "Bu sayfada sipariş yok" : "Bu ölçüte uyan sipariş yok"}>{filter === "open" ? "Açık sipariş kalmadı." : "Süzgeci değiştirerek başka siparişlere bak."}</Empty>;
      return <>
        <DataTable caption="Siparişler" rows={data.items} columns={columns} rowKey={(o) => o.id} />
        <Pager page={data.page} totalPages={data.totalPages} totalCount={data.totalCount} onPage={setPage} busy={remote.loading} />
      </>;
    }}</Loaded>
  </>;
}

const FORWARD: Record<string, { to: string; label: string }> = {
  placed: { to: "processing", label: "Hazırlanıyor olarak işaretle" },
  processing: { to: "shipped", label: "Kargoya verildi olarak işaretle" },
  shipped: { to: "completed", label: "Tamamlandı olarak işaretle" },
};

export function OrderAdminDetail({ id }: { id: string }) {
  const remote = useRemote(() => aGet<AdminOrder>(`/admin/orders/${enc(id)}`), id);
  return <>
    <p className="mb-2"><Link href="/admin/siparisler" className="inline-flex min-h-11 items-center text-sm font-semibold text-accent-deep underline underline-offset-2">Tüm siparişler</Link></p>
    <Loaded remote={remote} label="Sipariş yükleniyor…">{(order) => <Detail order={order} reload={remote.reload} />}</Loaded>
    {remote.result && !remote.result.ok && remote.result.status === 404 && <Empty title="Sipariş bulunamadı" />}
  </>;
}

function Detail({ order, reload }: { order: AdminOrder; reload: () => void }) {
  const { isAdmin } = useAdmin();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiFailure | null>(null);
  const [done, setDone] = useState("");
  const [reason, setReason] = useState("");
  const [confirmCancel, setConfirmCancel] = useState(false);
  const [now] = useState(() => Date.now());
  const forward = FORWARD[order.status];
  const canCancel = isAdmin && isOpen(order.status);

  async function change(status: string, why?: string) {
    setBusy(true); setFailure(null); setDone("");
    const result = await aSend<AdminOrder>("POST", `/admin/orders/${enc(order.id)}/status`, why ? { status, reason: why } : { status });
    setBusy(false); setConfirmCancel(false);
    if (result.ok) { setDone(status === "cancelled" ? "Sipariş iptal edildi; ayrılan stok depolara iade edildi." : `Sipariş "${labelOf(status)}" durumuna alındı.`); reload(); return; }
    setFailure(result);
    if (result.code === "order_transition_invalid") reload();
  }
  const reasonError = reason.length > 500 ? "Neden en çok 500 karakter olabilir." : fieldError(failure, "reason");

  const items: Column<AdminOrder["items"][number]>[] = [
    { key: "sku", header: "SKU", primary: true, cell: (i) => <Kunye>{i.sku}</Kunye> },
    { key: "n", header: "Ürün", cell: (i) => <span className="break-words"><span className="font-semibold">{i.productName}</span><span className="block text-fg-muted">{i.variantName}</span></span> },
    { key: "q", header: "Adet", align: "right", cell: (i) => i.quantity },
    { key: "u", header: "Birim fiyat", align: "right", cell: (i) => <span className="font-mono">{formatPriceMinor(i.unitPriceMinor)}</span> },
    { key: "l", header: "Satır toplamı", align: "right", cell: (i) => <span className="font-mono">{formatPriceMinor(i.lineTotalMinor)}</span> },
  ];
  const history = order.statusHistory;
  return <>
    <div className="flex flex-wrap items-start justify-between gap-3 border-b-2 border-dashed border-line pb-4">
      <div className="min-w-0">
        <h1 data-testid="order-number" className="font-mono text-3xl font-medium tracking-tight md:text-4xl">{order.orderNumber}</h1>
        <p className="mt-1 text-fg-muted">Alındı: <time dateTime={order.createdAt}>{formatDateTime(order.createdAt)}</time>{isOpen(order.status) && <> · Yaş: {formatAge(order.createdAt, now)}</>}</p>
      </div>
      <StatusStamp status={order.status} />
    </div>

    <Section id="islem" title="Durum işlemleri">
      {done && <div className="mb-3"><Notice tone="ok">{done}</Notice></div>}
      {failure && <div className="mb-3"><Notice tone="danger" live="alert" title="İşlem yapılamadı">{failure.status === 409 && failure.code === "order_transition_invalid" ? "Siparişin durumu bu sırada değişti; bu geçiş artık yapılamıyor. Güncel durum yüklendi." : failure.message}</Notice></div>}
      {!forward && !canCancel && <p className="text-fg-muted">{order.status === "cancelled" || order.status === "completed" ? "Bu sipariş son durumda; başka işlem yapılamaz." : "Bu durumda sizin yapabileceğiniz bir işlem yok."}</p>}
      <div className="flex flex-wrap gap-3">
        {forward && <button type="button" disabled={busy} onClick={() => void change(forward.to)} data-testid={`to-${forward.to}`} className={btnPrimary}>{busy ? "Kaydediliyor…" : forward.label}</button>}
      </div>
      {!isAdmin && isOpen(order.status) && <p className="mt-3 text-sm text-fg-muted">İptal yalnızca yöneticidedir (iptal stoku depolara iade eder).</p>}
      {canCancel && <div className="mt-5 max-w-xl rounded-lg border border-red-300 bg-red-50/60 p-4">
        <h3 className="font-semibold text-red-950">Siparişi iptal et</h3>
        <p className="mt-1 text-sm text-red-950">İptal edilen siparişin ayrılan stoku depolara geri yazılır. Geri alınamaz.</p>
        <Field label="İptal nedeni" id="iptal-neden" className="mt-3" error={reasonError} hint={`${reason.length}/500`}>
          <textarea id="iptal-neden" rows={2} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} aria-invalid={!!reasonError} aria-describedby={reasonError ? "iptal-neden-error" : "iptal-neden-hint"} className={textareaClass} />
        </Field>
        <button type="button" disabled={busy || reason.trim() === "" || reason.length > 500} onClick={() => setConfirmCancel(true)} data-testid="cancel-open" className={`${btnDanger} mt-3`}>Siparişi iptal et…</button>
      </div>}
    </Section>
    <ConfirmDialog open={confirmCancel} title={`${order.orderNumber} iptal edilsin mi?`} description={`Sipariş iptal edilir ve stok depolara iade edilir. Neden: ${reason.trim()}`} confirmLabel="Evet, iptal et" busyLabel="İptal ediliyor…" busy={busy} onConfirm={() => void change("cancelled", reason.trim())} onCancel={() => setConfirmCancel(false)} />

    <Section id="kalemler" title="Kalemler">
      <DataTable caption="Sipariş kalemleri" rows={order.items} columns={items} rowKey={(i) => i.id} />
      <dl className="mt-3 ml-auto max-w-sm space-y-1.5 border-t-2 border-dashed border-line pt-3">
        <div className="flex justify-between gap-4"><dt>Ödenmemiş ara toplam</dt><dd data-testid="order-subtotal" className="font-mono text-lg font-medium tabular-nums">{formatPriceMinor(order.subtotalMinor)}</dd></div>
        <div className="flex justify-between gap-4 text-fg-muted"><dt>Kargo</dt><dd>{order.shippingMinor === null ? "Hesaplanmadı" : formatPriceMinor(order.shippingMinor)}</dd></div>
        <div className="flex justify-between gap-4 text-fg-muted"><dt>Ödeme</dt><dd>Alınmadı</dd></div>
      </dl>
    </Section>

    <Section id="ayrim" title="Stok ayrımı" note="Siparişin hangi depodan kaç adet düştüğü.">
      {order.allocations && order.allocations.length > 0
        ? <DataTable caption="Stok ayrımı" rows={order.allocations} rowKey={(a) => `${a.sku}-${a.warehouseCode}`} columns={[
          { key: "s", header: "SKU", primary: true, cell: (a) => <Kunye>{a.sku}</Kunye> },
          { key: "w", header: "Depo", cell: (a) => <Kunye>{a.warehouseCode}</Kunye> },
          { key: "q", header: "Adet", align: "right", cell: (a) => a.quantity },
        ]} />
        : <p className="text-fg-muted">Bu sipariş için ayrım kaydı görünmüyor.</p>}
    </Section>

    <Section id="gecmis" title="Durum geçmişi">
      <ol className="relative space-y-0 border-l-2 border-dashed border-line pl-5">
        {history.map((h, i) => <li key={`${h.occurredAt}-${i}`} className="relative pb-4 last:pb-0">
          <span aria-hidden="true" className="absolute -left-[1.62rem] top-1.5 size-2.5 rounded-full bg-ink" />
          <p className="font-semibold">{h.fromStatus ? `${labelOf(h.fromStatus)} → ${labelOf(h.toStatus)}` : `Sipariş verildi (${labelOf(h.toStatus)})`}</p>
          <p className="text-sm text-fg-muted"><time dateTime={h.occurredAt}>{formatDateTime(h.occurredAt)}</time> · {actorKindLabel[h.actorKind] ?? h.actorKind}</p>
          {h.reason && <p className="mt-0.5 break-words text-sm">Neden: {h.reason}</p>}
        </li>)}
      </ol>
    </Section>

    <Section id="teslimat" title="Teslimat bilgisi">
      {isAdmin ? <DeliveryBox orderId={order.id} /> : <p className="text-fg-muted">Teslimat bilgisi yalnızca yöneticiye açıktır.</p>}
    </Section>
  </>;
}

// Teslimat adresi kişisel veridir: yalnız bu bileşenin durumunda tutulur (URL, depo, günlük, liste yok); her görüntüleme sunucuda denetim kaydı bırakır.
function DeliveryBox({ orderId }: { orderId: string }) {
  const [view, setView] = useState<DeliveryView | null>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiFailure | null>(null);
  async function show() {
    setBusy(true); setFailure(null);
    const result = await aSend<DeliveryView>("POST", `/admin/orders/${enc(orderId)}/delivery-view`);
    setBusy(false);
    if (result.ok) setView(result.data); else setFailure(result);
  }
  return <div>
    {!view && <>
      <p className="mb-3 max-w-prose text-sm text-fg-muted">Alıcının adı, telefonu ve adresi kişisel veridir. Göster dediğinde bu görüntüleme denetim kaydına yazılır (kayıtta adres bilgisi tutulmaz).</p>
      <button type="button" onClick={() => void show()} disabled={busy} data-testid="delivery-show" className={btnSmall}>{busy ? "Getiriliyor…" : "Teslimat bilgisini göster"}</button>
    </>}
    {failure && <div className="mt-3"><FailureBox failure={failure} /></div>}
    {view && <div data-testid="delivery-box" className="max-w-lg rounded-lg border border-line bg-card p-4">
      <dl className="grid gap-x-6 gap-y-2 sm:grid-cols-[auto_1fr]">
        <dt className="text-fg-muted">Etiket</dt><dd>{view.label}</dd>
        <dt className="text-fg-muted">Alıcı</dt><dd className="break-words">{view.recipientName}</dd>
        <dt className="text-fg-muted">Telefon</dt><dd className="break-words">{view.phone}</dd>
        <dt className="text-fg-muted">İl / ilçe</dt><dd className="break-words">{view.province} / {view.district}</dd>
        <dt className="text-fg-muted">Adres</dt><dd className="break-words">{view.streetAddress}</dd>
        <dt className="text-fg-muted">Posta kodu</dt><dd>{view.postalCode ?? "—"}</dd>
      </dl>
      <button type="button" onClick={() => setView(null)} className={`${btnSmall} mt-4`}>Gizle</button>
    </div>}
  </div>;
}

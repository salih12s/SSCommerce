"use client";

import { useSearchParams } from "next/navigation";
import { useState, type FormEvent } from "react";
import { aGet, aSend, qs } from "@/lib/admin/client";
import { formatDateTime } from "@/lib/admin/format";
import { clearStockIntent, isStockUnknown, markStockSettled, markStockUnknown, pendingStockIntent, stockIntentFor, type StockIntent, type StockOp } from "@/lib/admin/stock-intent";
import { listOf, type Balance, type Movement, type Paged, type VariantHit, type Warehouse } from "@/lib/admin/types";
import { useRemote, type Remote } from "@/lib/admin/use-remote";
import type { ApiFailure } from "@/lib/api";
import { btnPrimary } from "../ui";
import { useAdmin } from "./AdminContext";
import { btnSmall, Chip, DataTable, describedBy, Empty, Field, FailureBox, fieldError, inputClass, Kunye, Loaded, Notice, PageHead, Pager, Section, textareaClass, pagedOf, type Column } from "./kit";
import { MovementLedger } from "./Movements";

const SEND_TIMEOUT_MS = 15_000;
const MAX_QTY = 1_000_000;
const PAGE_SIZE = 20;

// Stok: varyant ara -> depo bakiyeleri -> giriş/düzeltme -> hareket defteri. Giriş ve düzeltme Idempotency-Key ile gider (lib/admin/stock-intent.ts):
// yanıt kaybolursa yeni anahtar üretilmez; yalnız aynı anahtar ve içerikle yeniden gönderilir ya da defter yenilenir.
export function StockAdmin() {
  const { account } = useAdmin();
  const initialQuery = useSearchParams().get("q") ?? "";
  const [text, setText] = useState(initialQuery);
  const [applied, setApplied] = useState(initialQuery);
  const [selected, setSelected] = useState<VariantHit | null>(null);
  const [hitPage, setHitPage] = useState(1);
  const [ledgerKey, setLedgerKey] = useState(0);
  const [gate, setGate] = useState<StockIntent | null>(() => pendingStockIntent(account.id));
  const [notice, setNotice] = useState("");

  const warehouses = useRemote(() => aGet<Warehouse[] | { items: Warehouse[] }>("/admin/warehouses"), "depolar");
  const hitsQuery = qs({ q: applied, page: hitPage, pageSize: 10 });
  const hits = useRemote(() => aGet<Paged<VariantHit>>(`/admin/variants${hitsQuery}`), hitsQuery);
  const bump = () => setLedgerKey((n) => n + 1);

  function search(event: FormEvent) {
    event.preventDefault();
    setApplied(text.trim()); setHitPage(1); setSelected(null);
  }
  const hitColumns: Column<VariantHit>[] = [
    { key: "sku", header: "SKU", primary: true, cell: (v) => <Kunye>{v.sku}</Kunye> },
    { key: "p", header: "Ürün", cell: (v) => <span className="break-words"><span className="font-semibold">{v.productName}</span><span className="block text-fg-muted">{v.displayName}</span></span> },
    { key: "st", header: "Durum", cell: (v) => <Chip tone={v.isActive && v.productIsActive ? "ok" : "neutral"}>{v.isActive && v.productIsActive ? "Satışta" : "Pasif"}</Chip> },
    { key: "on", header: "Toplam stok", align: "right", cell: (v) => <span className="font-mono">{v.onHandTotal}</span> },
    { key: "act", header: "İşlem", cell: (v) => <button aria-label={`${selected?.id === v.id ? "Seçili" : "Seç"}: ${v.sku}`} type="button" className={btnSmall} aria-pressed={selected?.id === v.id} onClick={() => setSelected(v)}>{selected?.id === v.id ? "Seçili" : "Seç"}</button> },
  ];

  return <>
    <PageHead title="Stok" lead="Depo girişi ve stok düzeltmesi yapılır; her işlem hareket defterine yazılır ve silinemez." />
    {notice && <div className="mb-4"><Notice tone="ok">{notice}</Notice></div>}
    {gate && <UnknownGate intent={gate} onResolved={(message) => { setGate(null); setNotice(message); bump(); }} onUnknown={setGate} onCheck={bump} />}

    <Section id="ara" title="Varyant ara" note="SKU, ürün adı ya da varyant adıyla ara; boş bırakırsan tüm varyantlar listelenir.">
      <form onSubmit={search} role="search" className="mb-3 flex max-w-2xl flex-wrap items-end gap-3">
        <Field label="Arama" id="varyant-ara" className="min-w-0 flex-1 basis-64"><input id="varyant-ara" type="search" value={text} onChange={(e) => setText(e.target.value)} maxLength={100} autoComplete="off" className={inputClass} /></Field>
        <button type="submit" className={btnPrimary}>Ara</button>
      </form>
      <Loaded remote={hits} label="Varyantlar aranıyor…">{(raw) => {
        const data = pagedOf<VariantHit>({ ok: true, data: raw });
        if (!data || data.items.length === 0) return <Empty title="Varyant bulunamadı">{applied ? "Aramayı değiştirip tekrar dene." : "Önce bir ürün ve varyant oluştur."}</Empty>;
        return <><DataTable caption="Varyantlar" rows={data.items} columns={hitColumns} rowKey={(v) => v.id} /><Pager page={data.page} totalPages={data.totalPages} totalCount={data.totalCount} onPage={setHitPage} busy={hits.loading} /></>;
      }}</Loaded>
    </Section>

    {selected ? <SelectedVariant key={selected.id} variant={selected} warehouses={warehouses} gate={gate} account={account.id} ledgerKey={ledgerKey}
      onApplied={(message) => { setNotice(message); bump(); hits.reload(); }} onGate={setGate} />
      : <Section id="defter" title="Son stok hareketleri" note="Bir varyant seçersen yalnız onun hareketleri görünür."><Ledger variantId={null} reloadKey={ledgerKey} /></Section>}
  </>;
}

function SelectedVariant({ variant, warehouses, gate, account, ledgerKey, onApplied, onGate }: {
  variant: VariantHit; warehouses: Remote<Warehouse[] | { items: Warehouse[] }>; gate: StockIntent | null; account: string; ledgerKey: number;
  onApplied: (message: string) => void; onGate: (intent: StockIntent) => void;
}) {
  const balances = useRemote(() => aGet<Paged<Balance> | Balance[]>(`/admin/inventory/balances${qs({ variantId: variant.id, pageSize: 100 })}`), `${variant.id}-${ledgerKey}`);
  const sellable = variant.isActive && variant.productIsActive;
  return <>
    <Section id="bakiye" title={`Bakiyeler: ${variant.sku}`} note={`${variant.productName} · ${variant.displayName}`}>
      {!sellable && <div className="mb-3"><Notice tone="warn" live="off">Bu varyant ya da ürünü pasif. Stok girişi/düzeltmesi sunucu tarafından reddedilebilir.</Notice></div>}
      <Loaded remote={balances} label="Bakiyeler yükleniyor…">{(raw) => {
        const rows = listOf(raw);
        return rows.length === 0 ? <Empty title="Bu varyantın hiçbir depoda bakiyesi yok">İlk depo girişini aşağıdan yapabilirsin.</Empty> : <DataTable caption="Depo bakiyeleri" rows={rows} rowKey={(b) => b.warehouseId} columns={[
          { key: "w", header: "Depo", primary: true, cell: (b) => <Kunye>{b.warehouseCode}</Kunye> },
          { key: "q", header: "Eldeki adet", align: "right", cell: (b) => <span className="font-mono font-medium">{b.onHandQuantity}</span> },
          { key: "u", header: "Güncellendi", cell: (b) => <time dateTime={b.updatedAt} className="whitespace-nowrap text-fg-muted">{formatDateTime(b.updatedAt)}</time> },
        ]} />;
      }}</Loaded>
    </Section>
    <Section id="islemler" title="Stok işlemi">
      <Loaded remote={warehouses} label="Depolar yükleniyor…">{(raw) => {
        const active = listOf(raw).filter((w) => w.isActive);
        if (active.length === 0) return <Empty title="Aktif depo yok">Stok işlemi için önce Depolar sayfasından bir depo oluştur.</Empty>;
        return <div className="grid gap-5 lg:grid-cols-2">
          <StockForm op="receive" variant={variant} warehouses={active} account={account} locked={gate !== null} onApplied={onApplied} onGate={onGate} />
          <StockForm op="adjust" variant={variant} warehouses={active} account={account} locked={gate !== null} onApplied={onApplied} onGate={onGate} />
        </div>;
      }}</Loaded>
    </Section>
    <Section id="defter" title={`Hareket defteri: ${variant.sku}`}><Ledger variantId={variant.id} reloadKey={ledgerKey} /></Section>
  </>;
}

function Ledger({ variantId, reloadKey }: { variantId: string | null; reloadKey: number }) {
  const [page, setPage] = useState(1);
  const query = qs({ variantId, page, pageSize: PAGE_SIZE });
  const remote = useRemote(() => aGet<Paged<Movement>>(`/admin/inventory/movements${query}`), `${query}-${reloadKey}`);
  return <Loaded remote={remote} label="Hareketler yükleniyor…">{(raw) => {
    const data = pagedOf<Movement>({ ok: true, data: raw });
    if (!data || data.items.length === 0) return <Empty title="Henüz stok hareketi yok">Depo girişi yapıldığında burada görünür.</Empty>;
    return <><MovementLedger rows={data.items} hideSku={variantId !== null} /><Pager page={data.page} totalPages={data.totalPages} totalCount={data.totalCount} onPage={setPage} busy={remote.loading} /></>;
  }}</Loaded>;
}

/** İstek gövdesi: alan sırası sabittir ki aynı niyet aynı metne çıksın. */
function receiveBody(warehouseId: string, variantId: string, quantity: number, reason: string, reference: string) {
  return { warehouseId, variantId, quantity, reason, ...(reference ? { reference } : {}) };
}
function adjustBody(warehouseId: string, variantId: string, quantityDelta: number, type: string, reason: string, reference: string) {
  return { warehouseId, variantId, quantityDelta, type, reason, ...(reference ? { reference } : {}) };
}
const endpointOf = (op: StockOp) => (op === "receive" ? "/admin/inventory/receive" : "/admin/inventory/adjust");

function StockForm({ op, variant, warehouses, account, locked, onApplied, onGate }: {
  op: StockOp; variant: VariantHit; warehouses: Warehouse[]; account: string; locked: boolean; onApplied: (message: string) => void; onGate: (intent: StockIntent) => void;
}) {
  const receive = op === "receive";
  const [warehouseId, setWarehouseId] = useState(warehouses[0]?.id ?? "");
  const [amount, setAmount] = useState("");
  const [type, setType] = useState("adjustment");
  const [reason, setReason] = useState("");
  const [reference, setReference] = useState("");
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiFailure | null>(null);
  const [local, setLocal] = useState<Record<string, string>>({});
  const id = receive ? "giris" : "duzeltme";

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (locked || busy) return;
    setFailure(null);
    const errors: Record<string, string> = {};
    const n = Number(amount.trim());
    if (amount.trim() === "" || !Number.isInteger(n)) errors.amount = "Tam sayı gir.";
    else if (receive && (n < 1 || n > MAX_QTY)) errors.amount = `Adet 1 ile ${MAX_QTY.toLocaleString("tr-TR")} arasında olmalı.`;
    else if (!receive && (n === 0 || Math.abs(n) > MAX_QTY)) errors.amount = `Değişim 0 olamaz ve en çok ${MAX_QTY.toLocaleString("tr-TR")} olabilir (eksi sayı stok düşürür).`;
    if (!warehouseId) errors.warehouse = "Depo seç.";
    if (reason.trim() === "") errors.reason = "Neden yaz."; else if (reason.trim().length > 500) errors.reason = "Neden en çok 500 karakter olabilir.";
    if (reference.trim().length > 128) errors.reference = "Referans en çok 128 karakter olabilir.";
    setLocal(errors);
    if (Object.keys(errors).length > 0) return;
    const body = receive ? receiveBody(warehouseId, variant.id, n, reason.trim(), reference.trim()) : adjustBody(warehouseId, variant.id, n, type, reason.trim(), reference.trim());
    const code = warehouses.find((w) => w.id === warehouseId)?.code ?? "";
    const label = `${receive ? "Mal girişi" : "Düzeltme"} · ${variant.sku} · ${code} · ${n > 0 && !receive ? "+" : ""}${n}`;
    const intent = stockIntentFor(account, op, JSON.stringify(body), label);
    await run(intent, label);
  }
  async function run(intent: StockIntent, label: string) {
    setBusy(true);
    markStockUnknown(intent); // yanıt gelmeden sayfa kapanırsa bile niyet "bilinmiyor" kalır
    const result = await aSend<Movement>("POST", endpointOf(op), JSON.parse(intent.payload), { headers: { "Idempotency-Key": intent.key }, timeoutMs: SEND_TIMEOUT_MS });
    setBusy(false);
    if (result.ok) {
      clearStockIntent();
      setAmount(""); setReason(""); setReference(""); setLocal({});
      onApplied(result.data.replayed ? `${label}: bu işlem daha önce uygulanmıştı, tekrar yazılmadı. Bakiye ${result.data.onHandAfter}.` : `${label} kaydedildi. Yeni bakiye: ${result.data.onHandAfter}.`);
      return;
    }
    if (isStockUnknown(result)) { onGate({ ...intent, unknown: true }); return; }
    markStockSettled(intent);
    setFailure(result);
  }
  const err = (field: string, key = field) => local[field] ?? fieldError(failure, key);
  const amountKey = receive ? "quantity" : "quantityDelta";
  // `stock_limit_exceeded` genel iletisi sipariş iptalindeki stok iadesini anlatır; burada anlamı farklıdır (bakiye üst sınırı).
  const general = failure && Object.keys(failure.errors).length === 0
    ? (failure.code === "stock_limit_exceeded" ? { ...failure, message: `Bakiye üst sınırı (${MAX_QTY.toLocaleString("tr-TR")} adet) aşılamaz; işlem uygulanmadı, bakiye değişmedi.` } : failure)
    : null;
  return <form onSubmit={(e) => void submit(e)} noValidate aria-busy={busy} className="rounded-lg border border-line bg-card p-4 sm:p-5">
    <h3 className="font-display text-lg font-bold">{receive ? "Depo girişi" : "Stok düzeltmesi"}</h3>
    <p className="mb-3 text-sm text-fg-muted">{receive ? "Depoya yeni gelen adet. Bakiyeyi artırır." : "Sayım farkı ya da hata düzeltmesi. Eksi sayı stok düşürür; bakiye sıfırın altına inemez."}</p>
    <div className="space-y-3">
      <Field label="Depo" id={`${id}-depo`} error={err("warehouse", "warehouseId")}>
        <select id={`${id}-depo`} value={warehouseId} disabled={locked} onChange={(e) => setWarehouseId(e.target.value)} aria-invalid={!!err("warehouse", "warehouseId")} className={inputClass}>{warehouses.map((w) => <option key={w.id} value={w.id}>{w.code} · {w.name}</option>)}</select>
      </Field>
      {!receive && <Field label="Düzeltme türü" id={`${id}-tur`} error={fieldError(failure, "type")}>
        <select id={`${id}-tur`} value={type} disabled={locked} onChange={(e) => setType(e.target.value)} className={inputClass}><option value="adjustment">Düzeltme</option><option value="correction">Hata düzeltmesi</option></select>
      </Field>}
      <Field label={receive ? "Adet" : "Adet değişimi"} id={`${id}-adet`} error={err("amount", amountKey)} hint={receive ? undefined : "Örn. 5 ya da -3"}>
        <input id={`${id}-adet`} inputMode={receive ? "numeric" : "text"} value={amount} disabled={locked} onChange={(e) => setAmount(e.target.value)} aria-invalid={!!err("amount", amountKey)} aria-describedby={describedBy(`${id}-adet`, !receive, !!err("amount", amountKey))} className={`${inputClass} font-mono`} />
      </Field>
      <Field label="Neden" id={`${id}-neden`} error={err("reason")} hint={`${reason.length}/500`}>
        <textarea id={`${id}-neden`} rows={2} maxLength={500} value={reason} disabled={locked} onChange={(e) => setReason(e.target.value)} aria-invalid={!!err("reason")} aria-describedby={describedBy(`${id}-neden`, true, !!err("reason"))} className={textareaClass} />
      </Field>
      <Field label="Referans (isteğe bağlı)" id={`${id}-ref`} error={err("reference")} hint="İrsaliye ya da sayım numarası">
        <input id={`${id}-ref`} value={reference} maxLength={128} disabled={locked} onChange={(e) => setReference(e.target.value)} aria-invalid={!!err("reference")} aria-describedby={describedBy(`${id}-ref`, true, !!err("reference"))} className={inputClass} />
      </Field>
    </div>
    <div aria-live="polite">{general && <div className="mt-3"><FailureBox failure={general} /></div>}</div>
    <button type="submit" disabled={busy || locked} data-testid={`stock-${op}`} className={`${btnPrimary} mt-4`}>{busy ? "Kaydediliyor…" : receive ? "Girişi kaydet" : "Düzeltmeyi kaydet"}</button>
  </form>;
}

// Yanıt kaybolduysa işlem uygulanmış olabilir. "Başarısız oldu" denmez; yeni işlem kapalıdır; tek yol AYNI anahtar ve içerikle yeniden göndermek ya da defteri incelemektir.
function UnknownGate({ intent, onResolved, onUnknown, onCheck }: { intent: StockIntent; onResolved: (message: string) => void; onUnknown: (intent: StockIntent) => void; onCheck: () => void }) {
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<ApiFailure | null>(null);
  async function resend() {
    setBusy(true); setFailure(null);
    markStockUnknown(intent);
    const result = await aSend<Movement>("POST", endpointOf(intent.op), JSON.parse(intent.payload), { headers: { "Idempotency-Key": intent.key }, timeoutMs: SEND_TIMEOUT_MS });
    setBusy(false);
    if (result.ok) { clearStockIntent(); onResolved(result.data.replayed ? `${intent.label}: işlem zaten uygulanmıştı; tekrar yazılmadı. Bakiye ${result.data.onHandAfter}.` : `${intent.label} kaydedildi. Yeni bakiye: ${result.data.onHandAfter}.`); return; }
    if (isStockUnknown(result)) { onUnknown({ ...intent, unknown: true }); setFailure(result); return; }
    // Kesin red: işlem uygulanmadı. Kapı kapanır; kullanıcı formu yeniden doldurabilir.
    markStockSettled(intent); clearStockIntent();
    onResolved(`${intent.label} uygulanmadı: ${result.message}`);
  }
  return <div className="mb-6" data-testid="stock-unknown"><Notice tone="warn" live="alert" title="Önceki stok işleminin sonucu bilinmiyor"
    action={<><button type="button" onClick={() => void resend()} disabled={busy} className={btnPrimary} data-testid="stock-resend">{busy ? "Kontrol ediliyor…" : "Aynı işlemi yeniden gönder"}</button><button type="button" onClick={onCheck} disabled={busy} className={btnSmall}>Hareket defterini yenile</button></>}>
    <p><strong>{intent.label}</strong></p>
    <p className="mt-1">İsteği gönderdik ama yanıtı alamadık; işlem uygulanmış olabilir. Aynı işlemi yeniden göndermek ikinci bir hareket yazmaz: uygulandıysa mevcut hareket döner, uygulanmadıysa şimdi uygulanır. Sonuç netleşene kadar yeni stok işlemi kapalıdır; defteri yenileyip hareketin görünüp görünmediğine bakabilirsin.</p>
    {failure && <p className="mt-1">Son deneme de sonuçlanmadı: {failure.message}</p>}
  </Notice></div>;
}

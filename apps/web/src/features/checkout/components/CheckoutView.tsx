"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { checkCsrf } from "@/lib/api";
import { listAddresses, type Address } from "@/features/account/addresses";
import { fetchAccount } from "@/features/auth/auth-client";
import { loadCart, previewCheckout, type CheckoutIssue, type CheckoutPreview } from "@/features/cart/cart";
import { formatPriceMinor } from "@/lib/format";
import { clearIntent, intentFor, markSettled, markUnknown, pendingIntent, type OrderIntent } from "@/features/checkout/order-key";
import { isUnknownOutcome, markJustPlaced, placeOrder } from "@/features/orders/orders";
import { usePersistedPageshow } from "@/lib/page-restore";
import { loginHref } from "@/lib/safe-next";
import { AddressCard } from "@/features/account/components/AddressManager";
import { AddressForm } from "@/features/account/components/AddressForm";
import { statusNote } from "@/features/cart/components/CartView";
import { btnLink, btnPrimary, btnSecondary, panel } from "@/components/ui/styles";

type Load = { kind: "loading" } | { kind: "anonymous" } | { kind: "error"; message: string } | { kind: "ready"; items: Address[] };
// csrf: sipariş düğmesi açılmadan önce güvenlik belirtecinin alınabildiği doğrulanır (her yazma yine kendi taze belirtecini alır).
type Preview = { kind: "idle" } | { kind: "loading" } | { kind: "empty" } | { kind: "error"; message: string } | { kind: "ready"; data: CheckoutPreview; csrf: boolean };
// Bilinen (sunucunun açıkça söylediği) sipariş hataları. Sonucu BİLİNMEYEN durum bundan ayrıdır: `unknown` kapısı.
type Problem = { title: string; text: string; lines?: string[]; details?: string[]; diff?: boolean; link?: "sepet" | "login" | "siparisler" | "reload" };

const issueText: Record<CheckoutIssue["code"], string> = {
  out_of_stock: "Stokta görünmeyen satır var.",
  insufficient_stock: "İstenen adet için yeterli stok görünmeyen satır var.",
  unavailable: "Artık satışta olmayan satır var.",
  price_changed: "Fiyatı değişen satır var.",
};

// Quote değişince "ne değişti" açıklaması: iki sunucu önizlemesinin yan yana okunması; tutar hesaplanmaz.
function describeChanges(before: CheckoutPreview, after: CheckoutPreview): string[] {
  const out: string[] = [];
  for (const line of after.lines) {
    const old = before.lines.find((l) => l.variantId === line.variantId);
    if (!old) { out.push(`${line.productName}, ${line.variantName}: sepete eklenmiş.`); continue; }
    if (old.unitPriceMinor !== line.unitPriceMinor && old.unitPriceMinor !== null && line.unitPriceMinor !== null) out.push(`${line.productName}, ${line.variantName}: birim fiyat ${formatPriceMinor(old.unitPriceMinor)} iken ${formatPriceMinor(line.unitPriceMinor)} oldu.`);
    if (old.quantity !== line.quantity) out.push(`${line.productName}, ${line.variantName}: adet ${old.quantity} iken ${line.quantity} oldu.`);
  }
  for (const old of before.lines) if (!after.lines.some((l) => l.variantId === old.variantId)) out.push(`${old.productName}, ${old.variantName}: sepetten çıkmış.`);
  if (before.subtotalMinor !== after.subtotalMinor) out.push(`Ara toplam ${formatPriceMinor(before.subtotalMinor)} iken ${formatPriceMinor(after.subtotalMinor)} oldu.`);
  return out.length > 0 ? out : ["Sepetin içeriği ya da fiyatlar değişti."];
}

// Sipariş öncesi son ekran: sepet ve adres sunucuda birleştirilir, tutarlar API'nin kuruş değerlerinden biçimlenir (istemci fiyat hesaplamaz).
// "Sipariş oluştur" ödeme ALMAZ; sipariş ödenmemiş bir kayıttır ve kargo/genel toplam yoktur. Stok yalnız sipariş anında düşer.
export function CheckoutView() {
  const router = useRouter();
  const [load, setLoad] = useState<Load>({ kind: "loading" });
  const [userId, setUserId] = useState<string | null>(null);
  const [selected, setSelected] = useState("");
  const [adding, setAdding] = useState(false);
  const [preview, setPreview] = useState<Preview>({ kind: "idle" });
  const [attempt, setAttempt] = useState(0);
  const [sending, setSending] = useState(false);
  const [problem, setProblem] = useState<Problem | null>(null);
  const [unknown, setUnknown] = useState<OrderIntent | null>(null);
  const [waitUntil, setWaitUntil] = useState(0);
  const [now, setNow] = useState(0);
  // Eşzamanlı çift tıklama/Enter yarışı: state güncellemesi asenkron olduğundan senkron kilit ref'tedir.
  const submitting = useRef(false);
  const before = useRef<CheckoutPreview | null>(null);

  const apply = (result: Awaited<ReturnType<typeof listAddresses>>, select?: string) => {
    if (!result.ok) { setLoad(result.status === 401 ? { kind: "anonymous" } : { kind: "error", message: result.message }); return; }
    setLoad({ kind: "ready", items: result.data });
    setSelected((current) => select ?? (result.data.some((a) => a.id === current) ? current : result.data[0]?.id ?? ""));
  };
  const refresh = async (select?: string) => apply(await listAddresses(), select);
  // Hesap kimliği anahtarı kullanıcıya bağlamak için okunur; sonucu çözülmemiş bir deneme varsa kapı açılır.
  const bootstrap = () => Promise.all([fetchAccount(), listAddresses()]).then(([account, list]) => {
    if (!account.ok) { setLoad(account.status === 401 ? { kind: "anonymous" } : { kind: "error", message: account.message }); return; }
    setUserId(account.data.id);
    setUnknown(pendingIntent(account.data.id));
    apply(list);
  });
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { void bootstrap(); }, []);

  // bfcache geri yüklemesi: ekrandaki adres, özet ve sipariş durumu önceki kimliğe ait olabilir; temizle ve yeniden oku.
  usePersistedPageshow(() => { setLoad({ kind: "loading" }); setUserId(null); setSelected(""); setPreview({ kind: "idle" }); setProblem(null); setUnknown(null); submitting.current = false; setSending(false); void bootstrap(); });

  // Seçim değiştikçe sunucudan özet istenir; eski istek geç dönerse yok sayılır.
  useEffect(() => {
    if (!selected) return;
    let alive = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setPreview({ kind: "loading" });
    void (async () => {
      const result = await previewCheckout(selected);
      if (!alive) return;
      if (result.ok) {
        const proceed = result.data.canProceed && result.data.quote !== null;
        const csrf = proceed ? (await checkCsrf()).ok : false;
        if (!alive) return;
        setPreview({ kind: "ready", data: result.data, csrf });
        // quote değişikliğinden sonra yenilenen özet "ne değişti"yi açıklar.
        const old = before.current; before.current = null;
        if (old) setProblem((p) => (p?.diff ? { ...p, details: describeChanges(old, result.data) } : p));
      } else if (result.status === 401) setLoad({ kind: "anonymous" });
      else if (result.code === "cart_empty") setPreview({ kind: "empty" });
      else if (result.code === "address_not_found") { setPreview({ kind: "idle" }); void listAddresses().then((list) => apply(list)); }
      else setPreview({ kind: "error", message: result.message });
    })();
    return () => { alive = false; };
  }, [selected, attempt]);

  // 429: Retry-After dolana kadar düğme kapalı; sayaç yalnız bekleme sürerken çalışır.
  useEffect(() => {
    if (waitUntil === 0) return;
    const timer = setInterval(() => { const t = Date.now(); setNow(t); if (t >= waitUntil) { setWaitUntil(0); clearInterval(timer); } }, 500);
    return () => clearInterval(timer);
  }, [waitUntil]);
  const waitSeconds = waitUntil > 0 ? Math.max(1, Math.ceil((waitUntil - now) / 1000)) : 0;

  const lines = preview.kind === "ready" ? preview.data.lines : [];
  const nameOf = (ids: unknown) => (Array.isArray(ids) ? ids : []).map((id) => lines.find((l) => l.id === id)).filter((l) => l !== undefined).map((l) => `${l.productName}, ${l.variantName}`);

  // Sipariş verme. Anahtar önce "sonucu belirsiz" diye kaydedilir: sayfa istek uçuştayken yenilenirse kapı yeniden açılır ve aynı anahtar kullanılır.
  async function place(intent: OrderIntent) {
    if (submitting.current) return;
    submitting.current = true; setSending(true); setProblem(null); setWaitUntil(0);
    markUnknown(intent);
    const result = await placeOrder(intent.addressId, intent.quote, intent.key);
    if (result.ok) {
      clearIntent(); setUnknown(null); markJustPlaced(result.data.id, result.data.replayed);
      void loadCart(true); // sepet sunucuda boşaldı: üst sayaç güncellenir
      router.push(`/siparisler/${encodeURIComponent(result.data.id)}`);
      return; // gezinme sürerken düğme kapalı kalır
    }
    submitting.current = false; setSending(false);
    if (isUnknownOutcome(result)) { setUnknown({ ...intent, unknown: true }); return; }
    markSettled(intent); setUnknown(null);
    const reload = () => { if (preview.kind === "ready") before.current = preview.data; setAttempt((n) => n + 1); };
    if (result.status === 0) return setProblem({ title: "Sunucuya bağlanılamadı", text: "Sipariş gönderilmedi. Bağlantını kontrol edip tekrar dene; aynı sipariş denemesi kullanılır." });
    if (result.status === 401) return setProblem({ title: "Oturumun sona erdi", text: "Sipariş verilmedi. Giriş yaptıktan sonra bu sayfaya dönersin; sepetin ve adreslerin yerinde.", link: "login" });
    if (result.status === 403) return setProblem({ title: "Güvenlik doğrulaması geçersiz", text: "Oturumun ya da güvenlik belirtecin geçerli değil. Sipariş verilmedi; sayfayı yenileyip tekrar dene.", link: "reload" });
    // Kullanıcı başına sipariş sınırı (sunucu kuralı): otomatik yeniden deneme yok, yeni anahtar üretilmez; aynı anahtar korunur ve kullanıcıya ne yapacağı söylenir.
    if (result.status === 429 && result.code === "order_daily_limit") {
      const limit = typeof result.extra.limit === "number" ? result.extra.limit : null;
      return setProblem({ title: "Günlük sipariş sınırına ulaştın", text: `Sipariş verilmedi. Son 24 saatte ${limit ? `en çok ${limit} sipariş verebilirsin` : "verebileceğin sipariş sayısına ulaştın"}. ${result.retryAfter ? `Yaklaşık ${formatWait(result.retryAfter)} sonra yeniden deneyebilirsin.` : "Daha sonra yeniden dene."} Sepetin ve adreslerin yerinde.`, link: "siparisler" });
    }
    if (result.status === 429) { const t = Date.now(); setNow(t); setWaitUntil(t + (result.retryAfter ?? 10) * 1000); return setProblem({ title: "Çok fazla istek", text: "Sipariş verilmedi. Kısa bir süre bekleyip tekrar dene." }); }
    switch (result.code) {
      case "order_quote_changed": reload(); return setProblem({ title: "Fiyatlar ya da sepet değişti", text: "Sipariş verilmedi. Güncel özeti incele ve siparişi yeniden onayla.", details: [], diff: true });
      case "insufficient_stock": reload(); return setProblem({ title: "Bazı ürünler için yeterli stok yok", text: "Sipariş verilmedi, sepetin değişmedi. Adedi azaltmak ya da satırı kaldırmak için sepete dön.", lines: nameOf(result.extra.itemIds), link: "sepet" });
      case "order_item_inactive": reload(); return setProblem({ title: "Bazı ürünler artık satışta değil", text: "Sipariş verilmedi, sepetin değişmedi. Bu satırları kaldırmak için sepete dön.", lines: nameOf(result.extra.itemIds), link: "sepet" });
      case "cart_empty": setPreview({ kind: "empty" }); return setProblem({ title: "Sepetin boş, sipariş oluşmadı", text: "Bu deneme için bir sipariş bulunamadı. Başka bir sekmede sipariş verdiysen Siparişlerim'de görünür.", link: "siparisler" });
      case "address_not_found": void refresh(); reload(); return setProblem({ title: "Teslimat adresi bulunamadı", text: "Sipariş verilmedi. Adres listesi yenilendi; bir adres seçip tekrar dene." });
      case "idempotency_key_reused": clearIntent(); reload(); return setProblem({ title: "Sipariş denemesi yenilendi", text: "Bu deneme başka bir içerikle kullanılmıştı. Sipariş verilmedi; özet yenilendi, siparişi yeniden onayla." });
      case "open_order_limit": {
        const limit = typeof result.extra.limit === "number" ? result.extra.limit : null;
        return setProblem({ title: "Açık sipariş sınırına ulaştın", text: `Sipariş verilmedi. ${limit ? `Aynı anda en çok ${limit} ödenmemiş açık siparişin olabilir.` : "Aynı anda açık tutabileceğin sipariş sayısına ulaştın."} Siparişlerim sayfasında birini iptal et ya da hazırlanmasını bekle, sonra tekrar dene. Sepetin yerinde.`, link: "siparisler" });
      }
      case "cart_total_too_large": return setProblem({ title: "Sepet toplamı çok yüksek", text: "Sipariş verilmedi. Sepetteki ürünleri azaltıp tekrar dene.", link: "sepet" });
      case "busy": return setProblem({ title: "Sistem şu an meşgul", text: "Sipariş verilmedi. Birkaç saniye sonra tekrar dene; aynı sipariş denemesi kullanılır." });
      default: return setProblem({ title: "Sipariş verilemedi", text: result.message });
    }
  }
  const order = (previewData: CheckoutPreview) => {
    if (!userId || previewData.quote === null || submitting.current) return;
    void place(intentFor(userId, selected, previewData.quote));
  };

  const heading = <h1 className="font-display text-title font-extrabold">Teslimat ve özet</h1>;
  const wrap = (content: React.ReactNode) => <div className="mx-auto max-w-5xl px-4 py-10 sm:px-6 md:py-14">{heading}{content}</div>;
  if (load.kind === "loading") return wrap(<p role="status" className="mt-6">Adresler yükleniyor…</p>);
  if (load.kind === "anonymous") return wrap(<div className={`${panel} mt-6`} data-testid="checkout-anonymous"><p className="font-semibold">Devam etmek için giriş yapmalısın.</p><p className="mt-1 text-fg-muted">Sepetin ve adreslerin hesabında saklanır.</p><Link href={loginHref("/checkout")} className={`${btnPrimary} mt-4`}>Giriş yap</Link></div>);
  if (load.kind === "error") return wrap(<div role="alert" className={`${panel} mt-6`}><p className="font-semibold">Adresler yüklenemedi.</p><p className="mt-1 text-fg-muted">{load.message}</p><button type="button" onClick={() => { setLoad({ kind: "loading" }); void bootstrap(); }} className={`${btnPrimary} mt-4`}>Tekrar dene</button></div>);

  const addresses = load.items;
  const showForm = addresses.length === 0 || adding;
  const canOrder = preview.kind === "ready" && preview.data.canProceed && preview.data.quote !== null && preview.csrf && userId !== null && !sending && waitSeconds === 0;
  return wrap(<div className="mt-6 grid gap-6 lg:grid-cols-[1fr_24rem] lg:items-start">
    <div className="space-y-6">
      {addresses.length > 0 && <div className={panel}><fieldset className="m-0 min-w-0 border-0 p-0">
        <legend className="mb-3 p-0 font-display text-xl font-bold">Teslimat adresi</legend>
        <ul className="mt-3 space-y-3">
          {addresses.map((address) => <li key={address.id}>
            <label className={`flex cursor-pointer items-start gap-3 rounded-md border bg-paper p-3 ${selected === address.id ? "border-ink ring-1 ring-ink" : "border-line"}`}>
              <input type="radio" name="adres" value={address.id} checked={selected === address.id} onChange={() => { setSelected(address.id); setProblem(null); }} className="mt-1 size-5 shrink-0 accent-[var(--color-ink)]" />
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

      {problem && <ProblemBox problem={problem} />}
      {unknown ? <UnknownBox sending={sending} onRetry={() => void place(unknown)} /> : <>
        <button type="button" data-testid="order-submit" aria-disabled={!canOrder} aria-describedby="siparis-not" onClick={() => { if (canOrder && preview.kind === "ready") order(preview.data); }} className={`${btnPrimary} mt-5 w-full`}>{sending ? "Sipariş oluşturuluyor…" : waitSeconds > 0 ? `${waitSeconds} sn sonra tekrar dene` : "Sipariş oluştur — ödeme alınmaz"}</button>
        {preview.kind === "ready" && preview.data.canProceed && !preview.csrf && <p role="alert" className="mt-3 text-sm text-red-800">Güvenlik doğrulaması alınamadı. <button type="button" onClick={() => setAttempt((n) => n + 1)} className={btnLink}>Tekrar dene</button></p>}
      </>}
      <p id="siparis-not" className="mt-3 text-sm text-fg-muted" data-testid="order-note">Ödeme alınmaz: sipariş ödenmemiş bir kayıt olarak oluşur, kargo ücreti sonra belirlenir. Sepet stok ayırmaz; stok yalnızca sipariş verdiğin anda düşer. Fiyat ya da stok değişirse sipariş verilmeden önce uyarılırsın.</p>
    </aside>
  </div>);
}

/** Retry-After saniyesini okunur süreye çevirir ("12 dakika", "3 saat"); tahmindir, sunucu saatine göre yuvarlanır. */
function formatWait(seconds: number): string {
  if (seconds < 90) return `${seconds} saniye`;
  if (seconds < 5400) return `${Math.ceil(seconds / 60)} dakika`;
  return `${Math.ceil(seconds / 3600)} saat`;
}

function ProblemBox({ problem }: { problem: Problem }) {
  return <div role="alert" data-testid="order-problem" className="mt-5 rounded-md border border-amber-400 bg-amber-50 p-3 text-amber-950">
    <p className="font-semibold">{problem.title}</p>
    <p className="mt-1 text-sm">{problem.text}</p>
    {problem.details && problem.details.length > 0 && <ul data-testid="order-changes" className="mt-2 list-disc pl-5 text-sm">{problem.details.map((d) => <li key={d}>{d}</li>)}</ul>}
    {problem.lines && problem.lines.length > 0 && <ul data-testid="order-problem-lines" className="mt-2 list-disc pl-5 text-sm">{problem.lines.map((d) => <li key={d}>{d}</li>)}</ul>}
    {problem.link === "sepet" && <Link href="/sepet" className={`${btnLink} mt-1`}>Sepete dön</Link>}
    {problem.link === "login" && <Link href={loginHref("/checkout")} className={`${btnLink} mt-1`}>Giriş yap</Link>}
    {problem.link === "siparisler" && <Link href="/siparisler" className={`${btnLink} mt-1`}>Siparişlerim</Link>}
    {problem.link === "reload" && <button type="button" onClick={() => window.location.reload()} className={`${btnSecondary} mt-2`}>Sayfayı yenile</button>}
  </div>;
}

// Yanıt kaybolduysa sipariş oluşmuş olabilir. Burada "başarısız oldu" denmez; tek yol AYNI anahtarla yeniden sormaktır.
function UnknownBox({ sending, onRetry }: { sending: boolean; onRetry: () => void }) {
  return <div role="alert" data-testid="order-unknown" className="mt-5 rounded-md border border-amber-400 bg-amber-50 p-3 text-amber-950">
    <p className="font-semibold">Siparişinin sonucu bilinmiyor</p>
    <p className="mt-1 text-sm">İsteği gönderdik ama yanıtı alamadık; sipariş oluşmuş olabilir. Aynı isteği yeniden göndermek ikinci bir sipariş yaratmaz: oluştuysa mevcut siparişin açılır, oluşmadıysa şimdi oluşturulur.</p>
    <button type="button" data-testid="order-retry" aria-disabled={sending} onClick={() => { if (!sending) onRetry(); }} className={`${btnPrimary} mt-3 w-full`}>{sending ? "Kontrol ediliyor…" : "Aynı siparişi yeniden gönder"}</button>
    <Link href="/siparisler" className={`${btnLink} mt-1`}>Siparişlerim&apos;e bak</Link>
  </div>;
}

function Summary({ data }: { data: CheckoutPreview }) {
  return <div data-testid="preview" data-can-proceed={String(data.canProceed)}>
    <div data-testid="preview-address" className="mt-3 rounded-md border border-line bg-paper p-3 text-sm"><p className="font-semibold">Teslimat: {data.address.label}</p><p>{data.address.recipientName}</p><p className="break-words">{data.address.district} / {data.address.province}</p></div>
    <ul className="mt-3 divide-y divide-line">
      {data.lines.map((line) => <li key={line.id} data-testid="preview-line" data-status={line.status} className="py-3">
        <p className="font-semibold">{line.productName}</p>
        <p className="text-fg-muted">{line.variantName}</p>
        <p className="font-mono text-xs text-fg-muted">{line.sku}</p>
        <p className="mt-1 flex justify-between gap-3 tabular-nums"><span>{line.quantity} adet{line.unitPriceMinor !== null ? ` × ${formatPriceMinor(line.unitPriceMinor)}` : ""}</span><span className="font-semibold">{line.lineSubtotalMinor === null ? "—" : formatPriceMinor(line.lineSubtotalMinor)}</span></p>
        {line.priceChanged && line.previousUnitPriceMinor !== null && line.unitPriceMinor !== null && <p className="mt-1 text-sm">Fiyat değişti: önceki fiyat {formatPriceMinor(line.previousUnitPriceMinor)}, güncel fiyat {formatPriceMinor(line.unitPriceMinor)}.</p>}
        {statusNote(line) && <p className="mt-1 rounded-md border border-amber-400 bg-amber-50 px-3 py-2 text-sm text-amber-950">{statusNote(line)}</p>}
      </li>)}
    </ul>
    <dl className="mt-2 space-y-2 border-t border-dashed border-line pt-3">
      <div className="flex justify-between gap-4"><dt>Ara toplam</dt><dd data-testid="preview-subtotal" className="font-display text-lg font-bold tabular-nums">{formatPriceMinor(data.subtotalMinor)}</dd></div>
      <div className="flex justify-between gap-4"><dt>Kargo</dt><dd data-testid="preview-shipping" className="text-fg-muted">{data.shippingMinor === null ? "Kargo henüz hesaplanmadı" : formatPriceMinor(data.shippingMinor)}</dd></div>
      <div className="flex justify-between gap-4"><dt>Ödeme</dt><dd data-testid="preview-payment" className="text-fg-muted">Alınmaz</dd></div>
    </dl>
    <p className="mt-2 text-sm text-fg-muted">Kargo hesaplanmadığı için toplam tutar henüz belli değil.</p>
    <p className="mt-1 text-sm text-fg-muted">Özet {new Date(data.calculatedAt).toLocaleTimeString("tr-TR", { hour: "2-digit", minute: "2-digit", timeZone: "Europe/Istanbul" })} itibarıyla hesaplandı.</p>
    {!data.canProceed && <div data-testid="preview-issues" className="mt-3 rounded-md border border-amber-400 bg-amber-50 p-3 text-amber-950">
      <p className="font-semibold">Devam etmeden önce sepetini düzenle.</p>
      <ul className="mt-1 list-disc pl-5 text-sm">{Array.from(new Set(data.issues.map((i) => i.code))).map((code) => <li key={code}>{issueText[code] ?? "Sepetinde bir sorun var."}</li>)}</ul>
      <Link href="/sepet" className={`${btnLink} mt-1`}>Sepete dön</Link>
    </div>}
  </div>;
}

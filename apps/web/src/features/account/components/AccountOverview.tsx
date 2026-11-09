"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { authMutation, fetchAccount, type Account } from "@/features/auth/auth-client";
import { resetCart } from "@/features/cart/cart";
import { loadFavorites, resetFavorites, useFavorites } from "@/features/favorites/favorites";
import { formatOrderDate, StatusStamp, Thumb } from "@/features/orders/components/OrderParts";
import { listRecentOrders, type OrderSummary, type Paged } from "@/features/orders/orders";
import { listAddresses } from "@/features/account/addresses";
import { btnLink, btnPrimary, btnSecondary, panel } from "@/components/ui/styles";
import { formatDateIstanbul } from "@/lib/format";
import { usePersistedPageshow } from "@/lib/page-restore";
import { loginHref } from "@/lib/safe-next";

// Hesap özeti. Kişisel veri sunucudan HTML olarak gelmez: her blok tarayıcıda, oturum çerezi ile `no-store` okunur.
// Bloklar birbirinden bağımsızdır (kendi yükleniyor/hata durumu ve "Tekrar dene"); biri düşerse diğerleri çalışmaya devam eder.
type Result<T> = { ok: true; data: T } | { ok: false; status: number; message: string };
type Load<T> = { kind: "loading" } | { kind: "error"; status: number; message: string } | { kind: "ready"; data: T };

function useLoad<T>(fetcher: () => Promise<Result<T>>) {
  const [state, setState] = useState<Load<T>>({ kind: "loading" });
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    let alive = true;
    void fetcher().then((result) => {
      if (!alive) return;
      setState(result.ok ? { kind: "ready", data: result.data } : { kind: "error", status: result.status, message: result.message });
    });
    return () => { alive = false; };
  }, [fetcher, attempt]);
  const reload = () => { setState({ kind: "loading" }); setAttempt((n) => n + 1); };
  return [state, reload, setState] as const;
}

// Sabit başvurular: useLoad bağımlılığı yeniden başlatmasın.
const recentOrders = () => listRecentOrders(3);

/** Üyelik künyesi: e-posta, doğrulama durumu, üyelik tarihi. */
function Profile({ state, retry }: { state: Load<Account>; retry: () => void }) {
  if (state.kind === "loading") return <p role="status" className="mt-6">Hesap yükleniyor…</p>;
  if (state.kind === "error") return <div role="alert" className={`${panel} mt-6`}><p className="font-semibold">Hesap bilgilerin yüklenemedi.</p><p className="mt-1 text-fg-muted">{state.message}</p><button type="button" onClick={retry} className={`${btnPrimary} mt-4`}>Tekrar dene</button></div>;
  const account = state.data;
  const since = account.createdAt ? formatDateIstanbul(account.createdAt) : null;
  return <section aria-label="Üyelik bilgileri" className={`${panel} mt-6`}>
    <dl className="divide-y divide-dashed divide-line [&>div]:py-3 [&>div:first-child]:pt-0 [&>div:last-child]:pb-0">
      <div><dt className="text-sm text-fg-muted">E-posta</dt><dd data-testid="account-email" className="font-display text-lg font-bold break-all">{account.email}</dd></div>
      <div><dt className="text-sm text-fg-muted">E-posta durumu</dt><dd data-testid="account-verified">{account.emailConfirmed ? "Doğrulandı" : "Doğrulanmadı; e-posta doğrulaması henüz sunulmuyor"}</dd></div>
      {since && <div><dt className="text-sm text-fg-muted">Üyelik tarihi</dt><dd data-testid="account-since">{since}</dd></div>}
      <AccountId id={account.id} />
    </dl>
  </section>;
}

// Hesap kimliği (uuid) gizli değildir ama kişiye aittir: yalnız tarayıcıda gösterilir. Personel olacak kişi bunu bir yöneticiye iletir.
function AccountId({ id }: { id: string }) {
  const [copied, setCopied] = useState<"idle" | "done" | "failed">("idle");
  async function copy() {
    try { await navigator.clipboard.writeText(id); setCopied("done"); } catch { setCopied("failed"); }
  }
  return <div>
    <dt className="text-sm text-fg-muted">Hesap kimliği</dt>
    <dd className="mt-1 flex flex-wrap items-center gap-3">
      <code data-testid="account-id" className="break-all rounded border border-dashed border-line bg-paper px-2 py-1 font-mono text-sm">{id}</code>
      <button type="button" onClick={() => void copy()} className={btnSecondary}>Kopyala</button>
      <span role="status" className="text-sm text-fg-muted">{copied === "done" ? "Kopyalandı." : copied === "failed" ? "Kopyalanamadı; kimliği elle seçip kopyala." : ""}</span>
    </dd>
    <dd className="mt-1 text-sm text-fg-muted">Mağaza yönetiminde çalışacaksan bu kimliği bir yöneticiye ilet. Rol, e-posta ile değil bu kimlikle verilir.</dd>
  </div>;
}

/** Fiş altındaki sayım şeridi: kesik çizgiyle ayrılmış üç hücre. Her hücre kendi durumunu taşır. */
function Tally({ label, testId, state, retry }: { label: string; testId: string; state: { kind: "loading" } | { kind: "error" } | { kind: "ready"; count: number }; retry: () => void }) {
  return <div className="flex min-w-0 flex-col-reverse justify-end px-2 text-center sm:px-4">
    <dt className="text-sm text-fg-muted">{label}</dt>
    <dd className="font-display text-3xl font-extrabold tabular-nums" data-testid={testId} aria-busy={state.kind === "loading"}>
      {state.kind === "ready" ? state.count : <><span aria-hidden="true">{state.kind === "loading" ? "…" : "—"}</span><span className="sr-only">{state.kind === "loading" ? "yükleniyor" : "yüklenemedi"}</span></>}
      {state.kind === "error" && <button type="button" onClick={retry} className="mx-auto mt-1 block min-h-11 text-sm font-semibold text-accent-deep underline underline-offset-2">Tekrar dene</button>}
    </dd>
  </div>;
}

function RecentOrderRow({ order }: { order: OrderSummary }) {
  const first = order.previewItems[0];
  return <li data-testid="recent-order-row">
    <Link href={`/siparisler/${order.id}`} className="flex min-h-14 items-center gap-3 rounded-md py-3 hover:bg-paper-2">
      <span className="max-sm:hidden"><Thumb url={first?.imageUrl ?? null} alt={null} /></span>
      <span className="min-w-0 flex-1">
        <span className="block font-mono text-[0.9375rem] font-medium whitespace-nowrap">{order.orderNumber}</span>
        <span className="block text-sm text-fg-muted">{formatOrderDate(order.createdAt)} · {order.itemCount} ürün</span>
      </span>
      <StatusStamp status={order.status} />
    </Link>
  </li>;
}

function RecentOrders({ state, retry }: { state: Load<Paged<OrderSummary>>; retry: () => void }) {
  let body;
  if (state.kind === "loading") body = <p role="status" className="py-1">Siparişlerin yükleniyor…</p>;
  else if (state.kind === "error") body = <div role="alert"><p className="font-semibold">Siparişler yüklenemedi.</p><p className="mt-1 text-fg-muted">{state.message}</p><button type="button" onClick={retry} className={`${btnSecondary} mt-3`}>Tekrar dene</button></div>;
  else if (state.data.totalCount === 0) body = <div data-testid="recent-orders-empty"><p className="font-semibold">Henüz siparişin yok.</p><p className="mt-1 text-fg-muted">Sepetini adresinle onayladığında siparişin burada görünür.</p><Link href="/#urunler" className={`${btnPrimary} mt-4`}>Mağazaya göz at</Link></div>;
  else body = <>
    <ul data-testid="recent-orders" className="-mx-2 divide-y divide-dashed divide-line">{state.data.items.map((order) => <RecentOrderRow key={order.id} order={order} />)}</ul>
    {state.data.totalCount > state.data.items.length && <p className="mt-3"><Link href="/siparisler" className={btnLink}>Tüm siparişleri gör ({state.data.totalCount})</Link></p>}
  </>;
  return <section aria-labelledby="son-siparisler" className={`${panel} mt-6`}>
    <h2 id="son-siparisler" className="mb-3 font-display text-xl font-bold">Son siparişlerin</h2>
    {body}
  </section>;
}

export function AccountOverview() {
  const router = useRouter();
  const [account, reloadAccount, setAccount] = useLoad(fetchAccount);
  const [orders, reloadOrders] = useLoad(recentOrders);
  const [addresses, reloadAddresses] = useLoad(listAddresses);
  const favorites = useFavorites({ refresh: true });
  const [busy, setBusy] = useState(false);
  const [logoutError, setLogoutError] = useState("");

  // bfcache geri yüklemesi: ekrandaki veri önceki kimliğe ait olabilir; hepsini yeniden oku.
  usePersistedPageshow(() => { reloadAccount(); reloadOrders(); reloadAddresses(); void loadFavorites(true); });

  async function logout() {
    setBusy(true); setLogoutError("");
    const result = await authMutation<void>("logout");
    setBusy(false);
    if (!result.ok) { setLogoutError(result.message); return; }
    // Oturum durumu yalnız hesap bloğunda tutulur: Next önceki rotayı gizli saklayıp geri gelince yeniden gösterir; efekt yeniden çalışınca hesap yeniden okunur.
    setAccount({ kind: "error", status: 401, message: "" });
    resetCart();
    resetFavorites();
    router.push("/giris"); router.refresh();
  }

  const anonymous = account.kind === "error" && account.status === 401;
  const heading = <h1 className="font-display text-title font-extrabold">Hesabım</h1>;
  if (anonymous) return <div className="mx-auto max-w-3xl px-4 py-8 sm:px-6 md:py-10">{heading}<div className={`${panel} mt-6`}><p>Hesabını görmek için giriş yap.</p><Link href={loginHref("/hesabim")} className={`${btnPrimary} mt-4`}>Giriş yap</Link></div></div>;

  const favoriteCount = favorites.status === "ready" ? { kind: "ready" as const, count: favorites.ids.size } : favorites.status === "error" ? { kind: "error" as const } : { kind: "loading" as const };
  const toTally = <T,>(load: Load<T>, count: (data: T) => number) => load.kind === "ready" ? { kind: "ready" as const, count: count(load.data) } : { kind: load.kind };
  const staff = account.kind === "ready" && (account.data.roles.includes("Admin") || account.data.roles.includes("Warehouse"));
  const adminHref = account.kind === "ready" && account.data.roles.includes("Admin") ? "/admin" : "/admin/siparisler";

  return <div className="mx-auto max-w-3xl px-4 py-8 sm:px-6 md:py-10">
    {heading}
    <Profile state={account} retry={reloadAccount} />
    <dl className="mt-6 grid grid-cols-3 divide-x-2 divide-dashed divide-line rounded-xl border border-line bg-card px-2 py-5 shadow-card sm:px-4 sm:py-6">
      <Tally label="Sipariş" testId="tally-orders" state={toTally(orders, (d) => d.totalCount)} retry={reloadOrders} />
      <Tally label="Adres" testId="tally-addresses" state={toTally(addresses, (d) => d.length)} retry={reloadAddresses} />
      <Tally label="Favori" testId="tally-favorites" state={favoriteCount} retry={() => void loadFavorites(true)} />
    </dl>
    <RecentOrders state={orders} retry={reloadOrders} />
    {logoutError && <p role="alert" className="mt-6 rounded-md border border-red-300 bg-red-50 p-3 text-sm text-red-800">{logoutError}</p>}
    <div className="mt-6 flex flex-wrap items-center gap-x-6 gap-y-2">
      <button type="button" disabled={busy} onClick={logout} className={btnPrimary}>{busy ? "Çıkış yapılıyor…" : "Çıkış yap"}</button>
      {staff && <Link href={adminHref} className={btnLink}>Yönetim</Link>}
    </div>
  </div>;
}

"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { onSessionLost } from "@/features/admin/client";
import {
  authMutation,
  fetchAccount,
  type Account,
} from "@/features/auth/auth-client";
import { resetCart } from "@/features/cart/cart";
import { resetFavorites } from "@/features/favorites/favorites";
import { usePersistedPageshow } from "@/lib/page-restore";
import { loginHref } from "@/lib/safe-next";
import { LogoMark } from "@/components/ui/LogoMark";
import { btnPrimary, btnSecondary } from "@/components/ui/styles";
import { AdminContext } from "./AdminContext";

type State =
  | { kind: "loading" }
  | { kind: "anonymous" }
  | { kind: "error"; message: string }
  | { kind: "ready"; account: Account };

const NAV: { href: string; label: string; adminOnly: boolean }[] = [
  { href: "/admin", label: "Özet", adminOnly: true },
  { href: "/admin/urunler", label: "Ürünler", adminOnly: true },
  { href: "/admin/kategoriler", label: "Kategoriler", adminOnly: true },
  { href: "/admin/stok", label: "Stok", adminOnly: true },
  { href: "/admin/depolar", label: "Depolar", adminOnly: true },
  { href: "/admin/siparisler", label: "Siparişler", adminOnly: false },
  { href: "/admin/raporlar", label: "Raporlar", adminOnly: true },
  { href: "/admin/kullanicilar", label: "Kullanıcılar", adminOnly: true },
];
const onOrders = (pathname: string) =>
  pathname === "/admin/siparisler" || pathname.startsWith("/admin/siparisler/");
const isActive = (pathname: string, href: string) =>
  href === "/admin"
    ? pathname === "/admin"
    : pathname === href || pathname.startsWith(`${href}/`);

// Yönetim çerçevesi: vitrin üst bölgesi/alt bilgisi yoktur. Hesap ve roller tarayıcıda /auth/me ile okunur (HTML'de kişisel veri yok).
// src/proxy.ts yanlış rolü HTTP düzeyinde zaten durdurur; burası oturum ortasında bitişi ve rol değişimini de karşılar. Backend her uçta yetkiyi yine uygular.
export function AdminShell({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const router = useRouter();
  const [state, setState] = useState<State>({ kind: "loading" });
  const [attempt, setAttempt] = useState(0);
  const [busy, setBusy] = useState(false);
  const [logoutError, setLogoutError] = useState("");
  const tabsRef = useRef<HTMLUListElement>(null);

  useEffect(() => {
    let alive = true;
    void fetchAccount().then((result) => {
      if (!alive) return;
      if (result.ok) setState({ kind: "ready", account: result.data });
      else if (result.status === 401) setState({ kind: "anonymous" });
      else setState({ kind: "error", message: result.message });
    });
    return () => {
      alive = false;
    };
  }, [attempt]);
  useEffect(() => onSessionLost(() => setState({ kind: "anonymous" })), []);
  // bfcache geri yüklemesi: ekrandaki yönetim verisi önceki kimliğe ait olabilir; yeniden doğrula.
  usePersistedPageshow(() => {
    setState({ kind: "loading" });
    setAttempt((n) => n + 1);
  });

  const account = state.kind === "ready" ? state.account : null;
  const isAdmin = account?.roles.includes("Admin") ?? false;
  const isWarehouse = account?.roles.includes("Warehouse") ?? false;
  const staff = isAdmin || isWarehouse;
  const warehouseOnly = isWarehouse && !isAdmin;

  // Depo `/admin` kökünde sipariş listesine gider (proxy de yapar; bu, oturum ortasında rol değişimini karşılar).
  useEffect(() => {
    if (warehouseOnly && pathname === "/admin")
      router.replace("/admin/siparisler");
  }, [warehouseOnly, pathname, router]);
  // Dar ekranda etkin sekmeyi görünür tut (yalnız şerit kaydırılır, sayfa değil).
  useEffect(() => {
    const list = tabsRef.current;
    const current = list?.querySelector<HTMLElement>("[aria-current='page']");
    if (list && current)
      list.scrollLeft = Math.max(
        0,
        current.offsetLeft - (list.clientWidth - current.offsetWidth) / 2,
      );
  }, [pathname, staff]);

  async function logout() {
    setBusy(true);
    setLogoutError("");
    const result = await authMutation<void>("logout");
    setBusy(false);
    if (!result.ok) {
      setLogoutError(result.message);
      return;
    }
    setState({ kind: "anonymous" }); // bellekteki yönetim verisi bu çizimle birlikte düşer
    resetCart();
    resetFavorites();
    router.push("/giris");
    router.refresh();
  }

  let body: React.ReactNode;
  if (state.kind === "loading")
    body = (
      <p role="status" className="py-10 text-fg-muted">
        Oturum denetleniyor…
      </p>
    );
  else if (state.kind === "anonymous")
    body = (
      <Gate
        title="Oturumun sona erdi"
        text="Yönetim alanını kullanmak için yeniden giriş yapmalısın. Yarım kalan bir stok işlemin varsa giriş yaptıktan sonra aynı ekranda devam eder."
      >
        <Link href={loginHref(pathname)} className={btnPrimary}>
          Giriş yap
        </Link>
      </Gate>
    );
  else if (state.kind === "error")
    body = (
      <Gate title="Oturum doğrulanamadı" text={state.message} alert>
        <button
          type="button"
          onClick={() => {
            setState({ kind: "loading" });
            setAttempt((n) => n + 1);
          }}
          className={btnPrimary}
        >
          Tekrar dene
        </button>
      </Gate>
    );
  else if (!staff)
    body = (
      <Gate
        title="Bu alana erişimin yok"
        text="Yönetim alanı yönetici ve depo personeli içindir. Yetki gerekiyorsa hesap kimliğini Hesabım sayfasından kopyalayıp bir yöneticiye ilet."
      >
        <Link href="/hesabim" className={btnPrimary}>
          Hesabım
        </Link>
      </Gate>
    );
  else if (warehouseOnly && !onOrders(pathname))
    body =
      pathname === "/admin" ? (
        <p role="status" className="py-10 text-fg-muted">
          Siparişlere yönlendiriliyorsun…
        </p>
      ) : (
        <Gate
          title="Bu bölüm yöneticiye açık"
          text="Depo personeli yalnızca sipariş ekranlarını kullanabilir."
        >
          <Link href="/admin/siparisler" className={btnPrimary}>
            Siparişlere git
          </Link>
        </Gate>
      );
  else
    body = (
      <AdminContext.Provider value={{ account: state.account, isAdmin }}>
        {children}
      </AdminContext.Provider>
    );

  const items = NAV.filter((item) => isAdmin || !item.adminOnly);
  return (
    <>
      <header className="on-ink bg-ink text-on-ink">
        <div className="mx-auto flex max-w-7xl flex-wrap items-center gap-x-4 gap-y-1 px-4 pt-2 sm:px-6">
          <Link href="/admin" className="flex min-h-11 items-center gap-2.5">
            <LogoMark ground="ink" size={32} />
            <span className="font-display text-lg font-bold tracking-[-0.02em]">
              SSCommerce
            </span>{" "}
            <span className="rounded border border-on-ink-muted/50 px-1.5 py-0.5 font-mono text-xs text-on-ink-muted">
              yönetim
            </span>
          </Link>
          <div className="ml-auto flex min-w-0 items-center gap-1 sm:gap-3">
            {account && (
              <>
                <span className="rounded-full border border-accent/60 px-2.5 py-0.5 text-xs font-semibold text-accent">
                  {isAdmin ? "Yönetici" : isWarehouse ? "Depo" : "Yetkisiz"}
                </span>
                <span
                  className="hidden max-w-[16rem] truncate text-sm text-on-ink-muted lg:inline"
                  title={account.email}
                >
                  {account.email}
                </span>
              </>
            )}
            <Link
              href="/"
              className="inline-flex min-h-11 items-center px-2 text-sm font-medium hover:text-accent"
            >
              Mağaza
            </Link>
            {account && (
              <button
                type="button"
                onClick={() => void logout()}
                disabled={busy}
                className="inline-flex min-h-11 items-center px-2 text-sm font-medium hover:text-accent disabled:opacity-60"
              >
                {busy ? "Çıkış…" : "Çıkış yap"}
              </button>
            )}
          </div>
        </div>
        {staff && (
          <nav
            aria-label="Yönetim bölümleri"
            className="mx-auto max-w-7xl px-2 sm:px-4"
          >
            <ul
              ref={tabsRef}
              className="no-scrollbar flex gap-0.5 overflow-x-auto"
            >
              {items.map((item) => {
                const active = isActive(pathname, item.href);
                return (
                  <li key={item.href} className="shrink-0">
                    <Link
                      href={item.href}
                      aria-current={active ? "page" : undefined}
                      className={`inline-flex min-h-11 items-center border-b-[3px] px-3.5 text-sm font-semibold transition-colors duration-150 ${active ? "border-accent text-on-ink" : "border-transparent text-on-ink-muted hover:text-on-ink"}`}
                    >
                      {item.label}
                    </Link>
                  </li>
                );
              })}
            </ul>
          </nav>
        )}
        <div
          aria-hidden="true"
          className="h-0 border-b-[3px] border-dashed border-accent/70"
        />
      </header>
      <main
        id="icerik"
        tabIndex={-1}
        className="mx-auto w-full max-w-7xl flex-1 px-4 py-6 outline-none sm:px-6 md:py-8"
      >
        {logoutError && (
          <p
            role="alert"
            className="mb-4 rounded-md border border-red-300 bg-red-50 p-3 text-sm text-red-800"
          >
            {logoutError}
          </p>
        )}
        {body}
      </main>
    </>
  );
}

function Gate({
  title,
  text,
  alert,
  children,
}: {
  title: string;
  text: string;
  alert?: boolean;
  children?: React.ReactNode;
}) {
  return (
    <div
      role={alert ? "alert" : undefined}
      className="mx-auto max-w-lg rounded-xl border border-line bg-card p-6 shadow-card"
    >
      <h1 className="font-display text-2xl font-extrabold">{title}</h1>
      <p className="mt-2 text-fg-muted">{text}</p>
      <div className="mt-4 flex flex-wrap gap-3">
        {children}
        <Link href="/" className={btnSecondary}>
          Mağazaya dön
        </Link>
      </div>
    </div>
  );
}

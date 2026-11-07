"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { authMutation, fetchAccount, type Account } from "@/lib/auth-client";
import { resetCart } from "@/lib/cart";
import { btnSecondary } from "./ui";

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

export function AccountPanel() {
  const router = useRouter();
  const [state, setState] = useState<{ kind: "loading" } | { kind: "anonymous" } | { kind: "error"; message: string } | { kind: "ready"; account: Account }>({ kind: "loading" });
  const [busy, setBusy] = useState(false);
  const [logoutError, setLogoutError] = useState("");
  useEffect(() => {
    let alive = true;
    fetchAccount().then((result) => {
      if (!alive) return;
      if (result.ok) setState({ kind: "ready", account: result.data });
      else if (result.status === 401) setState({ kind: "anonymous" });
      else setState({ kind: "error", message: result.message });
    });
    return () => { alive = false; };
  }, []);
  async function logout() {
    setBusy(true); setLogoutError("");
    const result = await authMutation<void>("logout");
    setBusy(false);
    if (!result.ok) { setLogoutError(result.message); return; }
    setState({ kind: "anonymous" });
    resetCart();
    router.push("/giris"); router.refresh();
  }
  if (state.kind === "loading") return <p role="status" className="mt-6">Hesap yükleniyor…</p>;
  if (state.kind === "anonymous") return <div className="mt-6 rounded-xl border border-line bg-card p-6"><p>Hesabını görmek için giriş yap.</p><Link href="/giris" className="mt-4 inline-block rounded-md bg-ink px-5 py-3 font-semibold text-on-ink">Giriş yap</Link></div>;
  if (state.kind === "error") return <p role="alert" className="mt-6 rounded-md border border-line bg-card p-4">{state.message}</p>;
  return <div className="mt-6 rounded-xl border border-line bg-card p-6 shadow-card">
    <dl className="space-y-3">
      <div><dt className="text-sm text-fg-muted">E-posta</dt><dd className="font-semibold break-all">{state.account.email}</dd></div>
      <div><dt className="text-sm text-fg-muted">E-posta durumu</dt><dd>{state.account.emailConfirmed ? "Doğrulandı" : "Doğrulanmadı; e-posta doğrulaması henüz sunulmuyor"}</dd></div>
      <AccountId id={state.account.id} />
    </dl>
    <ul className="mt-6 flex flex-wrap gap-x-6">
      <li><Link href="/sepet" className="inline-flex min-h-11 items-center font-semibold text-accent-deep underline underline-offset-2">Sepetim</Link></li>
      <li><Link href="/siparisler" className="inline-flex min-h-11 items-center font-semibold text-accent-deep underline underline-offset-2">Siparişlerim</Link></li>
      {(state.account.roles.includes("Admin") || state.account.roles.includes("Warehouse")) && <li><Link href={state.account.roles.includes("Admin") ? "/admin" : "/admin/siparisler"} className="inline-flex min-h-11 items-center font-semibold text-accent-deep underline underline-offset-2">Yönetim</Link></li>}
      <li><Link href="/hesabim/adresler" className="inline-flex min-h-11 items-center font-semibold text-accent-deep underline underline-offset-2">Adreslerim</Link></li>
    </ul>
    {logoutError && <p role="alert" className="mt-5 rounded-md border border-red-300 bg-red-50 p-3 text-sm text-red-800">{logoutError}</p>}
    <button type="button" disabled={busy} onClick={logout} className="mt-4 rounded-md bg-ink px-5 py-3 font-semibold text-on-ink disabled:opacity-60">{busy ? "Çıkış yapılıyor…" : "Çıkış yap"}</button>
  </div>;
}

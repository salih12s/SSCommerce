"use client";

import Link from "next/link";
import { useEffect, useState, useSyncExternalStore, type FormEvent } from "react";
import { changePassword, fetchAccount, type Account } from "@/features/auth/auth-client";
import { btnPrimary, fieldClass, panel } from "@/components/ui/styles";
import { usePersistedPageshow } from "@/lib/page-restore";
import { loginHref } from "@/lib/safe-next";

// Güvenlik: parola değiştirme. Parola yalnızca tarayıcı belleğinde, formun alanlarında yaşar; URL'ye, depolamaya ve loga yazılmaz.
// Parola kuralı sunucudadır: istemci yalnızca "tekrar eşleşiyor mu" kolaylığını yapar, kuralı yeniden yazmaz.
const subscribe = () => () => {};
type Account$ = { kind: "loading" } | { kind: "anonymous" } | { kind: "error"; message: string } | { kind: "ready"; account: Account };
type Fields = { currentPassword?: string; newPassword?: string; confirm?: string };
const FIELD_ID = { currentPassword: "mevcut-parola", newPassword: "yeni-parola", confirm: "yeni-parola-tekrar" } as const;

/** Sunucu anahtar adını camelCase döndürür; PascalCase gelirse de eşleşsin. */
function pick(errors: Record<string, string[]>, name: string): string | undefined {
  const key = Object.keys(errors).find((k) => k.toLowerCase() === name.toLowerCase());
  const lines = key ? errors[key] : undefined;
  return lines && lines.length > 0 ? lines.join(" ") : undefined;
}

function PasswordField({ name, label, autoComplete, show, error, hint }: { name: keyof typeof FIELD_ID; label: string; autoComplete: "current-password" | "new-password"; show: boolean; error?: string; hint?: string }) {
  const id = FIELD_ID[name];
  const describedBy = [error ? `${id}-hata` : null, hint ? `${id}-ipucu` : null].filter(Boolean).join(" ") || undefined;
  return <div>
    <label htmlFor={id} className="mb-1 block font-semibold">{label}</label>
    <input id={id} name={name} type={show ? "text" : "password"} autoComplete={autoComplete} required maxLength={128} spellCheck={false} autoCapitalize="none" autoCorrect="off" aria-invalid={error ? true : undefined} aria-describedby={describedBy} className={`${fieldClass} h-12`} />
    {hint && <p id={`${id}-ipucu`} className="mt-1 text-sm text-fg-muted">{hint}</p>}
    {error && <p id={`${id}-hata`} className="mt-1 text-sm text-red-700">{error}</p>}
  </div>;
}

export function SecurityPanel() {
  const hydrated = useSyncExternalStore(subscribe, () => true, () => false);
  const [account, setAccount] = useState<Account$>({ kind: "loading" });
  const [attempt, setAttempt] = useState(0);
  const [show, setShow] = useState(false);
  const [busy, setBusy] = useState(false);
  const [fields, setFields] = useState<Fields>({});
  const [error, setError] = useState("");
  const [done, setDone] = useState("");

  useEffect(() => {
    let alive = true;
    void fetchAccount().then((result) => {
      if (!alive) return;
      if (result.ok) setAccount({ kind: "ready", account: result.data });
      else if (result.status === 401) setAccount({ kind: "anonymous" });
      else setAccount({ kind: "error", message: result.message });
    });
    return () => { alive = false; };
  }, [attempt]);
  // bfcache geri yüklemesi: ekrandaki oturum önceki kimliğe ait olabilir.
  usePersistedPageshow(() => { setAccount({ kind: "loading" }); setDone(""); setError(""); setFields({}); setAttempt((n) => n + 1); });

  function focusField(name: keyof typeof FIELD_ID) { document.getElementById(FIELD_ID[name])?.focus(); }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const current = String(data.get("currentPassword") ?? "");
    const next = String(data.get("newPassword") ?? "");
    const again = String(data.get("confirm") ?? "");
    setError(""); setDone(""); setFields({});
    if (next !== again) { setFields({ confirm: "Yeni parola ile tekrarı aynı değil." }); focusField("confirm"); return; }
    setBusy(true);
    const result = await changePassword(current, next);
    setBusy(false);
    if (result.ok) {
      form.reset();
      setShow(false);
      setDone("Parolan değişti. Diğer cihazlardaki oturumların kapatıldı.");
      // Mevcut oturum yeni çerezle sürer: hesabı yeniden oku (oturum gerçekten açık mı).
      const refreshed = await fetchAccount();
      if (refreshed.ok) setAccount({ kind: "ready", account: refreshed.data });
      else if (refreshed.status === 401) { setDone(""); setAccount({ kind: "anonymous" }); }
      return;
    }
    if (result.status === 401) { setAccount({ kind: "anonymous" }); return; }
    if (result.status === 400) {
      const found: Fields = { currentPassword: pick(result.errors, "currentPassword"), newPassword: pick(result.errors, "newPassword") };
      if (found.currentPassword || found.newPassword) {
        setFields(found);
        focusField(found.currentPassword ? "currentPassword" : "newPassword");
        return;
      }
    }
    if (result.status === 429) {
      setError(result.retryAfter ? `Çok fazla deneme yapıldı. Yaklaşık ${result.retryAfter} saniye sonra tekrar dene.` : "Çok fazla deneme yapıldı. Biraz bekleyip tekrar dene.");
      return;
    }
    if (result.status === 409) { setError("Parola şu an değiştirilemedi: hesabın aynı anda başka bir işlemle güncellendi. Lütfen tekrar dene."); return; }
    setError(result.message);
  }

  let body;
  if (account.kind === "loading") body = <p role="status" className="mt-6">Hesap yükleniyor…</p>;
  else if (account.kind === "anonymous") body = <div className={`${panel} mt-6`} data-testid="security-anonymous"><p className="font-semibold">Parolanı değiştirmek için giriş yapmalısın.</p><Link href={loginHref("/hesabim/guvenlik")} className={`${btnPrimary} mt-4`}>Giriş yap</Link></div>;
  else if (account.kind === "error") body = <div role="alert" className={`${panel} mt-6`}><p className="font-semibold">Hesap bilgilerin yüklenemedi.</p><p className="mt-1 text-fg-muted">{account.message}</p><button type="button" onClick={() => { setAccount({ kind: "loading" }); setAttempt((n) => n + 1); }} className={`${btnPrimary} mt-4`}>Tekrar dene</button></div>;
  else body = <form onSubmit={submit} aria-busy={busy} className={`${panel} mt-6 space-y-5`} aria-labelledby="parola-baslik" data-testid="password-form">
    <h2 id="parola-baslik" className="font-display text-xl font-bold">Parolayı değiştir</h2>
    <p className="text-fg-muted">Parolanı değiştirdiğinde diğer cihazlardaki oturumların kapatılır; bu cihazdaki oturumun açık kalır.</p>
    <PasswordField name="currentPassword" label="Mevcut parola" autoComplete="current-password" show={show} error={fields.currentPassword} />
    <PasswordField name="newPassword" label="Yeni parola" autoComplete="new-password" show={show} error={fields.newPassword} hint="Kayıttaki parola kuralları geçerlidir; eksik bir kural olursa burada yazılır." />
    <PasswordField name="confirm" label="Yeni parola (tekrar)" autoComplete="new-password" show={show} error={fields.confirm} />
    <label className="flex min-h-11 w-fit cursor-pointer items-center gap-3 font-medium">
      <input type="checkbox" checked={show} onChange={(event) => setShow(event.target.checked)} className="size-5 accent-ink" />
      Parolaları göster
    </label>
    {error && <p role="alert" data-testid="password-error" className="rounded-md border border-red-300 bg-red-50 p-3 text-sm text-red-800">{error}</p>}
    <button type="submit" disabled={busy || !hydrated} className={`${btnPrimary} w-full sm:w-auto`}>{busy ? "Değiştiriliyor…" : "Parolayı değiştir"}</button>
  </form>;

  return <div className="mx-auto max-w-3xl px-4 py-8 sm:px-6 md:py-10">
    <h1 className="font-display text-title font-extrabold">Güvenlik</h1>
    <p className="mt-2 text-fg-muted">Hesabının parolasını buradan değiştirirsin.</p>
    <div role="status" data-testid="password-done">{done && <p className="mt-6 rounded-md border border-emerald-700 bg-emerald-50 p-3 font-semibold text-emerald-900">{done}</p>}</div>
    {body}
  </div>;
}

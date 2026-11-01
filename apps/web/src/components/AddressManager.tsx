"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { deleteAddress, listAddresses, setDefaultAddress, type Address } from "@/lib/addresses";
import { usePersistedPageshow } from "@/lib/page-restore";
import { loginHref } from "@/lib/safe-next";
import { AddressForm } from "./AddressForm";
import { ConfirmDialog } from "./ConfirmDialog";
import { btnLink, btnPrimary, btnSecondary, panel } from "./ui";

type Load = { kind: "loading" } | { kind: "anonymous" } | { kind: "error"; message: string } | { kind: "ready"; items: Address[] };

export function AddressCard({ address }: { address: Address }) {
  // Kullanıcı girdisi her zaman metin olarak basılır (React kaçışlar); HTML olarak yorumlanmaz.
  return (
    <div className="min-w-0">
      <p className="font-semibold">{address.label} {address.isDefault && <span className="ml-1 rounded-md border border-line bg-paper px-2 py-0.5 text-sm font-medium">Varsayılan</span>}</p>
      <p>{address.recipientName}</p>
      <p className="break-words text-fg-muted">{address.streetAddress}</p>
      <p className="break-words text-fg-muted">{address.district} / {address.province}{address.postalCode ? ` ${address.postalCode}` : ""}</p>
      <p className="text-fg-muted tabular-nums">Tel: {address.phone}</p>
    </div>
  );
}

export function AddressManager() {
  const [load, setLoad] = useState<Load>({ kind: "loading" });
  const [adding, setAdding] = useState(false);
  const [editing, setEditing] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<Address | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [announce, setAnnounce] = useState("");
  const headingRef = useRef<HTMLHeadingElement>(null);
  const addRef = useRef<HTMLButtonElement>(null);

  const apply = (result: Awaited<ReturnType<typeof listAddresses>>) => {
    if (result.ok) setLoad({ kind: "ready", items: result.data });
    else if (result.status === 401) setLoad({ kind: "anonymous" });
    else setLoad({ kind: "error", message: result.message });
  };
  const refresh = async () => apply(await listAddresses());
  useEffect(() => { void listAddresses().then(apply); }, []);
  // bfcache geri yüklemesi: adresler önceki kimliğe ait olabilir; temizle ve yeniden oku.
  usePersistedPageshow(() => { setLoad({ kind: "loading" }); setAdding(false); setEditing(null); setDeleting(null); setMessage(""); void listAddresses().then(apply); });
  const backToAdd = () => setTimeout(() => addRef.current?.focus(), 0);

  async function makeDefault(address: Address) {
    setBusy(true); setMessage("");
    const result = await setDefaultAddress(address.id);
    setBusy(false);
    if (!result.ok) { setMessage(result.message); return; }
    setAnnounce(`${address.label} varsayılan adres yapıldı.`);
    await refresh();
  }
  async function confirmDelete() {
    if (!deleting) return;
    setBusy(true); setMessage("");
    const result = await deleteAddress(deleting.id);
    setBusy(false);
    const name = deleting.label;
    setDeleting(null);
    if (!result.ok) { setMessage(result.message); return; }
    setAnnounce(`${name} silindi.`);
    await refresh();
    headingRef.current?.focus();
  }

  const items = load.kind === "ready" ? load.items : [];
  let body;
  if (load.kind === "loading") body = <p role="status" className="mt-6">Adresler yükleniyor…</p>;
  else if (load.kind === "anonymous") body = <div className={`${panel} mt-6`} data-testid="addresses-anonymous"><p className="font-semibold">Adreslerini görmek için giriş yapmalısın.</p><Link href={loginHref("/hesabim/adresler")} className={`${btnPrimary} mt-4`}>Giriş yap</Link></div>;
  else if (load.kind === "error") body = <div role="alert" className={`${panel} mt-6`}><p className="font-semibold">Adresler yüklenemedi.</p><p className="mt-1 text-fg-muted">{load.message}</p><button type="button" onClick={() => { setLoad({ kind: "loading" }); void refresh(); }} className={`${btnPrimary} mt-4`}>Tekrar dene</button></div>;
  else body = <>
    {message && <p role="alert" className="mt-6 rounded-md border border-red-300 bg-red-50 p-3 text-red-800">{message}</p>}
    {items.length === 0 && !adding && <div className={`${panel} mt-6`} data-testid="addresses-empty"><p className="font-semibold">Kayıtlı adresin yok.</p><p className="mt-1 text-fg-muted">Teslimat özeti için en az bir adres gerekir.</p></div>}
    <ul className="mt-6 space-y-4" aria-label="Kayıtlı adresler">
      {items.map((address) => <li key={address.id} data-testid="address-item" className={panel}>
        {editing === address.id ? <>
          <h2 className="mb-4 font-display text-lg font-bold">Adresi düzenle</h2>
          <AddressForm initial={address} idPrefix={`duzenle-${address.id}`} submitLabel="Değişiklikleri kaydet" onCancel={() => setEditing(null)} onSaved={() => { setEditing(null); setAnnounce("Adres güncellendi."); void refresh(); }} />
        </> : <>
          <AddressCard address={address} />
          <div className="mt-4 flex flex-wrap gap-3">
            <button type="button" className={btnSecondary} onClick={() => { setAdding(false); setEditing(address.id); }} aria-label={`${address.label} adresini düzenle`}>Düzenle</button>
            {!address.isDefault && <button type="button" className={btnSecondary} disabled={busy} onClick={() => void makeDefault(address)} aria-label={`${address.label} adresini varsayılan yap`}>Varsayılan yap</button>}
            <button type="button" className={btnSecondary} disabled={busy} onClick={() => setDeleting(address)} aria-label={`${address.label} adresini sil`}>Sil</button>
          </div>
        </>}
      </li>)}
    </ul>
    {adding ? <section aria-labelledby="yeni-adres" className={`${panel} mt-6`}>
      <h2 id="yeni-adres" className="mb-4 font-display text-lg font-bold">Yeni adres</h2>
      <AddressForm idPrefix="yeni" submitLabel="Adresi kaydet" onCancel={() => { setAdding(false); backToAdd(); }} onSaved={() => { setAdding(false); setAnnounce("Adres eklendi."); void refresh(); backToAdd(); }} />
    </section> : <button ref={addRef} type="button" className={`${btnPrimary} mt-6`} onClick={() => { setEditing(null); setAdding(true); }}>Yeni adres ekle</button>}
  </>;

  return <div className="mx-auto max-w-2xl px-4 py-12 sm:px-6">
    <h1 ref={headingRef} tabIndex={-1} className="font-display text-title font-extrabold outline-none">Adreslerim</h1>
    <p className="mt-2 text-fg-muted">Teslimat adreslerin yalnızca hesabında saklanır; adres bilgisi bağlantılara yazılmaz. <Link href="/hesabim" className={btnLink}>Hesabıma dön</Link></p>
    <p role="status" className="sr-only" data-testid="address-announce">{announce}</p>
    {body}
    <ConfirmDialog open={deleting !== null} title="Adres silinsin mi?" description={deleting ? `"${deleting.label}" adresi silinecek. Bu işlem geri alınamaz.` : ""} confirmLabel="Adresi sil" busy={busy} onConfirm={() => void confirmDelete()} onCancel={() => setDeleting(null)} />
  </div>;
}

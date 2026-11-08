"use client";

import Image from "next/image";
import Link from "next/link";
import { Fragment, useCallback, useEffect, useRef, useState } from "react";
import { btnPrimary, btnSecondary, panel } from "@/components/ui/styles";
import { formatPriceMinor } from "@/lib/format";
import { usePersistedPageshow } from "@/lib/page-restore";
import { loginHref } from "@/lib/safe-next";
import { fetchFavorites, setFavorite, useFavorites, type FavoriteItem } from "@/features/favorites/favorites";

// /favorilerim: kişisel liste yalnızca tarayıcıda, oturum çerezi ile `no-store` okunur (HTML'de yer almaz).
// Kendi satır kartını kullanır: ProductCard (catalog) buraya bağlanırsa favorites ↔ catalog döngüsü oluşurdu.
type View = { kind: "loading" } | { kind: "anonymous" } | { kind: "error"; message: string } | { kind: "ready"; items: FavoriteItem[] };
type Failure = { id: string; text: string };

function Row({ item, busy, onRemove }: { item: FavoriteItem; busy: boolean; onRemove: () => void }) {
  const name = item.available
    ? <Link href={`/urun/${item.slug}`} className="hover:underline">{item.name}</Link>
    : <span>{item.name}</span>;
  return (
    <li data-testid="favorite-row" data-available={item.available} className={`${panel} grid grid-cols-[5.5rem_1fr] gap-x-4 gap-y-3 sm:grid-cols-[7rem_1fr]`}>
      <div className="relative aspect-[4/3] w-full self-start overflow-hidden rounded-md bg-paper-2 sm:row-span-2">
        {item.primaryImage
          ? <Image src={item.primaryImage.url} alt={item.primaryImage.altText} fill sizes="(min-width: 640px) 112px, 88px" className={`object-cover ${item.available ? "" : "opacity-50 grayscale"}`} />
          : <span className="grid h-full place-items-center px-1 text-center text-xs text-fg-muted">Görsel yok</span>}
      </div>
      <div className="min-w-0">
        <p className="text-[0.8125rem] leading-tight text-fg-muted">{item.category.name}</p>
        <h2 className="font-display text-lg leading-snug font-bold break-words">{name}</h2>
        {item.available
          ? <p className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1">
              {/* Fiyat etiketi: ürünün kendi dilinden, asılı etiket (delik + kesikli kenar). */}
              <span data-testid="favorite-price" className="relative inline-flex items-center rounded-md border border-dashed border-line bg-paper py-0.5 pr-3 pl-6 font-display font-bold tabular-nums before:absolute before:top-1/2 before:left-2 before:size-2 before:-translate-y-1/2 before:rounded-full before:border before:border-line before:bg-card">
                <span className="sr-only">Başlangıç fiyatı </span>{formatPriceMinor(item.startingPriceMinor)}
              </span>
              {!item.inStock && <span className="text-sm text-fg-muted">Stokta yok</span>}
            </p>
          : <p data-testid="favorite-unavailable" className="mt-1.5"><span className="inline-block rounded-md border border-dashed border-fg-muted px-2.5 py-0.5 text-sm font-semibold">Satışta değil</span></p>}
      </div>
      <div className="col-span-2 min-w-0 sm:col-span-1">
        <button type="button" id={`kaldir-${item.id}`} disabled={busy} onClick={onRemove} aria-label={`${item.name} favorilerden kaldır`} className={btnSecondary}>{busy ? "Kaldırılıyor…" : "Kaldır"}</button>
      </div>
    </li>
  );
}

export function FavoritesView() {
  useFavorites({ refresh: true }); // üst gezinti sayacı da güncel kalsın
  const [view, setView] = useState<View>({ kind: "loading" });
  const [busyId, setBusyId] = useState<string | null>(null);
  const [failure, setFailure] = useState<Failure | null>(null);
  const [announce, setAnnounce] = useState("");
  const focusRef = useRef<string | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);

  const load = useCallback(async () => {
    const result = await fetchFavorites();
    if (result.ok) setView({ kind: "ready", items: result.data.items });
    else if (result.status === 401) setView({ kind: "anonymous" });
    else setView({ kind: "error", message: result.message });
  }, []);
  useEffect(() => {
    let alive = true;
    void fetchFavorites().then((result) => {
      if (!alive) return;
      if (result.ok) setView({ kind: "ready", items: result.data.items });
      else if (result.status === 401) setView({ kind: "anonymous" });
      else setView({ kind: "error", message: result.message });
    });
    return () => { alive = false; };
  }, []);
  // bfcache'ten dönen sayfa önceki kimliğin listesini gösteriyor olabilir: listeyi yeniden oku.
  usePersistedPageshow(() => { setView({ kind: "loading" }); void load(); });

  // Silmeden sonra odak bir sonraki satırın "Kaldır" düğmesine, satır kalmadıysa başlığa gider (odak kaybolmaz).
  useEffect(() => {
    const id = focusRef.current;
    if (id === null) return;
    focusRef.current = null;
    (id === "heading" ? headingRef.current : document.getElementById(id))?.focus();
  }, [view]);

  async function remove(item: FavoriteItem, index: number, items: FavoriteItem[]) {
    if (busyId) return;
    setBusyId(item.id); setFailure(null);
    const others = items.filter((i) => i.id !== item.id);
    focusRef.current = others.length === 0 ? "heading" : `kaldir-${others[Math.min(index, others.length - 1)].id}`;
    const result = await setFavorite(item.id, false);
    setBusyId(null);
    if (result.ok) {
      setView({ kind: "ready", items: others });
      setAnnounce(`${item.name} favorilerden kaldırıldı. ${others.length} favori ürün kaldı.`);
    } else if (result.reason === "anonymous") {
      setView({ kind: "anonymous" });
    } else if (result.reason === "failed") {
      focusRef.current = null;
      setFailure({ id: item.id, text: result.message || "Ürün kaldırılamadı. Favorilerin değişmedi olabilir; tekrar dene." });
    }
  }

  let body;
  if (view.kind === "anonymous") {
    body = <div className={`${panel} mt-6`} data-testid="favorites-anonymous"><p className="font-semibold">Favorilerini görmek için giriş yapmalısın.</p><p className="mt-1 text-fg-muted">Favoriler hesabında saklanır; giriş yapmadan liste oluşturulmaz.</p><Link href={loginHref("/favorilerim")} className={`${btnPrimary} mt-4`}>Giriş yap</Link></div>;
  } else if (view.kind === "error") {
    body = <div role="alert" className={`${panel} mt-6`} data-testid="favorites-error"><p className="font-semibold">Favoriler yüklenemedi.</p><p className="mt-1 text-fg-muted">{view.message}</p><button type="button" onClick={() => { setView({ kind: "loading" }); void load(); }} className={`${btnPrimary} mt-4`}>Tekrar dene</button></div>;
  } else if (view.kind === "loading") {
    body = <p role="status" className="mt-6" data-testid="favorites-loading">Favoriler yükleniyor…</p>;
  } else if (view.items.length === 0) {
    body = <div className={`${panel} mt-6`} data-testid="favorites-empty"><p className="font-semibold">Henüz favori ürünün yok.</p><p className="mt-1 text-fg-muted">Ürün kartındaki kalbe dokunarak beğendiklerini buraya ekleyebilirsin.</p><Link href="/#urunler" className={`${btnPrimary} mt-4`}>Ürünlere göz at</Link></div>;
  } else {
    const items = view.items;
    body = <ul className="mt-6 space-y-4">
      {items.map((item, index) => <Fragment key={item.id}>
        <Row item={item} busy={busyId === item.id} onRemove={() => void remove(item, index, items)} />
        {failure?.id === item.id && <li role="alert" data-testid="favorite-failure" className="-mt-2 rounded-md border border-red-300 bg-red-50 p-3 text-red-800">{failure.text}</li>}
      </Fragment>)}
    </ul>;
  }
  return <div className="mx-auto max-w-3xl px-4 py-10 sm:px-6 md:py-14">
    <h1 ref={headingRef} tabIndex={-1} className="font-display text-title font-extrabold outline-none">Favorilerim</h1>
    {view.kind === "ready" && view.items.length > 0 && <p className="mt-1 text-fg-muted" data-testid="favorites-total">{view.items.length} ürün</p>}
    <p role="status" className="sr-only" data-testid="favorites-announce">{announce}</p>
    {body}
  </div>;
}

"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { HeartIcon } from "@/components/ui/Icons";
import { usePersistedPageshow } from "@/lib/page-restore";
import { resetFavorites, useFavorites } from "@/features/favorites/favorites";

// "Favorilerim" gerçek bağlantı. Sayaç yalnızca oturum açıksa ve en az bir favori varsa görünür; sayı tarayıcıda çerezle okunur (HTML'de kişisel veri yok).
function useCount() {
  const favorites = useFavorites();
  return favorites.status === "ready" ? favorites.ids.size : 0;
}
const badge = "absolute -top-1 -right-1 grid min-w-[1.25rem] place-items-center rounded-full bg-accent px-1 text-xs leading-5 font-bold text-ink tabular-nums";
const countText = (count: number) => count > 0 && <span className="sr-only">, {count} ürün</span>;

export function HeaderFavoritesLink() {
  // usePathname kullanılmaz: üst çubuk Suspense dışındadır ve yol okuyan bileşen prerender'ı bloklar.
  const count = useCount();
  // bfcache geri yüklemesinde önceki kimliğin kalpleri bellekte kalmasın.
  usePersistedPageshow(resetFavorites);
  return (
    <Link href="/favorilerim" className="inline-flex h-11 min-w-11 items-center justify-center gap-2.5 rounded-md px-2 text-on-ink hover:text-accent">
      <span className="relative"><HeartIcon />{count > 0 && <span aria-hidden="true" data-testid="favorites-count" className={badge}>{count}</span>}</span>
      <span className="text-[0.9375rem] font-medium max-lg:sr-only">Favorilerim{countText(count)}</span>
    </Link>
  );
}

export function BottomFavoritesLink({ className }: { className: (active: boolean) => string }) {
  const count = useCount();
  const onPage = usePathname() === "/favorilerim";
  return (
    <Link href="/favorilerim" aria-current={onPage ? "page" : undefined} className={className(onPage)}>
      <span className="relative"><HeartIcon />{count > 0 && <span aria-hidden="true" data-testid="favorites-count" className={badge}>{count}</span>}</span>
      <span>Favoriler{countText(count)}</span>
    </Link>
  );
}

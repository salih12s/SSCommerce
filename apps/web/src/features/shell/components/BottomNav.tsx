"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { BottomCartLink } from "@/features/cart/components/CartLink";
import { BottomFavoritesLink } from "@/features/favorites/components/FavoritesLink";
import { GridIcon, HomeIcon, UserIcon } from "@/components/ui/Icons";

// Mobil alt gezinti (açık zemin). Yalnızca gerçek sayfalara bağlanır: favoriler, sepet ve hesap sayaçlı/gerçek bağlantılardır.
// md ve üstünde gizlidir. İçeriği örtmemesi için gövdeye alt boşluk layout.tsx'te verilir.
const itemBase =
  "relative flex h-[4.5rem] w-full flex-col items-center justify-center gap-0.5 px-1 text-xs leading-[0.875rem] transition-colors duration-150";

export function BottomNav() {
  const pathname = usePathname();
  const onHome = pathname === "/";
  const onCategory = pathname.startsWith("/kategori");
  const onAccount = pathname.startsWith("/hesabim") || pathname.startsWith("/siparisler") || pathname === "/giris" || pathname === "/kayit";

  const live = (active: boolean) =>
    `${itemBase} ${active ? "font-semibold text-accent-deep" : "text-fg"} active:bg-paper-2`;

  return (
    <nav
      aria-label="Alt gezinti"
      className="fixed inset-x-0 bottom-0 z-40 border-t border-line bg-card pb-[env(safe-area-inset-bottom,0px)] text-fg shadow-[0_-8px_24px_-16px_rgb(60_40_20/0.4)] md:hidden"
    >
      <ul className="grid grid-cols-5">
        <li>
          <Link href="/" aria-current={onHome ? "page" : undefined} className={live(onHome)}>
            <HomeIcon />
            Ana sayfa
          </Link>
        </li>
        <li>
          <Link href="/#kategoriler" className={live(onCategory)}>
            <GridIcon />
            Kategoriler
          </Link>
        </li>
        <li><BottomFavoritesLink className={live} /></li>
        <li><BottomCartLink className={live} /></li>
        <li><Link href="/hesabim" aria-current={onAccount ? "page" : undefined} className={live(onAccount)}><UserIcon /><span>Hesabım</span></Link></li>
      </ul>
    </nav>
  );
}

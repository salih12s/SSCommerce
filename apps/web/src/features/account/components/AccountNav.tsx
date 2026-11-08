"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useRef } from "react";

// Hesap alanının ortak menüsü: Özet, Siparişlerim, Favorilerim, Adreslerim, Güvenlik. Beş hesap sayfasında da (app katmanı ekler) aynı yerde durur.
// Dil: mağaza etiketleri. Etiketler kesik çizgili kenarlıklı asılı etiket gibidir; bulunulan sayfa koyu "seçili etiket" olur.
// Yatay menü: dar ekranda yatay kaydırılır (kaydırma çubuğu gizli, bulunulan etiket görünür alana alınır), bölünmüş panel yoktur.
const items = [
  { href: "/hesabim", label: "Özet" },
  { href: "/siparisler", label: "Siparişlerim" },
  { href: "/favorilerim", label: "Favorilerim" },
  { href: "/hesabim/adresler", label: "Adreslerim" },
  { href: "/hesabim/guvenlik", label: "Güvenlik" },
] as const;

const base = "inline-flex min-h-11 shrink-0 snap-start items-center rounded-md border-2 px-4 font-display font-bold whitespace-nowrap transition-colors duration-150 ease-out motion-reduce:transition-none";
const idle = "border-dashed border-line bg-card text-fg hover:bg-paper-2";
const current = "border-solid border-ink bg-ink text-on-ink";

export function AccountNav() {
  const pathname = usePathname().replace(/\/+$/, "") || "/";
  const currentRef = useRef<HTMLAnchorElement>(null);
  // Mobilde bulunulan sayfanın etiketi ekran dışında kalmasın. Yalnız yatay kaydırır; sayfa dikeyde oynamaz.
  useEffect(() => { currentRef.current?.scrollIntoView({ block: "nearest", inline: "center" }); }, [pathname]);
  return <div className="mx-auto max-w-3xl px-4 pt-4 sm:px-6 md:pt-8">
    <nav aria-label="Hesap menüsü" data-testid="account-nav" className="no-scrollbar -mx-4 scroll-px-4 snap-x overflow-x-auto overscroll-x-contain px-4 py-2 sm:-mx-6 sm:scroll-px-6 sm:px-6">
      <ul className="flex w-max gap-2">
        {items.map((item) => {
          const active = pathname === item.href;
          return <li key={item.href}>
            <Link ref={active ? currentRef : undefined} href={item.href} aria-current={active ? "page" : undefined} className={`${base} ${active ? current : idle}`}>{item.label}</Link>
          </li>;
        })}
      </ul>
    </nav>
  </div>;
}

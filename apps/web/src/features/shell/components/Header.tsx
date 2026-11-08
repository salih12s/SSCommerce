import { UserIcon } from "@/components/ui/Icons";
import { HeaderCartLink } from "@/features/cart/components/CartLink";
import { HeaderFavoritesLink } from "@/features/favorites/components/FavoritesLink";
import Link from "next/link";
import { Suspense } from "react";
import { CategoryNav } from "@/features/catalog/components/CategoryNav";
import { Logo } from "./Logo";
import { MobileNav } from "./MobileNav";
import { SearchForm } from "@/features/catalog/components/SearchForm";

// Koyu üst bölge: demo uyarısı (sitede tek yer), logo + arama + hesap/favori/sepet, altta kategori menüsü.
// Favorilerim ve Sepetim gerçek bağlantılardır (sayaçlar tarayıcıda okunur). Mobilde hesap alt gezintidedir.
export function Header() {
  return (
    <header className="on-ink relative z-20 bg-ink text-on-ink">
      <p className="bg-accent px-4 py-1.5 text-center text-sm font-medium text-ink">
        Demo vitrin: ürünler ve fiyatlar örnektir, ödeme alınmaz.
      </p>
      <div className="mx-auto max-w-7xl px-4 sm:px-6">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2.5 py-3 md:flex-nowrap md:gap-x-8 md:py-4">
          <MobileNav><Suspense fallback={null}><CategoryNav variant="list" label="Kategoriler (menü)" /></Suspense></MobileNav>
          <Logo />
          <div className="order-last basis-full md:order-none md:max-w-2xl md:flex-1 md:basis-auto">
            <SearchForm />
          </div>
          <div className="ml-auto flex items-center gap-1 md:gap-3">
            <Link href="/hesabim" aria-label="Hesabım" className="max-md:hidden inline-flex min-h-11 items-center gap-2 rounded-md px-2 text-sm font-medium hover:text-accent"><UserIcon /><span>Hesabım</span></Link>
            <HeaderFavoritesLink />
            <HeaderCartLink />
          </div>
        </div>
        <div className="hidden border-t border-white/10 md:block">
          <Suspense fallback={null}><CategoryNav variant="bar" label="Kategoriler" /></Suspense>
        </div>
      </div>
    </header>
  );
}

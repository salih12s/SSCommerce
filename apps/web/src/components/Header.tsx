import { CategoryNav } from "./CategoryNav";
import { Logo } from "./Logo";
import { MobileNav } from "./MobileNav";
import { SearchForm } from "./SearchForm";

export function Header() {
  return (
    <header className="on-ink relative bg-ink text-on-ink">
      <p className="bg-accent px-4 py-1.5 text-center text-sm font-medium text-ink">
        Demo vitrin: ürünler ve fiyatlar örnektir, satış yakında.
      </p>
      <div className="mx-auto max-w-6xl px-4 sm:px-6">
        <div className="flex flex-wrap items-center gap-x-6 gap-y-3 py-3 md:py-4">
          <Logo />
          <MobileNav>
            <CategoryNav variant="list" label="Kategoriler (menü)" />
          </MobileNav>
          <div className="order-last basis-full md:order-none md:ml-auto md:max-w-md md:flex-1 md:basis-auto">
            <SearchForm />
          </div>
        </div>
        <div className="hidden border-t border-white/10 md:block">
          <CategoryNav variant="bar" label="Kategoriler" />
        </div>
      </div>
    </header>
  );
}

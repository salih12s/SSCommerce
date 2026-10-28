import Link from "next/link";
import { categories } from "@/content/demo-catalog";

const corporate = ["Hakkımızda", "İletişim", "Gizlilik ve KVKK", "Mesafeli satış sözleşmesi"];

export function Footer() {
  return (
    <footer className="on-ink bg-ink text-on-ink">
      <div className="mx-auto grid max-w-6xl gap-10 px-4 py-12 sm:px-6 md:grid-cols-3">
        <div>
          <p className="font-display text-xl font-bold">SSCommerce</p>
          <p className="mt-3 max-w-xs text-on-ink-muted">
            Demo vitrin. Ürünler, fiyatlar ve koleksiyonlar örnektir; satış ve ödeme henüz açık değil.
          </p>
        </div>
        <nav aria-label="Alt bilgi kategorileri">
          <p className="font-display text-lg font-bold">Kategoriler</p>
          <ul className="mt-3 space-y-1">
            {categories.map((c) => (
              <li key={c.slug}>
                <Link href={`/kategori/${c.slug}`} className="inline-block py-1.5 hover:text-accent">
                  {c.name}
                </Link>
              </li>
            ))}
            <li>
              <Link href="/status" className="inline-block py-1.5 hover:text-accent">
                Sistem durumu
              </Link>
            </li>
          </ul>
        </nav>
        <div>
          <p className="font-display text-lg font-bold">Kurumsal</p>
          <ul className="mt-3 space-y-1 text-on-ink-muted">
            {corporate.map((label) => (
              <li key={label} className="py-1.5">
                {label} <span className="ml-1 rounded-sm border border-white/25 px-1.5 py-px text-xs">Yakında</span>
              </li>
            ))}
          </ul>
        </div>
      </div>
      <p className="border-t border-white/10 px-4 py-4 text-center text-sm text-on-ink-muted">
        © SSCommerce demo. Gerçek ürün, kampanya ya da ödeme içermez.
      </p>
    </footer>
  );
}

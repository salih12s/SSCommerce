import Link from "next/link";
import { legalLinks } from "@/content/legal";
import { getCategories } from "@/features/catalog/catalog";

export async function Footer() {
  const result = await getCategories();
  const categories = result.state === "ok" ? result.data : [];
  return (
    <footer className="on-ink bg-ink text-on-ink">
      <div className="mx-auto grid max-w-7xl gap-10 px-4 py-12 sm:px-6 md:grid-cols-[1.2fr_1.6fr_1.2fr]">
        <div>
          <p className="font-display text-xl font-bold">SSCommerce</p>
          <p className="mt-3 max-w-xs text-on-ink-muted">
            Demo vitrin. Ürünler, fiyatlar ve kategoriler örnektir; ödeme henüz açık değil.
          </p>
        </div>
        <nav aria-label="Alt bilgi kategorileri">
          <p className="font-display text-lg font-bold">Kategoriler</p>
          <ul className="mt-3 grid grid-cols-2 gap-x-6">
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
            <li>
              <Link href="/gorsel-kaynaklari" className="inline-block py-1.5 hover:text-accent">
                Görsel kaynakları
              </Link>
            </li>
          </ul>
        </nav>
        <nav aria-label="Kurumsal">
          <p className="font-display text-lg font-bold">Kurumsal</p>
          <ul className="mt-3 text-on-ink-muted">
            {legalLinks.map((l) => (
              <li key={l.href}>
                <Link href={l.href} className="inline-block py-1.5 hover:text-accent">
                  {l.label}
                </Link>
              </li>
            ))}
          </ul>
        </nav>
      </div>
      <p className="border-t border-white/10 px-4 py-4 text-center text-sm text-on-ink-muted">
        © SSCommerce demo. Gerçek ürün, kampanya ya da ödeme içermez.
      </p>
    </footer>
  );
}

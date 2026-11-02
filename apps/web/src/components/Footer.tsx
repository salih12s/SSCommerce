import Link from "next/link";
import { getCategories } from "@/lib/catalog";

const corporate = ["Hakkımızda", "İletişim", "Gizlilik ve KVKK", "Mesafeli satış sözleşmesi"];

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

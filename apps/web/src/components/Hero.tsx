import Link from "next/link";
import { HeroScene } from "./art";

export function Hero() {
  return (
    <section aria-labelledby="hero-baslik" className="pt-10 pb-section md:pt-16">
      <div className="mx-auto max-w-6xl px-4 sm:px-6">
        <h1 id="hero-baslik" className="max-w-4xl font-display text-display font-extrabold">
          Masadan cebe, kulaktan eve: teknoloji tek yerde.
        </h1>
        <p className="mt-6 max-w-xl text-lg text-fg-muted">
          Bilgisayar, telefon, ses ve akıllı ev ürünlerini bir arada gör. Bu vitrin bir demodur; ürünler ve
          fiyatlar örnektir, satış henüz açılmadı.
        </p>
        <p className="mt-8">
          <Link
            href="#kategoriler"
            className="inline-flex h-12 items-center rounded-md bg-accent px-6 font-semibold text-ink shadow-card transition-transform duration-150 ease-out active:scale-[0.97]"
          >
            Kategorilere göz at
          </Link>
        </p>
        <figure className="mt-12 rounded-lg border border-line bg-grid bg-card/60 px-3 pt-6 pb-3 sm:px-8">
          <HeroScene className="block h-auto w-full" />
          <figcaption className="mt-1 flex flex-wrap justify-between gap-x-6 gap-y-1 font-mono text-[0.8125rem] text-fg-muted">
            <ul className="flex flex-wrap gap-y-1">
              {["SSC-1001", "SSC-2001", "SSC-2002", "SSC-3001", "SSC-4001"].map((code) => (
                <li key={code} className="whitespace-nowrap after:mx-2 after:content-['·'] last:after:content-none">
                  {code}
                </li>
              ))}
            </ul>
            <span>Çizim ölçekli değildir</span>
          </figcaption>
        </figure>
      </div>
    </section>
  );
}

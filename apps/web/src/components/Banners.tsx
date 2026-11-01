import Image from "next/image";
import Link from "next/link";
import { banners, type DemoBanner } from "@/content/demo-catalog";
import { ArrowIcon } from "./Icons";

// Üç kampanya benzeri banner (koyu / açık karışık). Gerçek kampanya yoktur: metinler kategori yönlendirmesidir.
function Banner({ banner, available }: { banner: DemoBanner; available: boolean }) {
  const dark = banner.tone === "dark";
  return (
    <article
      aria-labelledby={`banner-${banner.slug}`}
      className={`${dark ? "on-ink bg-ink text-on-ink" : "border border-line bg-card text-fg"} relative isolate flex min-h-44 overflow-hidden rounded-xl sm:min-h-48`}
    >
      <Image
        src={banner.image}
        alt={banner.imageAlt}
        fill
        sizes="(min-width: 1024px) 33vw, 100vw"
        className="-z-20 object-cover object-right"
      />
      <div
        aria-hidden="true"
        className={`absolute inset-0 -z-10 bg-gradient-to-r ${
          dark ? "from-ink from-35% via-ink/80 via-60% to-ink/0" : "from-card from-35% via-card/85 via-60% to-card/0"
        }`}
      />
      <div className="relative flex max-w-[68%] flex-col items-start justify-center gap-1.5 p-5 sm:p-6">
        <h2 id={`banner-${banner.slug}`} className="font-display text-xl leading-tight font-bold sm:text-2xl">
          {banner.title}
        </h2>
        <p className="text-[0.9375rem]">{banner.text}</p>
        {available ? <Link
          href={`/kategori/${banner.categorySlug}`}
          className={`mt-2 inline-flex h-10 items-center gap-1.5 rounded-md px-3.5 text-sm font-semibold transition-transform duration-150 ease-out active:scale-[0.97] ${
            dark ? "bg-accent text-ink" : "bg-ink text-on-ink"
          }`}
        >
          {banner.cta}
          <ArrowIcon className="size-4" />
        </Link> : <span className="mt-2 rounded-md border border-current px-3.5 py-2 text-sm font-semibold">Yakında</span>}
      </div>
    </article>
  );
}

export function Banners({ availableSlugs }: { availableSlugs: ReadonlySet<string> }) {
  return (
    <section aria-label="Öne çıkan kategoriler" className="pt-8 md:pt-10">
      <div className="mx-auto grid max-w-7xl gap-3 px-4 sm:px-6 lg:grid-cols-3 lg:gap-4">
        {banners.map((b) => (
          <Banner key={b.slug} banner={b} available={availableSlugs.has(b.categorySlug)} />
        ))}
      </div>
    </section>
  );
}

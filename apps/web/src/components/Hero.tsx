import Image from "next/image";
import Link from "next/link";
import { hero, promos } from "@/content/demo-catalog";
import { ArrowIcon } from "./Icons";

// Koyu hero (D-025): solda fotoğraflı sahne (başlık + 2 bağlantı, okunurluk için kömür scrim), sağda iki dikey tanıtım kartı.
// Slider yok: noktalar işlevsiz kalacağı için konmadı. Mobilde sahne tam genişlik, tanıtım kartları altta yan yana.
const primary =
  "inline-flex h-12 items-center gap-2 rounded-md bg-accent px-5 font-semibold text-ink transition-transform duration-150 ease-out active:scale-[0.97]";
const secondary =
  "inline-flex h-12 items-center rounded-md border border-white/30 px-5 font-semibold text-on-ink transition-[transform,background-color] duration-150 ease-out active:scale-[0.97] [@media(hover:hover)]:hover:bg-white/10";

export function Hero() {
  return (
    <section aria-labelledby="hero-baslik" className="on-ink bg-ink text-on-ink">
      <div className="mx-auto grid max-w-7xl gap-3 px-4 pb-5 sm:px-6 lg:grid-cols-12 lg:gap-4 lg:pb-6">
        <div
          data-testid="hero-scene"
          className="relative isolate min-h-[26rem] overflow-hidden rounded-xl bg-ink-2 lg:col-span-8 lg:min-h-[28rem]"
        >
          {/* Fotoğraf masaüstünde sahnenin sağ %62'ini kaplar: monitör metnin sağında kalır. Koyu kaynak, parlaklık artırılarak okunur hale getirildi. */}
          <div className="absolute inset-y-0 right-0 -z-20 w-full lg:w-[62%]">
            <Image
              src={hero.image}
              alt={hero.imageAlt}
              fill
              preload
              sizes="(min-width: 1280px) 660px, (min-width: 1024px) 52vw, 100vw"
              className="object-cover object-[45%_50%] [filter:brightness(1.45)_saturate(1.1)]"
            />
          </div>
          {/* Okunurluk scrim'i: mobilde üstten, masaüstünde soldan kömür; metin altında koyu zemin kalır (AA). */}
          <div
            aria-hidden="true"
            className="absolute inset-0 -z-10 bg-gradient-to-b from-ink from-30% via-ink/70 via-50% to-transparent lg:bg-gradient-to-r lg:from-ink lg:from-34% lg:via-ink/65 lg:via-50% lg:to-transparent"
          />
          <div className="relative flex h-full flex-col justify-start p-5 sm:p-8 lg:justify-center lg:p-12">
            <h1
              id="hero-baslik"
              className="max-w-md font-display text-[2.25rem] leading-[1.05] font-extrabold tracking-[-0.03em] sm:text-5xl lg:text-[3.25rem]"
            >
              Masadan cebe, kulaktan eve.
            </h1>
            <p className="mt-4 hidden max-w-sm text-lg text-on-ink sm:block">
              Bilgisayar, telefon, ses ve akıllı ev ürünleri tek vitrinde.
            </p>
            <p className="mt-5 flex flex-wrap gap-3 sm:mt-7">
              <Link href="#urunler" className={primary}>
                Ürünleri keşfet
                <ArrowIcon />
              </Link>
              <Link href="#kategoriler" className={secondary}>
                Kategoriler
              </Link>
            </p>
          </div>
        </div>

        <ul className="grid grid-cols-2 gap-3 sm:grid-cols-1 lg:col-span-4 lg:grid-rows-2 lg:gap-4">
          {promos.map((p) => (
            <li key={p.slug} className="min-w-0">
              <article className="relative flex h-full overflow-hidden rounded-xl bg-ink-2 ring-1 ring-white/10 max-sm:flex-col">
                <div className="relative aspect-[4/3] shrink-0 sm:order-2 sm:aspect-auto sm:min-h-36 sm:w-[46%]">
                  <Image
                    src={p.image}
                    alt={p.imageAlt}
                    fill
                    sizes="(min-width: 1280px) 190px, (min-width: 1024px) 15vw, 50vw"
                    className="object-cover"
                  />
                </div>
                <div className="flex flex-1 flex-col justify-center gap-1.5 p-3.5 sm:w-[54%] sm:p-5 lg:gap-2">
                  <h2 className="font-display text-base leading-tight font-bold sm:text-xl lg:text-[1.375rem]">{p.title}</h2>
                  <p className="hidden text-[0.9375rem] text-on-ink-muted sm:block">{p.text}</p>
                  <p className="mt-1.5 lg:mt-3">
                    <Link
                      href={`/kategori/${p.categorySlug}`}
                      className="inline-flex h-10 items-center gap-1.5 rounded-md bg-card px-3 text-sm font-semibold text-fg transition-transform duration-150 ease-out active:scale-[0.97]"
                    >
                      {p.cta}
                      <ArrowIcon className="size-4" />
                    </Link>
                  </p>
                </div>
              </article>
            </li>
          ))}
        </ul>
      </div>
    </section>
  );
}

import Image from "next/image";
import Link from "next/link";
import type { DemoCategory } from "@/content/demo-catalog";

// Beyaz kart: üstte kategori fotoğrafı (hepsi 4:3), altta ad ve alt metin. Mobilde alt metin gizlenir (4x2 kompakt ızgara).
export function CategoryCard({ category, available = true }: { category: DemoCategory; available?: boolean }) {
  const content = <>
      <span className="relative block aspect-[4/3] overflow-hidden bg-paper-2">
        <Image src={category.image} alt={category.imageAlt} fill sizes="(min-width: 1280px) 150px, (min-width: 768px) 24vw, 23vw" className="object-cover transition-transform duration-500 ease-out-strong motion-safe:[@media(hover:hover)]:group-hover:scale-[1.04]" />
      </span>
      <span className="flex flex-1 flex-col gap-0.5 px-1.5 py-2 text-center md:px-3 md:py-3 md:text-left">
        <span className="text-[0.8125rem] leading-tight font-semibold group-hover:underline md:text-[0.9375rem] md:font-bold">{category.name}</span>
        <span className="hidden text-[0.8125rem] leading-snug text-fg-muted md:block">{available ? category.blurb : "Yakında"}</span>
        {!available && <span className="text-xs text-fg-muted md:hidden">Yakında</span>}
      </span>
    </>;
  if (!available) return <div aria-label={`${category.name}, yakında`} className="group flex h-full flex-col overflow-hidden rounded-lg border border-line bg-card shadow-card">{content}</div>;
  return (
    <Link
      href={`/kategori/${category.slug}`}
      className="group flex h-full flex-col overflow-hidden rounded-lg border border-line bg-card shadow-card transition-shadow duration-200 [@media(hover:hover)]:hover:shadow-lift"
    >
      {content}
    </Link>
  );
}

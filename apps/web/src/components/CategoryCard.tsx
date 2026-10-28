import Link from "next/link";
import { productsInCategory, type DemoCategory } from "@/content/demo-catalog";
import { ProductArt } from "./art";

export function CategoryCard({ category }: { category: DemoCategory }) {
  const count = productsInCategory(category.slug).length;
  return (
    <Link
      href={`/kategori/${category.slug}`}
      className="group flex h-full min-h-11 flex-col overflow-hidden rounded-md border border-line bg-card shadow-card transition-[transform,box-shadow] duration-200 ease-out-strong hover:-translate-y-0.5 hover:shadow-lift active:scale-[0.99]"
    >
      <span className="block border-b border-line bg-grid bg-paper-2/70 px-3 py-3 sm:px-6 sm:py-4">
        <ProductArt kind={category.art} alt="" className="mx-auto h-14 w-auto sm:h-24" />
      </span>
      <span className="flex flex-1 flex-col gap-1 p-3 sm:p-5">
        <span className="font-display text-base leading-tight font-bold group-hover:underline sm:text-xl">
          {category.name}
        </span>
        <span className="hidden text-[0.9375rem] text-fg-muted sm:block">{category.blurb}</span>
        <span className="mt-auto pt-2 font-mono text-[0.75rem] text-fg-muted sm:pt-3 sm:text-[0.8125rem]">
          {count} örnek ürün
        </span>
      </span>
    </Link>
  );
}

import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";
import { ProductCard } from "@/components/ProductCard";
import { categories, getCategory, productsInCategory } from "@/content/demo-catalog";

export function generateStaticParams() {
  return categories.map((c) => ({ slug: c.slug }));
}

export async function generateMetadata({ params }: PageProps<"/kategori/[slug]">): Promise<Metadata> {
  const { slug } = await params;
  const category = getCategory(slug);
  return { title: category ? category.name : "Kategori bulunamadı" };
}

export default async function CategoryPage({ params }: PageProps<"/kategori/[slug]">) {
  const { slug } = await params;
  const category = getCategory(slug);
  if (!category) notFound();
  const items = productsInCategory(category.slug);

  return (
    <div className="mx-auto max-w-6xl px-4 py-section sm:px-6">
      <h1 className="font-display text-title font-extrabold">{category.name}</h1>
      <p className="mt-4 max-w-xl rounded-md border border-dashed border-fg-muted bg-card px-4 py-3 text-fg-muted">
        Bu kategori Faz 3&apos;te açılacak. Aşağıdakiler yalnızca örnek ürünlerdir.
      </p>
      <ul className="mt-10 grid grid-cols-2 gap-3 sm:gap-5 lg:grid-cols-4">
        {items.map((p) => (
          <li key={p.code}>
            <ProductCard product={p} />
          </li>
        ))}
      </ul>
      <p className="mt-10">
        <Link href="/" className="font-medium text-accent-deep underline underline-offset-4">
          Ana sayfaya dön
        </Link>
      </p>
    </div>
  );
}

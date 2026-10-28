import Link from "next/link";
import { categories } from "@/content/demo-catalog";

type Props = { variant: "bar" | "list"; label: string };

export function CategoryNav({ variant, label }: Props) {
  const linkBase = "block text-on-ink transition-colors duration-150";
  return (
    <nav aria-label={label}>
      <ul className={variant === "bar" ? "flex flex-wrap gap-x-8" : "flex flex-col"}>
        {categories.map((c) => (
          <li key={c.slug}>
            <Link
              href={`/kategori/${c.slug}`}
              className={
                variant === "bar"
                  ? `${linkBase} py-3 text-[0.9375rem] font-medium hover:text-accent`
                  : `${linkBase} border-t border-white/10 py-3.5 text-lg font-medium`
              }
            >
              {c.name}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}

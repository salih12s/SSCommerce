import Link from "next/link";
import { getCategories } from "@/lib/catalog";

type Props = { variant: "bar" | "list"; label: string };

export async function CategoryNav({ variant, label }: Props) {
  const result = await getCategories();
  const categories = result.state === "ok" ? result.data : [];
  const linkBase = "block text-on-ink transition-colors duration-150";
  return (
    <nav aria-label={label}>
      <ul className={variant === "bar" ? "no-scrollbar -mx-2 flex gap-x-1 overflow-x-auto" : "flex flex-col"}>
        {categories.map((c) => (
          <li key={c.slug} className="shrink-0">
            <Link
              href={`/kategori/${c.slug}`}
              className={
                variant === "bar"
                  ? `${linkBase} px-2.5 py-3 text-[0.9375rem] font-medium whitespace-nowrap hover:text-accent`
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

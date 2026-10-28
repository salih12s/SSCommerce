import Link from "next/link";

export function Logo() {
  return (
    <Link href="/" className="flex items-center gap-2.5 py-1 text-on-ink">
      <svg viewBox="0 0 32 32" width="32" height="32" aria-hidden="true" focusable="false">
        <path d="M4 4h18l6 6v18H4z" className="fill-accent" />
        <path d="M22 4v6h6" fill="rgb(22 24 27 / 0.25)" />
        <text
          x="15"
          y="23"
          textAnchor="middle"
          fontSize="15"
          fontWeight="800"
          className="fill-ink"
          style={{ fontFamily: "var(--font-display)" }}
        >
          SS
        </text>
      </svg>
      <span className="font-display text-xl font-bold tracking-tight">SSCommerce</span>
    </Link>
  );
}

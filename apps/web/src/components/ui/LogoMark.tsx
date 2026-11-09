// Logo A · Etiket: -10° eğik, ipli fiyat etiketi. Yalnız çizim; bağlantı ve yazı Logo.tsx'te.
// ground: deliğin rengi, yani simgenin üstünde durduğu zemin. "ink" kömür üst şerit, "paper" açık sayfa.
// Kesik çizgi (fiyat etiketi perforasyonu) küçük boyutta gürültü olur: yalnız 40px ve üstünde çizilir.
type Props = { ground?: "ink" | "paper"; size?: number; className?: string };

export function LogoMark({ ground = "ink", size = 40, className }: Props) {
  const dark = ground === "ink";
  return (
    <svg
      viewBox="0 0 64 64"
      width={size}
      height={size}
      aria-hidden="true"
      focusable="false"
      className={className}
    >
      <g transform="rotate(-10 32 34)">
        <path
          d="M22 10 H42 L54 23 V54 Q54 58 50 58 H14 Q10 58 10 54 V23 Z"
          className="fill-accent"
        />
        <circle
          cx="32"
          cy="21"
          r="4"
          className={dark ? "fill-ink" : "fill-paper"}
        />
        {size >= 40 && (
          <path
            d="M15 30 H49"
            className="stroke-ink"
            strokeOpacity="0.35"
            strokeWidth="1.4"
            strokeDasharray="3 3"
            fill="none"
          />
        )}
        <text
          x="32"
          y="51"
          textAnchor="middle"
          fontSize="20"
          fontWeight="800"
          className="fill-ink"
          style={{ fontFamily: "var(--font-display)" }}
        >
          SS
        </text>
        <path
          d="M32 21 C31 11 37 4 46 3"
          fill="none"
          strokeWidth="2"
          strokeLinecap="round"
          className={dark ? "stroke-on-ink" : "stroke-ink"}
        />
      </g>
    </svg>
  );
}

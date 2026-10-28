import type { ArtKind } from "@/content/demo-catalog";

// Yerel, telifsiz çizgi illüstrasyonlar. Her çizim 160x120 birim kutuda durur.
// Renkler tasarım token'larından gelir (fill-ink, fill-screen, fill-accent ...).

export function ArtShape({ kind }: { kind: ArtKind }) {
  switch (kind) {
    case "laptop":
      return (
        <g>
          <rect x="30" y="14" width="100" height="66" rx="5" className="fill-ink" />
          <rect x="35" y="19" width="90" height="56" rx="2" className="fill-screen" />
          <rect x="42" y="27" width="34" height="5" rx="2.5" className="fill-accent" />
          <rect x="42" y="38" width="62" height="3" rx="1.5" className="fill-on-ink-muted" />
          <rect x="42" y="46" width="48" height="3" rx="1.5" className="fill-on-ink-muted" />
          <rect x="42" y="54" width="56" height="3" rx="1.5" className="fill-on-ink-muted" />
          <path
            d="M14 84h132l-6 10q-1 3-4 3H24q-3 0-4-3z"
            className="fill-card stroke-ink"
            strokeWidth="2"
            strokeLinejoin="round"
          />
          <rect x="66" y="84" width="28" height="4" rx="2" className="fill-line" />
        </g>
      );
    case "monitor":
      return (
        <g>
          <rect x="20" y="12" width="120" height="72" rx="5" className="fill-ink" />
          <rect x="26" y="18" width="108" height="60" rx="2" className="fill-screen" />
          <path d="M34 66l20-18 14 12 16-22 22 28z" className="fill-accent" />
          <circle cx="112" cy="34" r="6" className="fill-on-ink" />
          <path d="M72 84l-4 17h24l-4-17z" className="fill-card stroke-ink" strokeWidth="2" strokeLinejoin="round" />
          <rect x="52" y="101" width="56" height="6" rx="3" className="fill-ink" />
        </g>
      );
    case "phone":
      return (
        <g>
          <rect x="54" y="6" width="52" height="108" rx="10" className="fill-ink" />
          <rect x="59" y="11" width="42" height="98" rx="6" className="fill-screen" />
          <rect x="73" y="15" width="14" height="4" rx="2" className="fill-ink" />
          <circle cx="80" cy="52" r="15" className="fill-accent" />
          <rect x="66" y="78" width="28" height="4" rx="2" className="fill-on-ink-muted" />
          <rect x="70" y="87" width="20" height="4" rx="2" className="fill-on-ink-muted" />
        </g>
      );
    case "watch":
      return (
        <g>
          <rect x="62" y="4" width="36" height="32" rx="7" className="fill-line" />
          <rect x="62" y="84" width="36" height="32" rx="7" className="fill-line" />
          <rect x="49" y="29" width="62" height="62" rx="17" className="fill-ink" />
          <rect x="55" y="35" width="50" height="50" rx="12" className="fill-screen" />
          <circle
            cx="80"
            cy="60"
            r="15"
            fill="none"
            className="stroke-accent"
            strokeWidth="5"
            strokeDasharray="62 100"
            strokeLinecap="round"
          />
          <circle cx="80" cy="60" r="3" className="fill-on-ink" />
          <rect x="111" y="52" width="4" height="14" rx="2" className="fill-ink" />
        </g>
      );
    case "headphones":
      return (
        <g>
          <path
            d="M38 72V62a42 42 0 0 1 84 0v10"
            fill="none"
            className="stroke-ink"
            strokeWidth="8"
            strokeLinecap="round"
          />
          <rect x="26" y="64" width="22" height="42" rx="10" className="fill-ink" />
          <rect x="112" y="64" width="22" height="42" rx="10" className="fill-ink" />
          <rect x="31" y="72" width="8" height="26" rx="4" className="fill-accent" />
          <rect x="121" y="72" width="8" height="26" rx="4" className="fill-accent" />
        </g>
      );
    case "speaker":
      return (
        <g>
          <rect x="48" y="14" width="64" height="94" rx="16" className="fill-ink" />
          {[34, 48, 62, 76].map((y) =>
            [62, 74, 86, 98].map((x) => (
              <circle key={`${x}-${y}`} cx={x} cy={y} r="2.2" className="fill-on-ink-muted" />
            )),
          )}
          <rect x="48" y="86" width="64" height="7" className="fill-accent" />
          <circle cx="80" cy="100" r="3" className="fill-on-ink" />
        </g>
      );
    case "bulb":
      return (
        <g>
          <path
            d="M80 4v8M42 18l6 6M118 18l-6 6M28 52h8M124 52h8"
            className="stroke-accent"
            strokeWidth="3"
            strokeLinecap="round"
          />
          <circle cx="80" cy="52" r="28" className="fill-card stroke-ink" strokeWidth="2.5" />
          <path
            d="M72 66V56a8 8 0 1 1 16 0v10"
            fill="none"
            className="stroke-accent"
            strokeWidth="3"
            strokeLinejoin="round"
            strokeLinecap="round"
          />
          <path d="M68 78h24v10H68z" className="fill-line stroke-ink" strokeWidth="2" strokeLinejoin="round" />
          <rect x="66" y="88" width="28" height="10" rx="3" className="fill-ink" />
          <rect x="72" y="98" width="16" height="6" rx="3" className="fill-ink" />
        </g>
      );
    case "sensor":
      return (
        <g>
          <circle cx="80" cy="60" r="44" className="fill-ink" />
          <circle cx="80" cy="60" r="35" className="fill-screen" />
          <path d="M52 78a35 35 0 0 1 56 0" fill="none" className="stroke-accent" strokeWidth="5" strokeLinecap="round" />
          <text
            x="80"
            y="66"
            textAnchor="middle"
            className="fill-on-ink"
            fontSize="20"
            fontWeight="600"
            style={{ fontFamily: "var(--font-mono)" }}
          >
            21°
          </text>
        </g>
      );
  }
}

export function ProductArt({ kind, alt, className }: { kind: ArtKind; alt: string; className?: string }) {
  return (
    <svg
      viewBox="0 0 160 120"
      {...(alt ? { role: "img", "aria-label": alt } : { "aria-hidden": true, focusable: false })}
      className={className}
    >
      <ArtShape kind={kind} />
    </svg>
  );
}

// Hero çizimi: beş ürün, tek ölçü çizgisi üzerinde.
const heroItems: { kind: ArtKind; cx: number; scale: number }[] = [
  { kind: "laptop", cx: 170, scale: 1.7 },
  { kind: "phone", cx: 400, scale: 1.55 },
  { kind: "watch", cx: 580, scale: 1.35 },
  { kind: "headphones", cx: 750, scale: 1.5 },
  { kind: "bulb", cx: 905, scale: 1.45 },
];

export function HeroScene({ className }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 1000 290"
      role="img"
      aria-label="Dizüstü bilgisayar, telefon, akıllı saat, kulaklık ve akıllı ampulün yan yana dizildiği çizgi illüstrasyon"
      className={className}
    >
      {heroItems.map(({ kind, cx, scale }) => (
        <g key={kind} transform={`translate(${cx - 80 * scale} ${232 - 118 * scale}) scale(${scale})`}>
          <ArtShape kind={kind} />
        </g>
      ))}
      {/* ölçü çizgisi */}
      <g className="stroke-fg-muted" strokeWidth="1.5" fill="none">
        <path d="M20 256h960M20 248v16M980 248v16" />
        {[120, 240, 360, 480, 600, 720, 840].map((x) => (
          <path key={x} d={`M${x} 252v8`} />
        ))}
      </g>
    </svg>
  );
}

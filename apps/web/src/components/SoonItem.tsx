import type { ReactNode } from "react";

// Henüz olmayan özellik (hesap, favori, sepet). Bağlantı değildir ve çalışıyormuş gibi görünmez:
// aria-disabled düğme, odaklanabilir ama hiçbir şey yapmaz; "Yakında" yazısı hem görünür hem erişilebilir addadır.
// `compact`: dar ekranda yalnızca simge görünür, metin ekran okuyucuya kalır.
export function SoonItem({
  label,
  icon,
  compact = false,
  className = "",
}: {
  label: string;
  icon: ReactNode;
  compact?: boolean;
  className?: string;
}) {
  return (
    <button
      type="button"
      aria-disabled="true"
      className={`flex h-11 min-w-11 cursor-not-allowed items-center justify-center gap-2.5 rounded-md px-2 text-on-ink-muted ${className}`}
    >
      {icon}
      <span className={`flex flex-col text-left leading-tight ${compact ? "max-lg:sr-only" : ""}`}>
        <span className="text-[0.9375rem] font-medium">{label}</span>
        <span className="text-xs">Yakında</span>
      </span>
    </button>
  );
}

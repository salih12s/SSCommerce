import Link from "next/link";
import { LogoMark } from "@/components/ui/LogoMark";

// Yatay kilit: 40px etiket simgesi + yazı. Koyu üst şeritte yazı ve ip krem (on-ink).
// aria-label YOK: bağlantının adı görünen "SSCommerce" yazısıdır. Ayrı bir etiket axe label-content-name-mismatch (WCAG 2.5.3) verir,
// çünkü axe SVG içindeki "SS" metnini de görünen metne katar.
export function Logo() {
  return (
    <Link href="/" className="flex min-h-11 items-center gap-2.5 text-on-ink">
      <LogoMark ground="ink" size={40} />
      <span className="font-display text-xl font-bold tracking-[-0.02em]">
        SSCommerce
      </span>
    </Link>
  );
}

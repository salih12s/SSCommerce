import type { ReactNode } from "react";

// Küçük, tek çizgi kalınlıklı simgeler. Hepsi dekoratiftir (aria-hidden); anlam yanındaki metindedir.
type IconProps = { className?: string };

function Svg({ className, children }: IconProps & { children: ReactNode }) {
  return (
    <svg
      viewBox="0 0 24 24"
      width="22"
      height="22"
      aria-hidden="true"
      focusable="false"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
    >
      {children}
    </svg>
  );
}

export function HomeIcon({ className }: IconProps) {
  return (
    <Svg className={className}>
      <path d="M4 11.5 12 5l8 6.5" />
      <path d="M6 10.5V19h12v-8.5" />
      <path d="M10 19v-5h4v5" />
    </Svg>
  );
}

export function GridIcon({ className }: IconProps) {
  return (
    <Svg className={className}>
      <rect x="4.5" y="4.5" width="6" height="6" rx="1.2" />
      <rect x="13.5" y="4.5" width="6" height="6" rx="1.2" />
      <rect x="4.5" y="13.5" width="6" height="6" rx="1.2" />
      <rect x="13.5" y="13.5" width="6" height="6" rx="1.2" />
    </Svg>
  );
}

export function SearchIcon({ className }: IconProps) {
  return (
    <Svg className={className}>
      <circle cx="11" cy="11" r="6" />
      <path d="m20 20-4.2-4.2" />
    </Svg>
  );
}

export function BagIcon({ className }: IconProps) {
  return (
    <Svg className={className}>
      <path d="M5.5 8.5h13l-1 11h-11z" />
      <path d="M9 8.5V7a3 3 0 0 1 6 0v1.5" />
    </Svg>
  );
}

export function UserIcon({ className }: IconProps) {
  return (
    <Svg className={className}>
      <circle cx="12" cy="8.5" r="3.5" />
      <path d="M5 19.5c.8-3.4 3.6-5 7-5s6.2 1.6 7 5" />
    </Svg>
  );
}

export function HeartIcon({ className }: IconProps) {
  return (
    <Svg className={className}>
      <path d="M12 19.5s-7-4.3-7-9.4A4 4 0 0 1 12 7.8a4 4 0 0 1 7 2.3c0 5.1-7 9.4-7 9.4z" />
    </Svg>
  );
}

export function MenuIcon({ className }: IconProps) {
  return (
    <Svg className={className}>
      <path d="M4 7h16M4 12h16M4 17h16" />
    </Svg>
  );
}

export function CloseIcon({ className }: IconProps) {
  return (
    <Svg className={className}>
      <path d="m6 6 12 12M18 6 6 18" />
    </Svg>
  );
}

export function ArrowIcon({ className }: IconProps) {
  return (
    <Svg className={className}>
      <path d="M5 12h14M13 6l6 6-6 6" />
    </Svg>
  );
}

export function TruckIcon({ className }: IconProps) {
  return (
    <Svg className={className}>
      <path d="M3.5 6.5h10v9h-10z" />
      <path d="M13.5 9.5h4l3 3v3h-7" />
      <circle cx="7.5" cy="17" r="1.8" />
      <circle cx="16.5" cy="17" r="1.8" />
    </Svg>
  );
}

export function ReturnIcon({ className }: IconProps) {
  return (
    <Svg className={className}>
      <path d="M8 6 4.5 9.5 8 13" />
      <path d="M4.5 9.5H14a5 5 0 0 1 0 10h-4" />
    </Svg>
  );
}

export function CardIcon({ className }: IconProps) {
  return (
    <Svg className={className}>
      <rect x="3.5" y="6" width="17" height="12" rx="2" />
      <path d="M3.5 10.5h17M7 15h3" />
    </Svg>
  );
}

export function ShieldIcon({ className }: IconProps) {
  return (
    <Svg className={className}>
      <path d="M12 4.5 5.5 7v5c0 3.7 2.6 6.3 6.5 7.5 3.9-1.2 6.5-3.8 6.5-7.5V7z" />
      <path d="m9.2 12 2 2 3.6-3.8" />
    </Svg>
  );
}

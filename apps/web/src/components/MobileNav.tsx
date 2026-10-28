"use client";

import { useEffect, useRef, useState, type ReactNode } from "react";

// Açılır kategori menüsü: aria-expanded, aria-controls, Esc ile kapanır,
// açılınca odak menüye gider, kapanınca düğmeye döner.
export function MobileNav({ children }: { children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    panelRef.current?.querySelector<HTMLElement>("a")?.focus();

    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        setOpen(false);
        buttonRef.current?.focus();
      }
    }
    function onPointerDown(event: PointerEvent) {
      const target = event.target as Node;
      if (!panelRef.current?.contains(target) && !buttonRef.current?.contains(target)) {
        setOpen(false);
      }
    }
    document.addEventListener("keydown", onKeyDown);
    document.addEventListener("pointerdown", onPointerDown);
    return () => {
      document.removeEventListener("keydown", onKeyDown);
      document.removeEventListener("pointerdown", onPointerDown);
    };
  }, [open]);

  return (
    <div className="ml-auto md:hidden">
      <button
        ref={buttonRef}
        type="button"
        aria-expanded={open}
        aria-controls="mobil-menu"
        onClick={() => setOpen((v) => !v)}
        className="flex h-11 items-center gap-2 rounded-md border border-white/25 px-3.5 font-medium text-on-ink transition-transform duration-150 ease-out active:scale-[0.97]"
      >
        <svg
          viewBox="0 0 20 20"
          width="20"
          height="20"
          aria-hidden="true"
          focusable="false"
          className="stroke-current"
          fill="none"
          strokeWidth="2"
          strokeLinecap="round"
        >
          {open ? <path d="M4 4l12 12M16 4L4 16" /> : <path d="M3 6h14M3 10h14M3 14h14" />}
        </svg>
        Kategoriler
      </button>
      <div
        ref={panelRef}
        id="mobil-menu"
        hidden={!open}
        onClick={(event) => {
          if ((event.target as HTMLElement).closest("a")) setOpen(false);
        }}
        className="absolute inset-x-0 top-full z-30 border-t border-white/10 bg-ink px-4 pb-4 shadow-lift"
      >
        {children}
      </div>
    </div>
  );
}

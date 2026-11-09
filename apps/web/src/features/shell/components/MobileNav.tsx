"use client";

import { useEffect, useRef, useState, type ReactNode } from "react";
import { CloseIcon, MenuIcon } from "@/components/ui/Icons";

// Açılır kategori menüsü: aria-expanded, aria-controls, Esc ile kapanır,
// açılınca odak menüye gider, kapanınca düğmeye döner.
export function MobileNav({ children }: { children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const panel = panelRef.current;
    function focusFirstLink() {
      const link = panel?.querySelector<HTMLElement>("a");
      if (!link) return false;
      link.focus();
      return true;
    }
    const observer = new MutationObserver(() => {
      // Do not steal focus if the user has already moved away while links streamed in.
      if (document.activeElement === buttonRef.current && focusFirstLink()) observer.disconnect();
    });
    if (!focusFirstLink() && panel) observer.observe(panel, { childList: true, subtree: true });

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
      observer.disconnect();
      document.removeEventListener("keydown", onKeyDown);
      document.removeEventListener("pointerdown", onPointerDown);
    };
  }, [open]);

  return (
    <div className="md:hidden">
      <button
        ref={buttonRef}
        type="button"
        aria-expanded={open}
        aria-controls="mobil-menu"
        aria-label="Kategoriler"
        onClick={() => setOpen((v) => !v)}
        className="grid size-11 place-items-center rounded-md text-on-ink transition-transform duration-150 ease-out active:scale-[0.97]"
      >
        {open ? <CloseIcon /> : <MenuIcon />}
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

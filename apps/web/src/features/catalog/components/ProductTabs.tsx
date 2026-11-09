"use client";

import { useId, useRef, useState, type KeyboardEvent, type ReactNode } from "react";

// Ürün sekmeleri: kartlar sunucuda bir kez üretilir (ProductCard sunucu bileşeni kalır), burada yalnızca süzülür.
// Her kartın `group` değeri (kategori) seçili sekmeyle eşleşmiyorsa gizlenir. Ok tuşları, Home ve End sekmeler arasında gezinir.
export type TabDef = { id: string; label: string };
export type TabItem = { key: string; groups: string[]; node: ReactNode };

export function ProductTabs({
  tabs,
  items,
  label,
  gridClass,
}: {
  tabs: TabDef[];
  items: TabItem[];
  label: string;
  gridClass: string;
}) {
  const [active, setActive] = useState(tabs[0]?.id);
  const base = useId();
  const refs = useRef<Record<string, HTMLButtonElement | null>>({});

  function onKeyDown(event: KeyboardEvent<HTMLButtonElement>, index: number) {
    let next = index;
    if (event.key === "ArrowRight") next = (index + 1) % tabs.length;
    else if (event.key === "ArrowLeft") next = (index - 1 + tabs.length) % tabs.length;
    else if (event.key === "Home") next = 0;
    else if (event.key === "End") next = tabs.length - 1;
    else return;
    event.preventDefault();
    setActive(tabs[next].id);
    refs.current[tabs[next].id]?.focus();
  }

  return (
    <div>
      <div
        role="tablist"
        aria-label={label}
        className="no-scrollbar -mx-4 flex gap-1 overflow-x-auto border-b border-line px-4 sm:mx-0 sm:px-0"
      >
        {tabs.map((tab, index) => {
          const selected = tab.id === active;
          return (
            <button
              key={tab.id}
              ref={(el) => {
                refs.current[tab.id] = el;
              }}
              type="button"
              role="tab"
              id={`${base}-tab-${tab.id}`}
              aria-selected={selected}
              aria-controls={`${base}-panel`}
              tabIndex={selected ? 0 : -1}
              onClick={() => setActive(tab.id)}
              onKeyDown={(event) => onKeyDown(event, index)}
              className={`-mb-px shrink-0 border-b-2 px-3.5 py-3 text-[0.9375rem] font-semibold whitespace-nowrap transition-colors duration-150 ${
                selected ? "border-accent text-fg" : "border-transparent text-fg-muted [@media(hover:hover)]:hover:text-fg"
              }`}
            >
              {tab.label}
            </button>
          );
        })}
      </div>
      <div role="tabpanel" id={`${base}-panel`} aria-labelledby={`${base}-tab-${active}`} className="pt-5">
        <ul className={gridClass}>
          {items.map((item) => (
            <li key={item.key} hidden={active !== tabs[0].id && !item.groups.includes(active ?? "")}>
              {item.node}
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}

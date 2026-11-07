"use client";

import { formatDateTime, opLabel } from "@/lib/admin/format";
import type { Movement } from "@/lib/admin/types";
import { Chip, DataTable, Kunye, type Column } from "./kit";

/** İşlemi yapan: personelse e-posta (sunucu yalnız hâlen Admin/Warehouse olanlar için doldurur), değilse müşteri/sistem. */
export const actorOf = (m: Movement) => m.actorEmail ?? "Müşteri";

const signed = (n: number) => (n > 0 ? `+${n}` : String(n));

/** Hareket defteri: her satır bir fiş kalemidir (zaman, tür, SKU, işaretli adet, işlem sonrası bakiye, neden, yapan). */
export function MovementLedger({ rows, caption = "Stok hareketleri", hideSku }: { rows: Movement[]; caption?: string; hideSku?: boolean }) {
  const columns: Column<Movement>[] = [
    { key: "time", header: "Zaman", primary: true, cell: (m) => <time dateTime={m.occurredAt} className="whitespace-nowrap">{formatDateTime(m.occurredAt)}</time> },
    { key: "type", header: "Tür", cell: (m) => <span>{opLabel[m.operationType] ?? m.operationType}{m.replayed ? <> <Chip>tekrar</Chip></> : null}</span> },
    ...(hideSku ? [] : [{ key: "sku", header: "SKU", cell: (m: Movement) => <Kunye>{m.sku}</Kunye> }]),
    { key: "delta", header: "Değişim", align: "right", cell: (m) => <span className={`font-mono font-medium ${m.quantityDelta < 0 ? "text-red-800" : "text-emerald-900"}`}>{signed(m.quantityDelta)}</span> },
    { key: "after", header: "Bakiye", align: "right", cell: (m) => <span className="font-mono">{m.onHandAfter}</span> },
    { key: "reason", header: "Neden", className: "md:max-w-xs", cell: (m) => <span className="break-words">{m.reason}{m.reference ? <span className="block text-xs text-fg-muted">Ref: {m.reference}</span> : null}</span> },
    { key: "actor", header: "Yapan", cell: (m) => <span className="break-all">{actorOf(m)}</span> },
  ];
  return <DataTable caption={caption} rows={rows} columns={columns} rowKey={(m) => m.id} />;
}

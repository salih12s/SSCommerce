"use client";

// Adet seçici: iki düğme + canlı değer. Klavye: Tab ile düğmelere, Enter/Boşluk ile artır/azalt.
// Yazma sürerken düğmeler devre dışı bırakılmaz (odak kaybolmasın); çağıran taraf çift isteği yok sayar.
// Sınırlar çağıran tarafından verilir (min 1, max = görünür stok ya da 20); gerçek doğrulama sunucudadır.
export function QuantityStepper({ value, min = 1, max, onChange, label, busy = false, id }: {
  value: number; min?: number; max: number; onChange: (next: number) => void; label: string; busy?: boolean; id?: string;
}) {
  const button = "grid size-11 place-items-center rounded-md border border-line bg-card text-xl leading-none font-semibold transition-transform duration-150 ease-out active:scale-[0.94] disabled:cursor-not-allowed disabled:opacity-45 disabled:active:scale-100";
  return (
    <div role="group" aria-label={label} id={id} aria-busy={busy || undefined} className="inline-flex items-center gap-1.5">
      <button type="button" className={button} disabled={value <= min} aria-label={`${label}: azalt`} onClick={() => onChange(value - 1)}><span aria-hidden="true">−</span></button>
      <output aria-live="off" aria-label={`${label}: ${value}`} className="grid min-w-10 place-items-center text-center font-display text-lg font-bold tabular-nums">{value}</output>
      <button type="button" className={button} disabled={value >= max} aria-label={`${label}: artır`} onClick={() => onChange(value + 1)}><span aria-hidden="true">+</span></button>
    </div>
  );
}

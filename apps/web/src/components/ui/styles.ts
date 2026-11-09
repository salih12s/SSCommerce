// Ortak sınıflar: mevcut tasarım dilindeki düğme ve alan görünümü (koyu kömür birincil düğme, açık zeminli alanlar).
const press =
  "transition-transform duration-150 ease-out active:scale-[0.97] motion-reduce:transition-none";
export const btnPrimary = `inline-flex min-h-12 items-center justify-center gap-2 rounded-md bg-ink px-5 py-3 font-semibold text-on-ink hover:bg-ink-2 ${press} disabled:cursor-not-allowed disabled:opacity-60 disabled:active:scale-100 aria-disabled:cursor-not-allowed aria-disabled:opacity-60 aria-disabled:active:scale-100`;
export const btnAccent = `inline-flex min-h-12 items-center justify-center gap-2 rounded-md bg-accent px-5 py-3 font-semibold text-ink hover:brightness-95 ${press} disabled:cursor-not-allowed disabled:opacity-60 disabled:active:scale-100`;
export const btnSecondary = `inline-flex min-h-11 items-center justify-center gap-2 rounded-md border border-line bg-card px-4 py-2 font-semibold text-fg hover:bg-paper-2 ${press} disabled:cursor-not-allowed disabled:opacity-60`;
export const btnLink =
  "inline-flex min-h-11 items-center font-semibold text-accent-deep underline underline-offset-2";
export const fieldClass =
  "w-full rounded-md border border-line bg-paper px-3 text-base";
export const panel =
  "rounded-xl border border-line bg-card p-5 shadow-card sm:p-6";

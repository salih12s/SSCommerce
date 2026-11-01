import { SearchIcon } from "./Icons";

export function SearchForm() {
  return (
    <form role="search" action="/arama" method="get" className="flex w-full">
      <label className="min-w-0 flex-1">
        <span className="sr-only">Ürün ara</span>
        <input
          type="search"
          name="q"
          maxLength={100}
          autoComplete="off"
          enterKeyHint="search"
          placeholder="Ürün ara"
          className="h-11 w-full rounded-l-md border border-r-0 border-white/10 bg-card px-4 text-fg placeholder:text-fg-muted focus-visible:relative focus-visible:z-10"
        />
      </label>
      <button
        type="submit"
        aria-label="Ara"
        className="grid h-11 w-12 place-items-center rounded-r-md bg-accent text-ink transition-transform duration-150 ease-out active:scale-[0.97]"
      >
        <SearchIcon />
      </button>
    </form>
  );
}

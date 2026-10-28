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
          className="h-11 w-full rounded-l-md border border-r-0 border-ink-2 bg-on-ink px-3.5 text-fg placeholder:text-fg-muted focus-visible:relative focus-visible:z-10"
        />
      </label>
      <button
        type="submit"
        className="h-11 rounded-r-md bg-accent px-5 font-semibold text-ink transition-transform duration-150 ease-out active:scale-[0.97]"
      >
        Ara
      </button>
    </form>
  );
}

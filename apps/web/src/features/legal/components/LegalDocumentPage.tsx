import Link from "next/link";
import { LAST_UPDATED, SAMPLE_NOTICE, legalDocuments, type LegalBlock, type LegalDocument } from "@/content/legal";

// Kurumsal ve yasal sayfaların ortak okuma düzeni: tek kolon, ~70 karakter ölçü, başlık hiyerarşisi.
// Statik sunucu bileşenidir; içerik src/content/legal altındaki saf veriden gelir. Kişisel veri içermez (server/privacy.mjs'e eklenmez).
const prose = "max-w-[70ch] text-[1.0625rem] leading-relaxed";
const linkClass = "font-medium text-accent-deep underline underline-offset-4";

/** Ürün etiketi biçiminde "örnek metin" uyarısı: delikli, kesik çizgili kenarlı. */
function SampleTag() {
  return (
    <div
      role="note"
      aria-label="Örnek metin uyarısı"
      className="relative mt-5 rounded-md border-2 border-dashed border-accent-deep bg-card py-3.5 pr-4 pl-12 sm:pl-14"
    >
      <span
        aria-hidden="true"
        className="absolute top-1/2 left-4 size-3.5 -translate-y-1/2 rounded-full border-2 border-accent-deep bg-paper sm:left-5"
      />
      <p className="font-mono text-xs font-medium tracking-widest text-accent-deep uppercase">Örnek metin</p>
      <p className="mt-1 font-medium">{SAMPLE_NOTICE}</p>
    </div>
  );
}

function Table({ block }: { block: Extract<LegalBlock, { type: "table" }> }) {
  return (
    <table role="table" className="mt-1 block w-full border-collapse text-[0.9375rem] sm:table">
      <caption className="mb-2 block text-left font-mono text-xs tracking-wide text-fg-muted uppercase">{block.caption}</caption>
      <thead role="rowgroup" className="hidden sm:table-header-group">
        <tr role="row">
          {block.head.map((h) => (
            <th key={h} role="columnheader" scope="col" className="border-b-2 border-ink px-3 py-2 text-left font-semibold">
              {h}
            </th>
          ))}
        </tr>
      </thead>
      <tbody role="rowgroup" className="block sm:table-row-group">
        {block.rows.map((row) => (
          <tr
            key={row[0]}
            role="row"
            className="mb-3 block rounded-md border border-line bg-card p-3 sm:mb-0 sm:table-row sm:rounded-none sm:border-0 sm:bg-transparent sm:p-0"
          >
            {row.map((cell, i) => (
              <td
                key={block.head[i]}
                role="cell"
                className={`block py-1 break-words sm:table-cell sm:border-b sm:border-line sm:px-3 sm:py-3 sm:align-top ${i === 0 ? "font-semibold" : ""}`}
              >
                <span className="block font-mono text-xs tracking-wide text-fg-muted uppercase sm:hidden">{block.head[i]}</span>
                {cell}
              </td>
            ))}
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function Block({ block }: { block: LegalBlock }) {
  switch (block.type) {
    case "p":
      return <p className={prose}>{block.text}</p>;
    case "ul":
      return (
        <ul className={`${prose} list-disc space-y-2 pl-5 marker:text-accent-deep`}>
          {block.items.map((item) => (
            <li key={item}>{item}</li>
          ))}
        </ul>
      );
    case "ol":
      return (
        <ol className={`${prose} list-decimal space-y-2 pl-6 marker:font-mono marker:text-accent-deep`}>
          {block.items.map((item) => (
            <li key={item}>{item}</li>
          ))}
        </ol>
      );
    case "note":
      return (
        <div className="max-w-[70ch] rounded-md border border-line border-l-4 border-l-accent-deep bg-card p-4 shadow-card">
          <p className="font-display text-lg font-bold">{block.title}</p>
          <p className="mt-1 leading-relaxed">{block.text}</p>
        </div>
      );
    case "facts":
      return (
        <dl className="max-w-[70ch] divide-y divide-dashed divide-line rounded-md border border-line bg-card shadow-card">
          {block.items.map((f) => (
            <div key={f.label} className="px-4 py-3 sm:grid sm:grid-cols-[9.5rem_1fr] sm:gap-4">
              <dt className="font-mono text-xs tracking-wide text-fg-muted uppercase sm:pt-1">{f.label}</dt>
              <dd className="mt-0.5 font-medium break-words sm:mt-0">{f.value}</dd>
            </div>
          ))}
        </dl>
      );
    case "table":
      return <Table block={block} />;
  }
}

export function LegalDocumentPage({ doc }: { doc: LegalDocument }) {
  const others = legalDocuments.filter((d) => d.slug !== doc.slug);
  return (
    <div className="mx-auto max-w-3xl px-4 py-8 sm:px-6 md:py-14">
      <article>
        <header>
          <p className="font-mono text-xs tracking-widest text-accent-deep uppercase">Kurumsal</p>
          <h1 className="mt-2 font-display text-title font-extrabold">{doc.title}</h1>
          <SampleTag />
          <p className={`${prose} mt-5 text-fg-muted`}>{doc.lead}</p>
        </header>

        {doc.toc && (
          <nav aria-labelledby={`${doc.slug}-toc`} className="mt-8 rounded-md border border-line bg-card p-4 sm:p-5">
            <p id={`${doc.slug}-toc`} className="font-display text-lg font-bold">
              İçindekiler
            </p>
            <ol className="mt-1 sm:grid sm:grid-cols-2 sm:gap-x-6">
              {doc.sections.map((s, i) => (
                <li key={s.id}>
                  <a href={`#${s.id}`} className="flex min-h-11 items-center gap-3 py-1 hover:text-accent-deep">
                    <span aria-hidden="true" className="w-6 shrink-0 font-mono text-sm text-accent-deep">
                      {i + 1}.
                    </span>
                    <span className="underline-offset-4 hover:underline">{s.title}</span>
                  </a>
                </li>
              ))}
            </ol>
          </nav>
        )}

        <div className="mt-10 space-y-12">
          {doc.sections.map((s, i) => (
            <section key={s.id} aria-labelledby={s.id} className="scroll-mt-4">
              <h2 id={s.id} className="font-display text-2xl leading-tight font-bold">
                {doc.toc && (
                  <span aria-hidden="true" className="mr-2 font-mono text-lg font-medium text-accent-deep">
                    {i + 1}.
                  </span>
                )}
                {s.title}
              </h2>
              <div className="mt-4 space-y-4">
                {s.blocks.map((b, j) => (
                  <Block key={j} block={b} />
                ))}
              </div>
            </section>
          ))}
        </div>

        <div className="mt-14 border-t border-dashed border-line pt-6">
          <p className="font-mono text-sm text-fg-muted">
            Son güncelleme: <time dateTime={LAST_UPDATED.iso}>{LAST_UPDATED.text}</time>
          </p>
          <nav aria-label="Diğer kurumsal sayfalar" className="mt-3">
            <ul className="flex flex-wrap gap-x-5">
              {others.map((d) => (
                <li key={d.slug}>
                  <Link href={`/${d.slug}`} className={`${linkClass} inline-flex min-h-11 items-center`}>
                    {d.navLabel}
                  </Link>
                </li>
              ))}
            </ul>
          </nav>
        </div>
      </article>
    </div>
  );
}

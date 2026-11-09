// Ürün sayfasındaki teslimat ve iptal bilgisi. Yalnızca sistemin gerçekten yaptığı şeyler yazılır; tahmini süre, iade günü, kargo firması vb. uydurma yoktur.
// Dil: kargo koli etiketi gibi kesik çizgili satırlar (etiket/paket). Metinler değişirse docs ve sipariş ekranındaki dille tutarlı kalmalıdır.
const rows: { term: string; text: string }[] = [
  { term: "Ödeme", text: "Sipariş verirken ödeme alınmaz." },
  { term: "Kargo", text: "Kargo ücreti henüz hesaplanmaz." },
  { term: "Stok", text: "Sepet stok ayırmaz; stok sipariş verdiğin anda düşer." },
  { term: "İptal", text: "“Alındı” durumundaki siparişi hesabından kendin iptal edebilirsin; ürünler stoğa geri döner." },
  { term: "İade", text: "İade (geri gönderme) süreci henüz yok." },
];

export function DeliveryInfo() {
  return <section aria-labelledby="teslimat-iptal" data-testid="delivery-info" className="mt-6 rounded-md border-2 border-dashed border-line bg-card p-4">
    <h2 id="teslimat-iptal" className="font-display text-lg font-bold">Teslimat ve iptal</h2>
    <dl className="mt-2 divide-y divide-dashed divide-line">
      {rows.map((row) => <div key={row.term} className="grid grid-cols-[4.5rem_1fr] gap-3 py-2 first:pt-0 last:pb-0 sm:grid-cols-[5.5rem_1fr]">
        <dt className="font-mono text-xs tracking-wide text-fg-muted uppercase">{row.term}</dt>
        <dd className="leading-snug">{row.text}</dd>
      </div>)}
    </dl>
  </section>;
}

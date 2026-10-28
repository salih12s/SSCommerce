// Doğrulanmamış vaat yok: tüm maddeler planlanan bilgi olarak yazılır.
const items = [
  {
    title: "Kargo",
    text: "Kargo seçenekleri ve süreleri satış açıldığında burada, sipariş vermeden önce görünecek. (planlanan)",
  },
  {
    title: "İade",
    text: "İade ve değişim koşulları satıştan önce açık bir sayfada yayımlanacak. (planlanan)",
  },
  {
    title: "Garanti",
    text: "Garanti süresi ve servis bilgisi her ürün sayfasında ayrıca yazılacak. (planlanan)",
  },
];

export function TrustSection() {
  return (
    <section aria-labelledby="guven-baslik" className="py-section">
      <div className="mx-auto max-w-6xl px-4 sm:px-6">
        <h2 id="guven-baslik" className="max-w-2xl font-display text-title font-extrabold">
          Servis ve güven bilgileri
        </h2>
        <p className="mt-3 max-w-xl text-fg-muted">
          Satış açılmadı, bu yüzden aşağıdakiler henüz bir taahhüt değil; vitrinde neyi açıkça göstereceğimizin listesi.
        </p>
        <dl className="mt-10 grid gap-x-8 gap-y-6 border-t border-line md:grid-cols-3">
          {items.map((item) => (
            <div key={item.title} className="pt-5">
              <dt className="font-display text-xl font-bold">{item.title}</dt>
              <dd className="mt-2 max-w-sm text-fg-muted">{item.text}</dd>
            </div>
          ))}
        </dl>
      </div>
    </section>
  );
}

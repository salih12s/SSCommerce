import { CardIcon, ReturnIcon, ShieldIcon, TruckIcon } from "./Icons";

// Satış açılmadı: burada vaat yok. Her madde, bilginin satıştan önce nerede yayımlanacağını söyler.
const items = [
  { icon: <TruckIcon />, title: "Kargo seçenekleri", text: "Yakında" },
  { icon: <ReturnIcon />, title: "İade koşulları", text: "Satıştan önce yayımlanacak" },
  { icon: <CardIcon />, title: "Ödeme yöntemleri", text: "Yakında" },
  { icon: <ShieldIcon />, title: "Garanti bilgisi", text: "Her ürün sayfasında yazılacak" },
];

export function ServiceStrip() {
  return (
    <section aria-label="Servis bilgileri" className="border-b border-line bg-card">
      <ul className="mx-auto grid max-w-7xl grid-cols-2 gap-x-4 gap-y-4 px-4 py-4 sm:px-6 md:grid-cols-4">
        {items.map((item) => (
          <li key={item.title} className="flex items-center gap-3">
            <span className="grid size-10 shrink-0 place-items-center rounded-full bg-paper-2 text-accent-deep">{item.icon}</span>
            <span className="flex min-w-0 flex-col leading-tight">
              <span className="text-[0.9375rem] font-semibold">{item.title}</span>
              <span className="text-[0.8125rem] text-fg-muted">{item.text}</span>
            </span>
          </li>
        ))}
      </ul>
    </section>
  );
}

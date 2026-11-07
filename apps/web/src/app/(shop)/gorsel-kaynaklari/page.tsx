import type { Metadata } from "next";
import Link from "next/link";
import { imageCredits } from "@/content/image-credits";

export const metadata: Metadata = {
  title: "Görsel kaynakları",
  description: "SSCommerce demo vitrinindeki fotoğrafların yazarı, kaynağı ve lisansı.",
};

const linkClass = "font-medium break-words text-accent-deep underline underline-offset-4";

export default function ImageCreditsPage() {
  return (
    <div className="mx-auto max-w-7xl px-4 py-10 sm:px-6 md:py-section">
      <h1 className="font-display text-title font-extrabold">Görsel kaynakları</h1>
      <div className="mt-4 max-w-2xl space-y-3 text-fg-muted">
        <p>
          Vitrindeki {imageCredits.length} fotoğrafın tamamı Unsplash&apos;ta <strong>Unsplash License</strong> ile
          yayımlanmıştır (ücretli Unsplash+ içeriği yoktur). Lisans ticari kullanıma izin verir ve atıf zorunlu kılmaz;
          yine de her fotoğrafın yazarı ve kaynak sayfası aşağıda verilir. Fotoğraflar yeniden boyutlandırılmış ve
          kırpılmıştır; yapılan değişiklik her satırda yazar.
        </p>
        <p>
          Ürünler, fiyatlar ve kategoriler demodur. Fotoğraflar ürünlerin gerçek görüntüsü değil, temsilî örneklerdir.
          Sitede çalışma anında dış kaynaktan görsel yüklenmez; dosyalar sitenin kendi sunucusundadır.
        </p>
        <p>
          Her fotoğraf yüksek çözünürlükte gözle incelenmiş; görünür logo ya da marka yazısı, tanınabilir marka tasarımı,
          yüz ve filigran içerenler elenmiştir. Bu bir hukuki marka taraması değildir.
        </p>
      </div>

      <ul className="mt-10 grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        {imageCredits.map((c) => (
          <li key={c.file} className="rounded-md border border-line bg-card p-4 shadow-card">
            <dl className="min-w-0 space-y-0.5 text-[0.9375rem]">
              <div>
                <dt className="sr-only">Kullanıldığı yer</dt>
                <dd className="font-display text-base leading-snug font-bold">{c.place}</dd>
              </div>
              <div>
                <dt className="sr-only">Dosya</dt>
                <dd className="font-mono text-[0.8125rem] break-all text-fg-muted">{c.file}</dd>
              </div>
              <div className="pt-1">
                <dt className="inline text-fg-muted">Yazar: </dt>
                <dd className="inline">
                  <a href={c.authorUrl} className={linkClass}>
                    {c.author}
                  </a>
                </dd>
              </div>
              <div>
                <dt className="inline text-fg-muted">Lisans: </dt>
                <dd className="inline">
                  <a href={c.licenseUrl} className={linkClass}>
                    {c.license}
                  </a>
                </dd>
              </div>
              <div>
                <dt className="inline text-fg-muted">Kaynak: </dt>
                <dd className="inline">
                  <a href={c.page} className={linkClass}>
                    Unsplash sayfası
                  </a>
                </dd>
              </div>
              <div>
                <dt className="inline text-fg-muted">Denetim: </dt>
                <dd className="inline">{c.audit}</dd>
              </div>
              <div>
                <dt className="sr-only">Yapılan değişiklik</dt>
                <dd className="pt-1 text-[0.8125rem] text-fg-muted">{c.change}</dd>
              </div>
            </dl>
          </li>
        ))}
      </ul>

      <p className="mt-10">
        <Link href="/" className={linkClass}>
          Ana sayfaya dön
        </Link>
      </p>
    </div>
  );
}

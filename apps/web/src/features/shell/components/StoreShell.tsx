import { Suspense } from "react";
import { BottomNav } from "./BottomNav";
import { Footer } from "./Footer";
import { Header } from "./Header";

// Vitrin çerçevesi: üst bölge, ana içerik, alt bilgi ve mobil alt gezinti. `(shop)` grubunun yerleşimi ve kök not-found kullanır;
// /admin bu çerçeveyi kullanmaz (kendi yerleşimi vardır). İşaretleme eski kök yerleşimiyle aynıdır (gövdenin doğrudan çocukları).
export function StoreShell({ children }: { children: React.ReactNode }) {
  return (
    <>
      <Header />
      <main id="icerik" tabIndex={-1} className="flex-1 outline-none">
        {children}
      </main>
      <Suspense fallback={null}><Footer /></Suspense>
      <Suspense fallback={null}><BottomNav /></Suspense>
    </>
  );
}

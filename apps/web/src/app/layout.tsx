import type { Metadata, Viewport } from "next";
import { Bricolage_Grotesque, DM_Mono, Instrument_Sans } from "next/font/google";
import { Suspense } from "react";
import { BottomNav } from "@/components/BottomNav";
import { Footer } from "@/components/Footer";
import { Header } from "@/components/Header";
import "./globals.css";

const bricolage = Bricolage_Grotesque({
  variable: "--font-bricolage",
  subsets: ["latin", "latin-ext"],
  display: "swap",
});

const instrument = Instrument_Sans({
  variable: "--font-instrument",
  subsets: ["latin", "latin-ext"],
  display: "swap",
});

const dmMono = DM_Mono({
  variable: "--font-dm-mono",
  subsets: ["latin", "latin-ext"],
  weight: ["400", "500"],
  display: "swap",
});

export const metadata: Metadata = {
  metadataBase: new URL(process.env.NEXT_PUBLIC_SITE_URL ?? "http://localhost:3000"),
  title: { default: "SSCommerce", template: "%s | SSCommerce" },
  description: "SSCommerce demo vitrini: bilgisayar, telefon, ses ve akıllı ev ürünleri. Ürünler ve fiyatlar örnektir.",
};

export const viewport: Viewport = {
  width: "device-width",
  initialScale: 1,
  viewportFit: "cover",
  themeColor: "#16181b",
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html lang="tr" className={`${bricolage.variable} ${instrument.variable} ${dmMono.variable}`}>
      {/* Mobilde alt gezinti sabit olduğundan gövdeye onun yüksekliği kadar alt boşluk verilir (footer örtülmez). */}
      <body className="flex min-h-dvh flex-col pb-[calc(4.6rem+env(safe-area-inset-bottom,0px))] md:pb-0">
        <a
          href="#icerik"
          className="absolute top-2 left-2 z-50 -translate-y-24 rounded-md bg-accent px-4 py-3 font-semibold text-ink focus:translate-y-0"
        >
          Ana içeriğe geç
        </a>
        <Header />
        <main id="icerik" tabIndex={-1} className="flex-1 outline-none">
          {children}
        </main>
        <Suspense fallback={null}><Footer /></Suspense>
        <Suspense fallback={null}><BottomNav /></Suspense>
      </body>
    </html>
  );
}

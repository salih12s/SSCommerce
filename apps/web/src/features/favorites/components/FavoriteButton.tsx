"use client";

import { useRouter } from "next/navigation";
import { useEffect, useState, useSyncExternalStore } from "react";
import { HeartIcon } from "@/components/ui/Icons";
import { loginHref, safeNext } from "@/lib/safe-next";
import { setFavorite, useFavorites } from "@/features/favorites/favorites";

// Ürün kartının köşesindeki kalp (istemci adacığı; kartın kendisi sunucu bileşeni kalır). Ebeveyni `relative` olmalı.
// Durum yalnızca tarayıcıda, oturum çerezi ile okunur. Misafir basarsa bulunduğu sayfaya dönecek şekilde girişe gider.
// Hidrasyondan önce düğme devre dışıdır (AuthForm ile aynı kalıp): işleyici yokken basış sessizce kaybolmasın.
// Erişilebilir ad SABİTtir ("<ürün> favorilerde"); durum yalnızca aria-pressed ile bildirilir (ad değişirse ekran okuyucu durumu iki kez söyler).
// Hata görünür kalır (role="alert"), birkaç saniye sonra kendiliğinden kapanır.
const subscribe = () => () => {};

export function FavoriteButton({ productId, productName }: { productId: string; productName: string }) {
  const router = useRouter();
  const hydrated = useSyncExternalStore(subscribe, () => true, () => false);
  const favorites = useFavorites();
  const pressed = favorites.ids.has(productId);
  const [pop, setPop] = useState(false);
  const [error, setError] = useState("");

  useEffect(() => {
    if (!error) return;
    const timer = setTimeout(() => setError(""), 7000);
    return () => clearTimeout(timer);
  }, [error]);

  async function onClick() {
    setError("");
    const wanted = !pressed;
    if (wanted && favorites.status === "ready") setPop(true);
    const result = await setFavorite(productId, wanted);
    if (result.ok) return;
    setPop(false);
    if (result.reason === "anonymous") {
      // Yol çalışma anında okunur: usePathname/useSearchParams üst sayfaları dinamik yapmasın.
      router.push(loginHref(safeNext(`${window.location.pathname}${window.location.search}`)));
    } else if (result.message) setError(result.message);
  }

  return <>
    <button
      type="button"
      aria-pressed={pressed}
      disabled={!hydrated}
      aria-busy={favorites.pending.has(productId) || undefined}
      aria-label={`${productName} favorilerde`}
      title={pressed ? "Favorilerden çıkar" : "Favorilere ekle"}
      data-testid="favorite-button"
      onClick={() => void onClick()}
      className="absolute top-1 right-1 z-10 grid size-11 place-items-center rounded-full text-fg transition-transform duration-150 ease-out active:scale-90 aria-pressed:text-accent-deep"
    >
      <span onAnimationEnd={() => setPop(false)} className={`grid size-9 place-items-center rounded-full bg-card/90 shadow-card ${pop && pressed ? "animate-heart-pop" : ""}`}>
        <HeartIcon className={pressed ? "fill-current" : ""} />
      </span>
    </button>
    {error && <p role="alert" data-testid="favorite-error" className="absolute inset-x-1.5 top-14 z-20 rounded-md border border-red-300 bg-red-50 p-2 text-xs leading-snug text-red-800 shadow-card">{error}</p>}
  </>;
}

"use client";

import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import {
  demoLogin,
  fetchDemoAccounts,
  type DemoAccountKey,
} from "@/features/auth/auth-client";
import { resetCart } from "@/features/cart/cart";
import { resetFavorites } from "@/features/favorites/favorites";
import { btnAccent, btnSecondary } from "@/components/ui/styles";
import { DEFAULT_NEXT } from "@/lib/safe-next";

// Vitrin girişi: yalnız GET /auth/demo 200 dönerse çizilir (karar backend'de). 404, ağ hatası ya da başka her durumda hiçbir şey görünmez, hata da gösterilmez.
// Görünüm "kopartılabilir fiş": kesik çizgili kenar ve delik, giriş formunun altında durur (bu yüzden geç görünmesi formu kaydırmaz).
const ACCOUNTS: {
  key: DemoAccountKey;
  label: string;
  hint: string;
  landing: string;
}[] = [
  {
    key: "customer",
    label: "Demo müşteri olarak gir",
    hint: "Adresleri, favorileri, sepeti ve 5 siparişi hazır.",
    landing: DEFAULT_NEXT,
  },
  {
    key: "admin",
    label: "Demo yönetici",
    hint: "Yönetim paneli: katalog, stok ve siparişler.",
    landing: "/admin",
  },
  {
    key: "warehouse",
    label: "Demo depocu",
    hint: "Yalnız sipariş ekranları.",
    landing: "/admin/siparisler",
  },
];

export function DemoLogin({ next = DEFAULT_NEXT }: { next?: string }) {
  const router = useRouter();
  const [available, setAvailable] = useState<string[] | null>(null);
  const [busy, setBusy] = useState<DemoAccountKey | null>(null);
  const [error, setError] = useState("");

  useEffect(() => {
    let alive = true;
    void fetchDemoAccounts().then((result) => {
      if (alive && result.ok && Array.isArray(result.data.accounts))
        setAvailable(result.data.accounts);
    });
    return () => {
      alive = false;
    };
  }, []);

  const shown = ACCOUNTS.filter((a) => available?.includes(a.key));
  if (shown.length === 0) return null;

  async function enter(account: (typeof ACCOUNTS)[number]) {
    setError("");
    setBusy(account.key);
    const result = await demoLogin(account.key);
    if (!result.ok) {
      setBusy(null);
      setError(result.message);
      return;
    }
    resetCart();
    resetFavorites();
    // Açık bir `next` varsa ona dönülür; yoksa hesabın doğal ilk sayfasına (müşteri: hesabım, yönetici/depocu: yönetim alanı).
    router.push(next !== DEFAULT_NEXT ? next : account.landing);
    router.refresh();
  }

  return (
    <section
      aria-labelledby="demo-baslik"
      aria-busy={busy !== null}
      data-testid="demo-login"
      className="relative mt-8 rounded-xl border-2 border-dashed border-line bg-card/60 px-5 pt-7 pb-5"
    >
      <span
        aria-hidden="true"
        className="absolute -top-3 left-5 size-6 rounded-full border-2 border-dashed border-line bg-paper"
      />
      <h2
        id="demo-baslik"
        className="font-display text-xl font-bold tracking-tight"
      >
        Hesap açmadan dene
      </h2>
      <p className="mt-1 text-fg-muted">
        Vitrin hesapları ortaktır; örnek veriyle doludur.
      </p>
      <ul className="mt-4 space-y-4">
        {shown.map((a) => (
          <li key={a.key}>
            <button
              type="button"
              onClick={() => void enter(a)}
              disabled={busy !== null}
              aria-describedby={`demo-${a.key}-not`}
              className={`${a.key === "customer" ? btnAccent : btnSecondary} w-full min-h-12`}
            >
              {busy === a.key ? "Giriş yapılıyor…" : a.label}
            </button>
            <p
              id={`demo-${a.key}-not`}
              className="mt-1.5 text-sm text-fg-muted"
            >
              {a.hint}
            </p>
          </li>
        ))}
      </ul>
      {error && (
        <p
          role="alert"
          className="mt-4 rounded-md border border-red-300 bg-red-50 p-3 text-sm text-red-800"
        >
          {error}
        </p>
      )}
    </section>
  );
}

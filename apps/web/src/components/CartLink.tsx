"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { resetCart, useCart } from "@/lib/cart";
import { usePersistedPageshow } from "@/lib/page-restore";
import { BagIcon } from "./Icons";

// "Sepetim" gerçek bağlantı. Sayaç yalnızca oturum açıksa ve sepet doluysa görünür; anonim kullanıcıya sayaç yoktur.
// Sayı tarayıcıda çerezle okunur (HTML'de kişisel veri yok) ve sepet her değiştiğinde ortak depodan güncellenir.
function useCount() {
  const cart = useCart();
  return cart.status === "ready" ? cart.cart.itemCount : 0;
}
const badge = "absolute -top-1 -right-1 grid min-w-[1.25rem] place-items-center rounded-full bg-accent px-1 text-xs leading-5 font-bold text-ink tabular-nums";

export function HeaderCartLink() {
  // usePathname kullanılmaz: üst çubuk Suspense dışındadır ve yol okuyan bileşen prerender'ı bloklar.
  const count = useCount();
  // bfcache geri yüklemesinde önceki kimliğin sayacı/sepeti bellekte kalmasın.
  usePersistedPageshow(resetCart);
  return (
    <Link href="/sepet" className="inline-flex h-11 min-w-11 items-center justify-center gap-2.5 rounded-md px-2 text-on-ink hover:text-accent">
      <span className="relative"><BagIcon />{count > 0 && <span aria-hidden="true" data-testid="cart-count" className={badge}>{count}</span>}</span>
      <span className="text-[0.9375rem] font-medium max-lg:sr-only">Sepetim{count > 0 && <span className="sr-only">, {count} ürün</span>}</span>
    </Link>
  );
}

export function BottomCartLink({ className }: { className: (active: boolean) => string }) {
  const count = useCount();
  const onCart = usePathname() === "/sepet";
  return (
    <Link href="/sepet" aria-current={onCart ? "page" : undefined} className={className(onCart)}>
      <span className="relative"><BagIcon />{count > 0 && <span aria-hidden="true" data-testid="cart-count" className={badge}>{count}</span>}</span>
      <span>Sepetim{count > 0 && <span className="sr-only">, {count} ürün</span>}</span>
    </Link>
  );
}

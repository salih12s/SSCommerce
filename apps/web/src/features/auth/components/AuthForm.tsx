"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState, useSyncExternalStore, type FormEvent } from "react";
import { authMutation, type Account } from "@/features/auth/auth-client";
import { DemoLogin } from "@/features/auth/components/DemoLogin";
import { resetCart } from "@/features/cart/cart";
import { resetFavorites } from "@/features/favorites/favorites";
import {
  DEFAULT_NEXT,
  loginHref,
  registerHref,
  safeNext,
} from "@/lib/safe-next";

const subscribe = () => () => {};

export function AuthForm({
  mode,
  next = DEFAULT_NEXT,
}: {
  mode: "login" | "register";
  next?: string;
}) {
  const target = safeNext(next);
  const router = useRouter();
  const hydrated = useSyncExternalStore(
    subscribe,
    () => true,
    () => false,
  );
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError("");
    setFieldErrors({});
    setBusy(true);
    const data = new FormData(event.currentTarget);
    const result = await authMutation<Account>(mode, {
      email: String(data.get("email") ?? "").trim(),
      password: String(data.get("password") ?? ""),
    });
    setBusy(false);
    if (!result.ok) {
      setError(result.message);
      setFieldErrors(result.errors);
      return;
    }
    resetCart();
    resetFavorites();
    router.push(target);
    router.refresh();
  }
  return (
    <>
      <form
        onSubmit={submit}
        className="mt-7 space-y-5 rounded-xl border border-line bg-card p-6 shadow-card"
        aria-busy={busy}
      >
        <div>
          <label htmlFor="email" className="mb-1 block font-semibold">
            E-posta
          </label>
          <input
            id="email"
            name="email"
            type="email"
            autoComplete="email"
            required
            maxLength={254}
            aria-invalid={!!fieldErrors.email}
            aria-describedby={fieldErrors.email ? "email-error" : undefined}
            className="h-12 w-full rounded-md border border-line bg-paper px-3"
          />
          {fieldErrors.email && (
            <p id="email-error" className="mt-1 text-sm text-red-700">
              {fieldErrors.email.join(" ")}
            </p>
          )}
        </div>
        <div>
          <label htmlFor="password" className="mb-1 block font-semibold">
            Parola
          </label>
          <input
            id="password"
            name="password"
            type="password"
            autoComplete={
              mode === "login" ? "current-password" : "new-password"
            }
            required
            maxLength={128}
            aria-invalid={!!fieldErrors.password}
            aria-describedby={
              fieldErrors.password ? "password-error" : undefined
            }
            className="h-12 w-full rounded-md border border-line bg-paper px-3"
          />
          {fieldErrors.password && (
            <p id="password-error" className="mt-1 text-sm text-red-700">
              {fieldErrors.password.join(" ")}
            </p>
          )}
        </div>
        {error && (
          <p
            role="alert"
            className="rounded-md border border-red-300 bg-red-50 p-3 text-sm text-red-800"
          >
            {error}
          </p>
        )}
        <button
          type="submit"
          disabled={busy || !hydrated}
          className="h-12 w-full rounded-md bg-ink px-5 font-semibold text-on-ink disabled:opacity-60"
        >
          {busy
            ? "Lütfen bekleyin…"
            : mode === "login"
              ? "Giriş yap"
              : "Hesap oluştur"}
        </button>
        <p className="text-sm text-fg-muted">
          {mode === "login" ? (
            <>
              Hesabın yok mu?{" "}
              <Link
                href={target === DEFAULT_NEXT ? "/kayit" : registerHref(target)}
                className="font-semibold text-accent-deep underline"
              >
                Kayıt ol
              </Link>
            </>
          ) : (
            <>
              Zaten hesabın var mı?{" "}
              <Link
                href={target === DEFAULT_NEXT ? "/giris" : loginHref(target)}
                className="font-semibold text-accent-deep underline"
              >
                Giriş yap
              </Link>
            </>
          )}
        </p>
      </form>
      {mode === "login" && <DemoLogin next={target} />}
    </>
  );
}

"use client";

import { useEffect, useState } from "react";
import type { ApiResult } from "@/lib/api";

export type Remote<T> = {
  /** Son tamamlanan yanıt (anahtar değişince yenisi gelene kadar eskisi kalır; `loading` o sırada true olur). İlk yüklemede null. */
  result: ApiResult<T> | null;
  loading: boolean;
  reload: () => void;
};

/**
 * Tarayıcıda veri okuma. `key` istek parametrelerini tanımlar (filtre, sayfa, kimlik); değişince yeniden okunur. `reload` aynı anahtarla yeniden okur.
 * Eski/geç dönen yanıt yok sayılır. Veri yalnız bileşen durumunda tutulur (depoya, URL'ye yazılmaz).
 */
export function useRemote<T>(load: () => Promise<ApiResult<T>>, key: string): Remote<T> {
  const [tick, setTick] = useState(0);
  const [done, setDone] = useState<{ id: string; result: ApiResult<T> } | null>(null);
  const id = `${key}#${tick}`;
  useEffect(() => {
    let alive = true;
    void load().then((result) => { if (alive) setDone({ id, result }); });
    return () => { alive = false; };
    // `load` her render'da yeni işlev olur; kimlik `key` ve `tick` ile belirlenir.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);
  return { result: done?.result ?? null, loading: done?.id !== id, reload: () => setTick((n) => n + 1) };
}

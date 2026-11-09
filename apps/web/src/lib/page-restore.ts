import { useEffect, useRef } from "react";

/** bfcache'ten geri yüklenen sayfa (pageshow.persisted) eski kimliğin verisini ekranda tutabilir: çağıran taraf veriyi yeniden okur. */
export function usePersistedPageshow(onRestore: () => void) {
  const ref = useRef(onRestore);
  useEffect(() => { ref.current = onRestore; });
  useEffect(() => {
    const handler = (event: PageTransitionEvent) => { if (event.persisted) ref.current(); };
    window.addEventListener("pageshow", handler);
    return () => window.removeEventListener("pageshow", handler);
  }, []);
}

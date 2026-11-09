import { useEffect, useSyncExternalStore } from "react";
import { apiGet, apiSend } from "@/lib/api";
import { createSyncTracker } from "./sync";

// Favori istemci deposu: kalpler (ürün kartı), üst gezinti sayacı ve /favorilerim aynı kimlik kümesini paylaşır.
// Favoriler kişiseldir: sunucudan gelen HTML'de yer almaz, yalnızca tarayıcıda oturum çerezi ile `no-store` okunur.
// localStorage/sessionStorage kullanılmaz; çıkışta resetFavorites() ile bellekten silinir (cart.ts ile aynı kalıp).
export type FavoriteItem = {
  /** Ürün kimliği (PUT/DELETE /favorites/{productId} yolunda kullanılır). */
  id: string; slug: string; name: string;
  category: { slug: string; name: string };
  primaryImage: { url: string; altText: string } | null;
  startingPriceMinor: number; currency: string; isDemo: boolean; inStock: boolean;
  addedAt: string;
  /** Ürün ve kategorisi hâlâ satışta mı. false ise satır listede kalır, kullanıcı kaldırabilir. */
  available: boolean;
};

export type FavoritesState = {
  status: "unknown" | "loading" | "anonymous" | "ready" | "error";
  ids: ReadonlySet<string>;
  /** Yazması sürmekte olan ürün kimlikleri (aynı ürüne üst üste istek gitmez). */
  pending: ReadonlySet<string>;
  message: string;
};
export type ToggleResult = { ok: true } | { ok: false; reason: "anonymous" | "busy" | "failed"; message: string };

const EMPTY: ReadonlySet<string> = new Set();
const INITIAL: FavoritesState = { status: "unknown", ids: EMPTY, pending: EMPTY, message: "" };
let state: FavoritesState = INITIAL;
let inflight: Promise<void> | null = null;
const listeners = new Set<() => void>();
// generation: resetFavorites() ile artar; eski kimliğe ait hiçbir yanıt durumu değiştiremez.
// sync: okuma/yazma yarışı (bkz. sync.ts). Yazmadan ÖNCE başlamış ya da yazma SÜRERKEN dönen bir GET eskidir ve iyimser kümeyi ezmez.
// getSeq: yalnızca en son başlayan GET uygulanır.
let generation = 0;
const sync = createSyncTracker();
let getSeq = 0;
function set(next: FavoritesState) { state = next; listeners.forEach((l) => l()); }
const current = (): FavoritesState => state;
const patch = (changes: Partial<FavoritesState>) => set({ ...state, ...changes });
const withId = (ids: ReadonlySet<string>, id: string, present: boolean) => { const next = new Set(ids); if (present) next.add(id); else next.delete(id); return next; };

export function loadFavorites(force = false): Promise<void> {
  if (!force && (state.status === "ready" || state.status === "anonymous")) return Promise.resolve();
  if (!force && inflight) return inflight;
  const gen = generation; const mark = sync.startRead(); const seq = ++getSeq;
  patch({ status: "loading" });
  const mine: Promise<void> = apiGet<{ productIds: string[] }>("/favorites/ids").then((result) => {
    if (gen !== generation || seq !== getSeq) return; // eski kimlik ya da daha yeni bir GET var
    if (result.ok) {
      const verdict = sync.judge(mark);
      if (verdict === "apply") set({ status: "ready", ids: new Set(result.data.productIds), pending: state.pending, message: "" });
      else {
        // Eski yanıt: iyimser küme korunur, yalnızca yükleniyor durumu kapanır. Araya yazma girip artık yazma yoksa güncel hâl yeniden okunur.
        patch({ status: "ready" });
        if (verdict === "reread") void loadFavorites(true);
      }
    } else if (result.status === 401) set({ status: "anonymous", ids: EMPTY, pending: EMPTY, message: "" });
    else patch({ status: "error", message: result.message });
  }).finally(() => { if (inflight === mine) inflight = null; });
  inflight = mine;
  return mine;
}

/** Giriş ya da çıkıştan (ve bfcache geri yüklemesinden) sonra çağrılır: önceki kimliğin kalpleri bellekte kalmaz, geç gelen yanıt yok sayılır. */
export function resetFavorites() {
  generation++; inflight = null;
  set(INITIAL);
  if (listeners.size > 0) void loadFavorites(true);
}

const UNCONFIRMED_MESSAGE = "İşlemin sonucu doğrulanamadı. Favorilerin güncel hâli gösteriliyor; lütfen kontrol et.";

/** Kalbi iyimser olarak değiştirir; sunucu reddederse yalnızca o ürünün durumu geri alınır ve görünür bir mesaj döner. */
export async function setFavorite(productId: string, wanted: boolean): Promise<ToggleResult> {
  if (state.pending.has(productId)) return { ok: false, reason: "busy", message: "" };
  if (state.status !== "ready") {
    await loadFavorites(state.status === "error");
    const loaded = current(); // await sonrası güncel durum (TS daralmasını aşmak için işlev üzerinden)
    if (loaded.status === "anonymous") return { ok: false, reason: "anonymous", message: "" };
    if (loaded.status !== "ready") return { ok: false, reason: "failed", message: loaded.message || "Favoriler yüklenemedi. Lütfen tekrar dene." };
  }
  if (state.pending.has(productId)) return { ok: false, reason: "busy", message: "" };
  const gen = generation;
  const before = state.ids.has(productId);
  set({ ...state, ids: withId(state.ids, productId, wanted), pending: withId(state.pending, productId, true) });
  sync.beginWrite();
  const result = await (wanted ? apiSend<void>("PUT", `/favorites/${encodeURIComponent(productId)}`) : apiSend<void>("DELETE", `/favorites/${encodeURIComponent(productId)}`));
  sync.endWrite(); // yanıt ne olursa olsun (oturum değişse bile) sayaç dengelenir
  if (gen !== generation) return { ok: false, reason: "failed", message: "" }; // oturum değişti: bu yanıt artık kimsenin değil
  const pending = withId(state.pending, productId, false);
  if (result.ok) { set({ ...state, pending }); return { ok: true }; }
  if (result.status === 401) { set({ status: "anonymous", ids: EMPTY, pending: EMPTY, message: "" }); return { ok: false, reason: "anonymous", message: "" }; }
  if (result.status === 0 || result.status >= 500) {
    // Etki bilinmiyor: iyimser değişiklik korunmaz, sunucudaki küme yeniden okunur.
    set({ ...state, ids: withId(state.ids, productId, before), pending });
    await loadFavorites(true);
    return { ok: false, reason: "failed", message: UNCONFIRMED_MESSAGE };
  }
  set({ ...state, ids: withId(state.ids, productId, before), pending });
  // 409 favorite_limit mesajı lib/api.ts sözlüğünden gelir.
  const message = result.code === "favorite_limit" ? result.message
    : result.status === 404 || result.code === "product_not_found" ? "Bu ürün artık satışta değil; favorilere eklenemedi."
    : `${result.message} Favorilerin değişmedi.`;
  return { ok: false, reason: "failed", message };
}

const serverState = INITIAL;
export function useFavorites(options: { refresh?: boolean } = {}): FavoritesState {
  const current = useSyncExternalStore((l) => { listeners.add(l); return () => { listeners.delete(l); }; }, () => state, () => serverState);
  const refresh = options.refresh ?? false;
  useEffect(() => { void loadFavorites(refresh); }, [refresh]);
  return current;
}

export const fetchFavorites = () => apiGet<{ items: FavoriteItem[] }>("/favorites");

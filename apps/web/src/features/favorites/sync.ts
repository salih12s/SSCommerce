// Favori kümesi okuma/yazma yarışını çözen SAF mantık (react, ağ ve modül durumu yok; node:test ile doğrudan sınanır).
//
// Sorun: `GET /favorites/ids` yazmadan (PUT/DELETE) ÖNCE başlayıp yazma sürerken ya da bittikten sonra dönerse eski kümeyi getirir;
// bu yanıt iyimser kümeyi ezerse kalp sunucudakinin tersine döner. Çözüm "dönem" (epoch) sayacı: her yazmanın BAŞLANGICI ve BİTİŞİ
// dönemi artırır. Bir okuma ancak başladığı andan döndüğü ana kadar hiçbir yazma başlamadıysa/bitmediyse ve şu an yazma sürmüyorsa güvenilirdir.

export type ReadMark = Readonly<{ epoch: number }>;
/** apply: kümeyi sunucudakiyle değiştir. discard: yok say (yazma sürüyor, sonuç yazma bitince zaten kesinleşir). reread: yok say ve yeniden oku (araya yazma girdi, şu an yazma yok). */
export type ReadVerdict = "apply" | "discard" | "reread";

export type SyncTracker = {
  beginWrite(): void;
  /** Her yazma sonucu (başarı, ret, belirsiz, oturum değişti) için tam bir kez çağrılır. */
  endWrite(): void;
  startRead(): ReadMark;
  judge(mark: ReadMark): ReadVerdict;
  readonly inFlight: number;
};

export function createSyncTracker(): SyncTracker {
  let epoch = 0;
  let inFlight = 0;
  return {
    beginWrite() { epoch++; inFlight++; },
    endWrite() { epoch++; inFlight = Math.max(0, inFlight - 1); },
    startRead() { return { epoch }; },
    judge(mark) {
      if (inFlight > 0) return "discard";
      return mark.epoch === epoch ? "apply" : "reread";
    },
    get inFlight() { return inFlight; },
  };
}

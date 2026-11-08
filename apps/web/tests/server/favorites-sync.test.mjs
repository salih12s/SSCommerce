import assert from "node:assert/strict";
import test from "node:test";
import { createSyncTracker } from "../../src/features/favorites/sync.ts";

// Favori okuma/yazma yarışı. Üretimdeki favorites.ts aynı izleyiciyi kullanır; burada kümeyi ezen yanıtın nasıl yargılandığı sınanır.
// E2E yerine birim testi seçildi: yarış milisaniyelik ağ sırasına bağlıdır, route gecikmesiyle kurulan E2E zamanlamaya bağımlı (flaky) olurdu;
// saf izleyici ise her sırayı deterministik olarak kurar.

test("yazma yokken başlayıp dönen okuma uygulanır", () => {
  const t = createSyncTracker();
  const mark = t.startRead();
  assert.equal(t.judge(mark), "apply");
});

test("okuma yazmadan ÖNCE başlar, yazma BİTTİKTEN sonra döner: eski yanıt yok sayılır ve yeniden okunur", () => {
  const t = createSyncTracker();
  const mark = t.startRead();   // GET gönderildi (sunucu henüz favori yok görüyor)
  t.beginWrite();               // kullanıcı kalbe bastı, PUT gitti
  t.endWrite();                 // PUT 204 döndü
  assert.equal(t.judge(mark), "reread"); // GET şimdi döndü: eski/boş küme iyimser kümeyi EZMEZ
  assert.equal(t.judge(t.startRead()), "apply"); // yeniden okuma temizdir
});

test("okuma yazma SÜRERKEN dönerse (yazma henüz bitmedi) yok sayılır", () => {
  const t = createSyncTracker();
  const mark = t.startRead();
  t.beginWrite();
  assert.equal(t.judge(mark), "discard"); // eski sayaçla bu durum kaçırılırdı: yazma sayacı yalnız bitişte artıyordu
  t.endWrite();
});

test("okuma yazma sürerken başlayıp yazma bittikten sonra dönerse yeniden okunur", () => {
  const t = createSyncTracker();
  t.beginWrite();
  const mark = t.startRead();   // sunucu yazmayı henüz işlemiş olmayabilir
  t.endWrite();
  assert.equal(t.judge(mark), "reread");
});

test("okuma yazma sürerken başlar ve döner: uygulanmaz", () => {
  const t = createSyncTracker();
  t.beginWrite();
  const mark = t.startRead();
  assert.equal(t.judge(mark), "discard");
  t.endWrite();
});

test("yazma tamamen okumadan önce bitmişse okuma güvenilirdir", () => {
  const t = createSyncTracker();
  t.beginWrite(); t.endWrite();
  assert.equal(t.judge(t.startRead()), "apply");
});

test("üst üste iki yazmadan biri sürerken okuma uygulanmaz; ikisi de bitince yeniden okunur", () => {
  const t = createSyncTracker();
  const mark = t.startRead();
  t.beginWrite(); t.beginWrite();
  t.endWrite();
  assert.equal(t.inFlight, 1);
  assert.equal(t.judge(mark), "discard");
  t.endWrite();
  assert.equal(t.inFlight, 0);
  assert.equal(t.judge(mark), "reread");
});

test("başarısız ya da iptal yazma da dönemi ilerletir (sayaç dengelenir, eksiye düşmez)", () => {
  const t = createSyncTracker();
  const mark = t.startRead();
  t.beginWrite();
  t.endWrite();                 // ret / belirsiz sonuç / oturum değişti: hepsi endWrite çağırır
  t.endWrite();                 // fazladan çağrı bile eksi sayıya götürmez
  assert.equal(t.inFlight, 0);
  assert.equal(t.judge(mark), "reread");
});

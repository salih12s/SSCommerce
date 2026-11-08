# Faz 9 doğrulama kanıtı

Tarih: 2026-11-07/08. Dal `feat/phase-3-4-complete`, doğrulama `965ebad` üzerindeki commit edilmemiş çalışma ağacında yapıldı; aynı içerik 2026-11-08'de `919c7a9` (feat) + docs commit'i olarak push edildi. Etiketler: `LOCAL_PASS` · `BLOCKED_EXTERNAL` · `NOT_VERIFIED` · `PENDING_OWNER_APPROVAL`. **Hiçbir şey herkese açık barındırmada, herkese açık CA ile ya da hosted CI'da doğrulanmadı.**

## 1. Ortam ve köken

| Öğe | Değer |
|---|---|
| Makine | Intel i7-13620H, 16 mantıksal çekirdek, 31,7 GB RAM; Windows 11; Docker Engine 29.9.2 (VM ~15,5 GB) |
| Araçlar | .NET SDK 10.0.401, Node 24.4.1, npm 11.4.2, PostgreSQL 17 (alpine), Caddy 2.11.9, Playwright 1.64.1, actionlint 1.7.7 |
| Test veritabanı | Tek kullanımlık PostgreSQL 17 (127.0.0.1:5434, tmpfs); dev DB (5433) kullanılmadı |
| Yığın | `deploy/compose.yml`, proje `sscommerce-stack`; imajlar `sscommerce-{api,web,migrate}:local` (api/web son düzeltmelerden sonra yeniden derlendi; migrate imajı Faz 9 boyunca değişmedi çünkü migration eklenmedi) |
| Sınırlar | İstemci ve sunucu **aynı makinede** (ölçümler taban değeridir, iş hedefi değildir); TLS yerel CA ile |

## 2. Nihai Release kapısı (son kodda; çıkış kodları gerçek)

| Komut | Sonuç | Çıkış |
|---|---|---|
| `dotnet build backend/SSCommerce.slnx -c Release` | 0 uyarı, 0 hata | 0 |
| `dotnet test backend/SSCommerce.slnx -c Release` (gerçek PostgreSQL) | **2304 geçti / 0 kaldı / 0 atlandı** (3 dk 17 sn). Tarihsel Faz 8: 2195 → +109 | 0 |
| `npm run lint` · `npm run typecheck` | temiz · temiz | 0 · 0 |
| `npm run test:server` | **40 geçti / 0** (tarihsel 10) | 0 |
| `npm run build` | derlendi | 0 |
| `npm run test:e2e` (gerçek Next + API + PostgreSQL; 390/768/1440 + axe) | **213 geçti / 0 kaldı / 0 atlandı / flaky 0** (3,6 dk; tarihsel 210) | 0 |
| `npm run test:proxy` | PASS (sahte XFF gerçek Next→API hız sınırını aşamaz: 400/400/400/429) | 0 |
| `PROD_CREDS_FILE=… npm run test:prod` (üretim yığını, son imajlar + web `tmpfs`, gerçek Chromium, HTTPS) | **21 geçti / 0 / 0** (3,6 dk) — bkz. not 2 | 0 |
| `deploy/scripts/https-check.sh` (son imajlar) | **69 geçti / 0 kaldı** | 0 |
| `deploy/scripts/resilience-check.sh` (son imajlar) | **11 geçti / 0 kaldı** | 0 |
| `deploy/scripts/upgrade-drill.sh` (Faz 8 öncesi gerçek döküm; beklenen migration sayısı göç imajından türetilir) | **22 geçti / 0** | 0 |
| `backup` + `deploy/scripts/restore-verify.sh` (yük testlerinden sonraki gerçek veri, 7,6 MB döküm) | tüm kontroller geçti (6 migration + 23 tablo sayısı + 16 tetikleyici + 6 değişmez); negatif kontroller doğru başarısız | 0 |
| `actionlint` (`.github/workflows/ci.yml`) | temiz | 0 |
| `dotnet ef migrations has-pending-model-changes` | "No changes" | 0 |

Notlar:
1. İlk tam backend koşusunda 3 test kaldı; neden **test** hatasıydı (bool olmayan `Ordering:Enabled` değerinde bağlayıcı `OptionsValidationException` yerine `InvalidOperationException` atıyor, uygulama yine açılmıyor). Test davranışa göre düzeltildi, tam paket yeniden koşuldu. Üretim kodu bu düzeltme için değişmedi.
2. `test:prod` son imajlarla bir koşuda **20/21** verdi: `03-order-flow` "fixture +15 girdi ⇒ stok ≥ 15" varsayıyordu, oysa `02-csp` testleri her genişlikte bir sipariş verip 3 birim tüketiyor ve yük testleri kalan stoğu 0'a indirmişti. **Test varsayımı kırılgandı, ürün hatası değil**; eşik "sipariş verebilmek için ≥ 1"e çekildi, yeniden koşuda 21/21.
3. Yığın denetimleri (`https-check`, `resilience-check`, `test:prod`) ve **S1–S8 + K1 yük senaryolarının tamamı**, son düzeltmeden (D-086) sonra derlenen **son API/web imajlarıyla** yeniden koşuldu (QA incelemesi, önceki sürümde bazı senaryoların eski imajla koştuğunu yakaladı; bu tablo son koşunun sayılarıdır). Aynı koşuda web servisine `.next/cache` için `tmpfs` eklendi (salt okunur kök dosya sisteminde Next çalışma zamanı önbelleği yazılamıyordu) ve `test:prod` onunla 21/21 geçti. Not: bir ara derleme `NETSDK1018` ile sessizce başarısız olmuş ve yığın eski imajla çalışmıştı (Docker `ARG VERSION` ⇒ MSBuild `Version` çakışması); bu yakalandı, ARG `BUILD_VERSION` yapıldı ve `docs/DEPLOYMENT.md`'ye uyarı eklendi.

## 3. Kabul kapıları (G1–G10)

| Kapı | Durum | Kanıt |
|---|---|---|
| G1 Faz 0–8 davranışları | **LOCAL_PASS** | §2 (213 E2E + 2304 backend, taze koşu; yetkilendirme dosyalarında `git diff HEAD` boş) |
| G2 Yeniden üretilebilir üretim artefaktları, ayrı volume/port | **LOCAL_PASS** | Dockerfile'lar (digest'li), `compose.yml`; dev yığınıyla çakışma yok |
| G3 Gerçek HTTPS yolu, güvenli çerez/CSRF/forwarding | **LOCAL_PASS** (yerel CA; herkese açık CA değil) | `https-check.sh` 69; `test:prod` 21; `SECURITY_REVIEW_PHASE_9.md` §2 |
| G4 Data Protection kalıcılığı + sır güvenliği | **LOCAL_PASS** | `resilience-check.sh` 11 (restart, recreate, DB restart; ikinci anahtar halkası çerezi çözemez) |
| G5 Migration (taze/yükseltme), yedek-geri yükleme, en az yetki, kurtarma el kitabı | **LOCAL_PASS** | `upgrade-drill.sh` 22; `restore-verify.sh` + negatif kontroller; `OPERATIONS.md` §5–6, §9 |
| G6 CI yerelde doğrulandı; hosted CI etiketi | **LOCAL_PASS (yerel eşdeğer) · hosted `BLOCKED_EXTERNAL`** | actionlint; §2 komutları yerelde koştu; GitHub Actions faturalandırma kilidi sürüyor, **hosted koşu yok** |
| G7 Gözlemlenebilirlik + sağlık/toparlanma, hassas veri günlüğe girmiyor | **LOCAL_PASS** (bkz. §6) | metrik taraması 0 PII; günlük taraması 0 (§6); `ssc_*` sayaçları senaryolarla tutarlı |
| G8 Sınırlı yük + sipariş/stok tutarlılığı (yalıtılmış) | **LOCAL_PASS** | §5 |
| G9 Bağımsız Güvenlik/Sağlık/QA | **bkz. §6** | Güvenlik: PASS_WITH_FINDINGS, açık Critical/High/Medium yok; Sağlık: bulgular düzeltildi; QA: §6 |
| G10 Dokümantasyon + öğrenme haritası | **LOCAL_PASS** | `DEPLOYMENT`, `OPERATIONS`, `SECURITY_REVIEW_PHASE_9`, `PHASE_9_VERIFICATION`, `SSCOMMERCE_LEARNING_MAP` (269 yol çözülüyor), `RELEASE_NOTES`, D-077..D-086 |

## 4. Kabul eşikleri (ölçümden ÖNCE tanımlandı; SLA uydurulmadı)

Doğruluk eşikleri (**sıfır tolerans**): negatif stok yok; çift sipariş yok; sipariş sayısı = stok düşüşü; tüm DB değişmezleri 0 ihlal; kesintide HTTP 500 yok (yalnız 503 problem+json); kesinti sonrası API/web yeniden başlatılmadan toparlanma; aynı `Idempotency-Key` ile tekrar → tek sipariş. **Performans sayıları taban ölçümüdür**; iş hedefi (p95, rps, kullanılabilirlik) **`PENDING_OWNER_APPROVAL`** — ölçülen sayılar hedef olarak sunulmaz.

## 5. Yük, tutarlılık ve kesinti (yalıtılmış yığın; `deploy/loadtest/`)

Koşullar: yalnız test için hız sınırı katmanı (`compose.perf.yml`, IP başına 100000); sipariş sınırları üretim varsayılanında (3 açık / 10 günlük). Katman sonra **kaldırıldı** (api env'inde `RateLimit` yok; son yığın denetimleri katmansız koştu). **Aşağıdaki tablo SON imajla (D-084/D-085/D-086 dahil) yapılan koşunun sayılarıdır.**

| Senaryo | Sonuç | Ölçümler (p50/p95/p99 ms) | Değişmezler |
|---|---|---|---|
| S1 okuma karışımı, eşzamanlılık 16, 30 sn | GEÇTİ | 3002 istek hepsi 200, 98,9 rps; 90/365/435 | 0 |
| S1, eşzamanlılık 64, 30 sn | GEÇTİ | 4020 istek hepsi 200, 131,8 rps; 230/1036/1228 | 0 |
| S2 kayıt+giriş+me, 150 kullanıcı | GEÇTİ | 150/150 tam akış, hata 0, 256 rps; 15,6/153,9/613,7 | 0 |
| S3 sipariş akışı, 150 kullanıcı (C=32) | GEÇTİ | 150×201; satılan 225 adet = stok düşüşü 225; fiyat anlık görüntüsü korundu; `POST /orders` 174/968/1753 | 0 |
| S4 son birim yarışı (3 tur × 40 eşzamanlı, stok 3) | GEÇTİ | her turda tam 3×201 + 37×409 `insufficient_stock`; 5xx yok; final stok 0 | 0 |
| S5 idempotent tekrar (30 kullanıcı × 6 eşzamanlı) | GEÇTİ | 180 istek: 30×201 + 150×200 `replayed:true`; aynı anahtar + farklı gövde reddedilir, yeni sipariş yok | 0 |
| S6 çift iptal (30 sipariş × 2 eşzamanlı) | GEÇTİ | 60×200 (30'u `replayed:true`; CONTRACT_PHASE_7); stok başlangıca döndü | 0 |
| S7 sınırlar | GEÇTİ | 3×201, 4. `409 open_order_limit`; ayrı kullanıcı bağımsız; 10×201 + 11. `429 order_daily_limit` | 0 |
| S8 yönetim okuma (Admin liste/pano/hareket), eşzamanlılık 8, 20 sn | GEÇTİ | 8590 istek hepsi 200, 429 rps; 17,7/25/29,2 | 0 |
| **K1 DB kesintisi** (2500 hazır müşteri, 64 işçi, DB 12 sn kapalı) | **GEÇTİ** | **500 yok**; kesintiye denk gelen **139** sipariş isteği 503/ağ hatası aldı (`service_unavailable`); DB dönünce ilk başarılı `POST /orders` 1490 ms, `/readyz` 671 ms; API/web yeniden başlamadı; aynı anahtarla yeniden gönderim: **133 yeni 201 + 6 `replayed`** (commit olmuş, yanıt görülmemiş); DB'de tam **2500** sipariş; kullanıcı başına 1; stok düşüşü 2500 = kalem adedi | 0 |

**Bulunan gerçek hata (D-086):** yeni imajla ilk S4 koşusunda yarış doğrulamaları geçti ama bir **kayıt isteği HTTP 500** verdi (120'den 1'i). Kök neden: `AuthEndpoints` kayıt yanıtında paylaşılan, dondurulmamış `JsonSerializerOptions`'ı `Results.Json`'a veriyordu; süreç yeni başlamışken eşzamanlı ilk iki istek seçenekleri aynı anda değiştirmeye çalışınca `InvalidOperationException`. Faz 9 öncesinden var olan gizli bir yarıştı. Düzeltme: seçenekler çözümleyiciyle oluşturulup `MakeReadOnly()` yapıldı; 2 regresyon testi (biri **mutasyon kontrolüyle** doğrulandı: düzeltme geri alınınca deterministik test kırıldı, geri yüklenince geçti; eşzamanlılık testi olasılıksal olduğundan tek başına kanıt sayılmaz). Düzeltilmiş imajla **taze API sürecinde üç ardışık S4 koşusu** ve ardından tam S1–S8 + K1 koşusu GEÇTİ.

İlk (zayıf) K1 koşusu yalnız 4 sipariş isteğini kesintiye denk getirmişti; güçlendirilmiş senaryo bunu 64, son imajla 139'a çıkardı ve "commit oldu ama yanıt görülmedi" durumunun gerçek örneklerini (5, 8, 6) yakaladı. **Metrik tutarlılığı:** `ssc_orders_total` farkı istemcinin gördüğü durumlarla tutarlı (S4: placed 9 / stock_conflict 110 ↔ 201:9, 409:110).

**Bilinen ölçüm notları:** (1) `http_server_requests_total{route=…auth/register,status_class="4xx"}` yük koşularında kayıt sayısına eşit görünür: kök neden **yük aracının** çerez deposu (yeni müşteride ilk kayıt denemesi CSRF'siz gidip yeniden deneniyor; müşteri başına 3 CSRF GET görülür). Sunucu sayacı doğru: tek temiz `curl` kaydı yalnız `2xx`'i +1 yaptı. (2) Windows'ta SIGTERM zarif kapanışının gerçek süreç testi atlanır (mantık birim testli); Linux'ta doğrulanmadı. (3) Soak, uzun süreli stres/spike testi yapılmadı (`NOT_VERIFIED`). (4) Boşta bellek: api ~61 MiB, web ~90 MiB (testlerden sonra ~235 MiB), edge ~23 MiB, db ~30 MiB. (5) Tüm sayılar istemci ve sunucu aynı makinedeyken alınmış **taban ölçümüdür**; iş hedefi değildir.

## 6. Bağımsız kapılar

- **Güvenlik (`security-auditor`, statik):** Tur 1 PASS_WITH_FINDINGS (3 Medium: B1 IPv6 hız sınırı kovası, B2 sipariş anahtarı fail-open, B3 `pg_hba` loopback `trust`); üçü **düzeltildi ve yeniden doğrulandı**. Tur 2: **PASS_WITH_FINDINGS, açık Critical/High/Medium yok.** Ayrıntı: `docs/SECURITY_REVIEW_PHASE_9.md`.
- **Sağlık (`health-observer`, çalışma zamanı): PASS_WITH_FINDINGS**, bulgular düzeltildi. Gözlemci 18 tur sınırında H6/H7'yi yapamadı; bunları lead ölçtü (aşağıda). Gözlemcinin ölçümleri: `/health/live` DB kapalıyken 200 (191 örnek, en yüksek 61 ms); `/health/ready` DB kapalıyken 503 ama **5–7,7 sn** (hedef 3 sn) → **D-084**: duvar saati sınırı, düzeltme sonrası canlı yığında **~3,0 sn**; web `/readyz` 2,5 sn; DB dönünce yeniden başlatmasız toparlanma 0,3–0,7 sn; soğuk başlangıç (5× `docker restart api`) ilk 200'e 1187–1276 ms, yanlış 503 yok (eski ~3 sn riski yok); API ölüyken kenarda **6×500 + 8 sn zaman aşımı** → **D-085**: kenar/web artık 503 problem+json + `Retry-After` (en kötü durum ~15 sn, bayat keep-alive soketleri; önceden 30 sn/504); `docker kill` sonrası konteynerin yeniden başlamaması Docker'ın "elle durdurma" semantiğidir: lead gerçek süreç ölümüyle doğruladı (**PID 1'e SIGTERM ⇒ api ve web otomatik yeniden başladı, RestartCount 0→1, 7–8 sn'de healthy**). Metrik tutarlılığı ve `X-Request-Id` kenarda ezilmesi doğrulandı.
  - **H6 (lead ölçümü, günlük hijyeni):** son koşulardan sonra api/web/edge/db günlüklerinde `@example.test` e-postası, parola, çerez (`__Host-ssc`, `Set-Cookie`), CSRF, `Authorization`, bağlantı dizesi, alıcı/adres alanı eşleşmesi **0** (edge'deki "phone" eşleşmeleri `Permissions-Policy: microphone=()` başlığı; DB günlüğündeki tek satır parametresiz SQL metni `WHERE "Id" = $1`). API `Error`/`Warning` günlüğü son süreçte 0.
  - **H7:** tüm konteynerlerde RestartCount 0, OOMKilled false, "Read-only file system" hatası 0.
  - **Hâlâ NOT_VERIFIED:** web ↔ api günlüklerinde aynı `requestId`'nin yan yana görülmesi (kenar ürettiği kimlik yanıtta doğrulandı, `process.test.mjs` upstream'e ulaştığını kanıtlıyor); histogram kovaları; SYN yanıtsız IP'ye bağlantı aşaması zaman aşımı (en kötü gözlenen ~15 sn).
- **QA (`qa-reviewer`, bağımsız son kod incelemesi): PASS_WITH_FINDINGS.** Critical/High yok; kodda kusur bulunmadı (BoundedWork, upstream.mjs imzası, validator, RateLimitKey, ordering kapısı, betikler incelendi). Kendi koşuları: lint/typecheck temiz, `test:server` 40/40, ilgili backend testleri 121/0/0, 9 betiğin `sh -n` kontrolü temiz. **2 Medium (belge/kanıt) ve 6 Low bulgu, hepsi işlendi:** (1) bazı yük senaryolarının eski imajla koştuğu → tümü son imajla yeniden koşuldu, tablo güncellendi; (2) PROGRESS.md'de Faz 9 kapanış bölümü ve tablo satırı eksikti → eklendi, ara sayılar "ara" diye işaretlendi; Low: CI API derleme argümanı `BUILD_VERSION` (düzeltildi), `upgrade-drill.sh` sabit migration sayısı (göç imajından türetiliyor), `backup-inner.sh` hata denetimi ve manifest ifadesi (düzeltildi), web `.next/cache` yazılabilirliği (`tmpfs` eklendi, kanıtlandı), `SSC_PUBLIC_ORIGIN` zorunlu (`:?`), `stack-fixtures.sh` temizlik (`trap`) ve bootstrap'ın Development ortamında koştuğu (OPERATIONS §8'e yazıldı). Kapsanmayanlar (QA'nın NOT_VERIFIED listesi): hosted CI, Linux SIGTERM; imajın son kaynakla derlendiği artık bu belgede gösterilen imaj-id eşleşmesi ve S4 yeniden koşusuyla desteklenir.

## 7. Sınırlamalar ve dışsal bağımlılıklar

`BLOCKED_EXTERNAL`: barındırma/alan adı/DNS, herkese açık CA TLS, hosted CI (GitHub faturalandırma). `NOT_VERIFIED`: gerçek internet istemcileri, çok-hop canlı proxy zinciri, soak/spike/stress, Linux SIGTERM, farklı donanım, DB adı çözümleme gecikmesinin doğrudan birim testi (kanıt: sessiz TCP sunucusu + iptali yok sayan iş testleri ve canlı ~3,0 sn ölçümü). DB konteyneri kaldırıldığında API'nin veritabanı çağrıları da Docker DNS bekleyişine takılır: kenarda 503 problem+json ~5 sn (bir örnekte 9,9 sn) sürer; 500 yoktur. `PENDING_OWNER_APPROVAL`: herkese açık sipariş (D-061/D-071/D-072), performans hedefleri, yedek saklama süresi ve zamanlayıcı, Git işlemleri (commit/push/PR/tag/main).

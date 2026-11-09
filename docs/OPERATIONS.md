# SSCommerce işletim el kitabı (Faz 9)

Kapsam: `deploy/compose.yml` ile kurulan yığın (bkz. `docs/DEPLOYMENT.md`). Hiçbir komut sır yazdırmaz; sırlar `deploy/.env.stack` dosyasındadır. **Yıkıcı komutlar bu kitapta yoktur**: `docker compose down -v`, volume silme ve `DROP` kullanılmaz. Her bölümde "KANIT" satırı, bu davranışın hangi betikle gerçekten sınandığını söyler; kanıtı olmayanlar "ÖNERİ"dir.

Kısaltma: `D="docker compose -f deploy/compose.yml --env-file deploy/.env.stack"`

## 1. Başlatma / durdurma

- Başlat: `$D up -d` (sıra: db → migrate → api → web → edge). Durum: `$D ps` (`api`, `web`, `db` healthy; `migrate` exited 0).
- Durdur (veri kalır): `$D stop`. Yeniden başlat: `$D restart api web`. Kapanış zarifçe yapılır: API ≤30 sn, web ≤10 sn sürmekte olan istekleri bekler.
- KANIT: `resilience-check.sh` (restart, `--force-recreate`, DB restart sonrası oturum ve veri sürer).

## 2. Sağlık ve uyarı örnekleri

| Uç | Anlamı | Kim sorar |
|---|---|---|
| API `GET /health/live` | Süreç ayakta (bağımlılığa bakmaz) | konteyner yoklaması (iç) |
| API `GET /health/ready` | PostgreSQL erişilir **ve** bekleyen migration yok; 503 = hazır değil | konteyner yoklaması, web `/readyz` |
| Web `GET /healthz`, `/readyz` | Web ayakta / API hazır | konteyner yoklaması; dış izleme için `/readyz` kullanılabilir (ayrıntı yok) |
| Edge `GET /edge-health` | Caddy ayakta | dış izleme |

Dış uç `/api/backend/health*` bilerek 404'tür. Önerilen alarm kuralları (**yapılandırma örneği; etkin bir izleme servisi KURULU DEĞİL**): `readyz` 2 ardışık yoklamada 503; `ssc_ready_state == 0` (>1 dk); `rate(http_server_requests_total{status_class="5xx"}[5m])` yükselişi; `ssc_orders_total{outcome=~"busy|open_order_limit|order_daily_limit"}` ani artış; `ssc_db_unavailable_total` artışı; yedek yaşı > 25 saat.

## 3. Günlük ve tanı

- `$D logs --since 10m api | web | edge | db`. API/web günlükleri satır başına JSON; alanlar: `method`, `route` (şablon), `status`, `durationMs`, `requestId`. **Bir isteği izleme**: istemciden gelen/yanıttaki `X-Request-Id` değerini üç servisin günlüğünde ara (edge üretir, web ve API aynı kimliği taşır).
- Günlüklerde çerez, CSRF jetonu, gövde, sorgu dizesi, e-posta, adres, telefon, parola bulunmaz (test kanıtı: `RequestLoggingTests`, `tests/server/handler.test.mjs`, metrik taraması).
- Metrik (iç): `docker run --rm --network sscommerce-stack_backend --entrypoint node sscommerce-web:local -e 'fetch("http://172.28.77.12:9100/metrics").then(r=>r.text()).then(console.log)'`. Sayaçlar: `http_server_requests_total{method,route,status_class}`, `http_server_request_duration_seconds` (histogram), `ssc_orders_total{outcome}`, `ssc_db_unavailable_total`, `ssc_ready_state`, `ssc_ready_transitions_total{to}`. `/metrics` internete **açılmaz**; toplayıcı eklenecekse iç ağda çalıştırın ve portu yayımlamayın.
- Saklama/örnekleme: günlükler Docker `json-file` sürücüsünde (varsayılan sınırsız; **sahip kararı**: `max-size`/`max-file` ayarlayın); metrikler bellekte, süreçle birlikte sıfırlanır; iz (trace) örnekleme yoktur.

## 4. Sorun giderme el kitabı

| Belirti | Olası neden | Eylem |
|---|---|---|
| `readyz` 503, `db` unhealthy | PostgreSQL kapalı/disk dolu | `$D ps`, `$D logs db`, `docker system df`; yer aç (volume silme!), `$D restart db`. API/web yeniden başlatma GEREKMEZ: DB dönünce hazır olur (KANIT: sağlık gözlemci Faz 8 + `resilience-check.sh`). |
| `readyz` 503, `db` healthy | **Bekleyen migration** | `$D logs migrate`; `$D up -d migrate`. Göç başarısızsa `api` zaten başlamaz (bağımlılık). |
| `migrate` exit ≠ 0 | Bozuk/uyumsuz şema, rol parolası yanlış | `$D logs migrate` (sır yok); idempotent, düzelttikten sonra tekrar çalıştır. Kısmi uygulanmışsa yedekten geri yükleme provası yap (§5), üretimi elle düzenleme. |
| 503 `service_unavailable` (API) | DB bağlantısı yok | Yukarıdaki ilk satır. `ssc_db_unavailable_total` artar. |
| 503 `busy` (sipariş/katalog yazma) | 5 sn kilit zaman aşımı/deadlock; hiçbir şey yazılmadı | İstemci güvenle tekrar dener. Süreklileşirse uzun işlem arayın: `select pid, state, now()-xact_start, left(query,60) from pg_stat_activity where datname = current_database() order by 3 desc;` (süper kullanıcıyla). |
| Çok sayıda 429 | Gerçek yoğunluk ya da kötüye kullanım | `Retry-After` doğru çalışır. IP başına limitler `Security__RateLimit__*`. Tüm istemciler tek IP görünüyorsa **güvenilir proxy listesi yanlış** (`WEB_TRUSTED_PROXIES`/`Security__TrustedProxies__*`) → herkes aynı kovada kalır. |
| Sipariş yavaş | DB kilit bekleme, havuz tükenmesi (30) | `http_server_request_duration_seconds` ve `pg_stat_activity`; havuz `Maximum Pool Size`. |
| Şüpheli sipariş kötüye kullanımı | Çok hesapla stok tüketimi (D-061/D-072) | Admin panelinde ilgili siparişleri iptal et (stok iade); `ORDERING_ENABLED=false` + `$D up -d api` ile **sunucuda** siparişi kapat (arayüz düğmesi değil, API 503 verir; KANIT: `OrderingSwitchTests`, canlı 503). |
| Yeniden başlatma sonrası herkes çıkış yapmış | `/keys` volume'ü kayıp/boş | Veri kaybı yok; kullanıcılar yeniden giriş yapar. `sscommerce-stack-dpkeys` yedeğini kontrol et; ortamlar arası anahtar paylaşma. |
| Yedek başarısız | Disk dolu / yetki / DB kapalı | `$D --profile tools run --rm backup` çıktısı; `docker system df`; `ssc_backup` parolası. |

## 5. Yedekleme ve geri yükleme

**Yedek** (tutarlı `pg_dump -Fc`, `ssc_backup` salt okunur rolü, sağlama toplamı + manifest; `sscommerce-stack-backups` volume'üne):
```sh
$D --profile tools run --rm backup
docker run --rm -v sscommerce-stack-backups:/b:ro postgres:17-alpine ls -l /b      # dosyalar: .dump .dump.sha256 .manifest
```
Ana makineye kopya (yedeği AYRI bir diske/şifreli yere taşıyın): `docker run --rm -v sscommerce-stack-backups:/b:ro -v <hedef>:/out postgres:17-alpine cp /b/<ad>.dump /b/<ad>.dump.sha256 /b/<ad>.manifest /out/`.
Saklama süresi ve zamanlama **sahip kararı** (öneri: günlük + haftalık; zamanlayıcı bu repo'da KURULU DEĞİL). Yedek PII içerir (kullanıcılar, adresler, siparişler): erişimi kısıtlayın, depoya koymayın.

**Geri yükleme provası** (kaynağa dokunmaz; tek kullanımlık, ağsız sunucu):
```sh
sh deploy/scripts/restore-verify.sh sscommerce-<zaman>          # volume'deki yedek
sh deploy/scripts/restore-verify.sh --file /yol/x.dump [/yol/x.manifest]
```
Doğrular: sağlama toplamı, `pg_restore --exit-on-error`, manifest sayıları (her tablo + migration kayıtları + tetikleyici sayısı), 6 iş değişmezi (negatif stok yok, stok = hareket toplamı, sipariş ara toplamı = kalemler, kalem = birim×adet, tahsis = kalem adedi, tek idempotency anahtarı). Çıkış 0 = geçti. KANIT: bkz. `docs/PHASE_9_VERIFICATION.md` (gerçek sipariş verili yedek).

**Gerçek kurtarma (felaket) — yalnız sahip onayıyla ve geri alınabilir sırayla:**
1. Yedeği **ayrı adlı** bir veritabanına geri yükle (`createdb sscommerce_restore` + `pg_restore --no-owner`), `invariants.sql` ve `restore-verify.sh` ile doğrula.
2. Uygulamayı durdur (`$D stop api web`); mevcut (bozuk) veritabanını **yeniden adlandır, silme** (`ALTER DATABASE … RENAME`).
3. Doğrulanmış kopyayı `sscommerce` adına al (RENAME), `ssc_owner` sahipliği ve `post-migrate.sql` yetkilerini uygula (`$D up -d migrate`), sonra `up -d api web`.
4. Eski veritabanı en az bir saklama dönemi boyunca durur; ancak sahip kararıyla silinir.

## 6. Uygulama geri alma ve migration

Şema göçleri **ileri yönlüdür**. Uygulama imajını eski sürüme almak, şema yeni sürümdeyse hata verebilir (yeni sütun/tetikleyici beklenir ya da eski kod yeni kısıta takılır). Sıra: (1) göçten önce yedek (§5), (2) göç, (3) sorun varsa önce **uygulamayı düzelterek ileri** git; (4) yalnızca veri bozulması varsa yedekten kurtarma (§5). Gerçek iş verisi (siparişler, stok hareketleri) uygulama geri alınarak **geri alınamaz**. KANIT (yükseltme): `upgrade-drill.sh`.

## 7. Periyodik bakım (öneri)

Günlük: yedek + `restore-verify.sh` haftalık; haftalık: `docker system df`, `$D logs --since 24h | grep -c '"level":"error"'`; aylık: temel imaj güncellemesi (DEPLOYMENT §8) ve tüm kapıların yeniden koşumu; her sürümde: `dotnet list package --vulnerable`, `npm audit`; anahtar/parola rotasyonu: DB rol parolası `ALTER ROLE` (yeni değeri `.env.stack`'e yaz, `up -d api`), Data Protection anahtarları kendiliğinden döner (90 gün; eski anahtarlar silinmez).

## 8. İlk Admin ve hesap kurtarma

> `api-command.sh` komutu tek kullanımlık konteynerde `ASPNETCORE_ENVIRONMENT=Development` ile çalışır (komutun kendi korumaları Development + loopback ister; Production fail-fast bu yolda devre dışıdır). Konteyner yalnız `ssc_app` parolasını alır, ağ açmaz, iş bitince silinir. Bu yüzden komut yalnız operatörün Docker erişimi olan makinesinde çalıştırılmalıdır.

İlk Admin: `sh deploy/scripts/api-command.sh bootstrap-admin <kayıtlı e-posta>` (yalnız Admin yokken çalışır; sonrası rol API'siyle). Son Admin'in rolü API'den kaldırılamaz (`last_admin`). Tüm Admin'ler kilitlendiyse: yeni bir hesap kaydedin, **Admin yokken** komut çalışır; Admin satırı varsa sahip rolüyle (`ssc_owner`) elle SQL gerekir ve denetim kaydı bırakılmalıdır — bu bir acil durum prosedürüdür, önceden yedek alın.

## 9. pg_hba sertleştirme (mevcut küme)

Resmi `postgres` imajı TCP loopback (`127.0.0.1`, `::1`) için `trust` yazar; bu, veritabanı konteynerinin **ağ ad alanına katılan** her sürecin (ör. `docker run --network container:<db>`) parolasız, süper kullanıcı dahil bağlanabilmesi demektir (güvenlik denetimi B3; gerçek yığında doğrulandı ve düzeltildi). Yeni kümelerde `deploy/db/init/99-harden-hba.sh` bunu otomatik düzeltir (yalnız boş veri dizininde çalışır). **Var olan küme için** (örn. bu Faz 9 kurulumundan önce açılmış):
```sh
docker exec $(docker compose -f deploy/compose.yml --env-file deploy/.env.stack ps -q db) sh -c \
 'sed -i -E "s#^(host[[:space:]]+(all|replication)[[:space:]]+all[[:space:]]+(127\.0\.0\.1/32|::1/128)[[:space:]]+)trust#\1scram-sha-256#" /var/lib/postgresql/data/pg_hba.conf'
docker compose -f deploy/compose.yml --env-file deploy/.env.stack exec -T db sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -X -At -c "select pg_reload_conf()"'
```
Doğrulama: `docker exec <db> psql -h 127.0.0.1 -U ssc_app -d sscommerce -w -c "select 1"` → `fe_sendauth: no password supplied` (reddedilmeli). Yerel Unix soketi konteyner içinde `trust` kalır (healthcheck ve init buna bağlıdır; yalnız `docker exec` yetkisiyle erişilir, o da zaten kök eşdeğeridir).

## 10. Sipariş verme anahtarı (üretim)

`Ordering__Enabled` Production/Staging'de **açıkça `true` ya da `false`** verilmelidir; verilmezse API açılmaz (fail-closed, güvenlik denetimi B2). Değişiklik ortam değişkeniyle yapıldığından API yeniden başlatılır: `ORDERING_ENABLED=false docker compose … up -d api`. Dosya tabanlı yapılandırma canlı yeniden yüklenir (IOptionsMonitor), ortam değişkeni yüklenmez.

## 11. Yük/perf katmanı üretimde KULLANILMAZ

`deploy/compose.perf.yml` hız sınırlarını 100000'e çıkarır (tek IP'den yük testi için). Üretimde ve operatör komutlarında **yalnız `-f deploy/compose.yml`** kullanılır; `docker compose … config | grep RateLimit` ile doğrulanabilir (bu satırlar compose.yml'de yoktur ⇒ kod varsayılanları 10/60/120 geçerli).

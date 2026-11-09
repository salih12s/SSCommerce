# SSCommerce dağıtım rehberi (Faz 9)

Durum etiketi: **`LOCAL_PASS`** (yalıtılmış, üretim-eşdeğeri yığın bu makinede doğrulandı). **`STAGING_PASS` / `PRODUCTION_VERIFIED` YOK**: herkese açık barındırma, alan adı, DNS ve herkese açık CA sertifikası sahip kararı ve erişimi bekliyor (`BLOCKED_EXTERNAL` / `PENDING_OWNER_ACTION`, bkz. §9). Kanıtlar: `docs/PHASE_9_VERIFICATION.md`.

## 1. Topoloji

```
Tarayıcı ──HTTPS──▶ edge (Caddy) ──HTTP──▶ web (Node, server.mjs) ──HTTP──▶ api (ASP.NET Core) ──▶ db (PostgreSQL 17)
 :8443/:8080 (127.0.0.1)   172.28.77.10          172.28.77.11:3000            172.28.77.12:8080        172.28.77.20:5432
                                                                              metrik: 172.28.77.12:9100 (yalnız iç)
```

- **edge**: TLS sonlandırır, HTTP→HTTPS 308, HSTS, `X-Request-Id` üretir (istemcininkini ezer), gövde sınırı 1 MB, iç uçları (`/api/backend/health*`, `/metrics`, `/openapi*`) 404 yapar; yukarı akış (web) ulaşılamazsa `/api/backend/*` için 502/503/504 yerine standart **503 problem+json + `Retry-After`** döner (yanıt başlığı zaman aşımı 15 sn). Yalnız edge port yayımlar.
- **web**: `node server.mjs` (Next özel sunucu). `next start` ve `output: 'standalone'` **kullanılmaz**: `server/forwarding.mjs` (XFF/XFP temizliği), `server/privacy.mjs`, CSP/nonce ve erişim günlüğü `server.mjs` girişindedir. Tarayıcı yalnız bu origin'e konuşur; `/api/backend/*` API'ye yeniden yazılır (D-006).
- **api**: yalnız web'den gelen istekleri kabul eder (ağ yalıtımı + `AllowedHosts`). Yetki yalnız burada uygulanır.
- **db**: yalnız `backend` ağında; hiçbir port yayımlanmaz.
- **backend ağı `internal: true`**: konteynerlerden internete çıkış ve ana makineden doğrudan erişim yoktur.

Dev yığınından (kök `docker-compose.yml`, proje `sscommerce`, 5433, volume `sscommerce-pgdata`) **ayrıdır**: proje adı `sscommerce-stack`, volume'ler `sscommerce-stack-*`, portlar 8443/8080. Dev veritabanına bağlanmaz.

## 2. Gereksinimler

Bu makinede doğrulanan: Docker Engine 29.9.2 (Compose v2), Docker VM ~15,5 GB RAM, 16 mantıksal çekirdek. Boşta ölçülen bellek: api ~61 MiB, web ~90 MiB, edge ~23 MiB, db ~30 MiB (yük altındaki değerler `docs/PHASE_9_VERIFICATION.md`). İmaj boyutları: api 204 MB, migrate 423 MB, web 987 MB. Daha küçük sunucular için ayrıca ölçüm gerekir (yapılmadı). İmajlar: .NET 10 (alpine), Node 24 (alpine), PostgreSQL 17 (alpine), Caddy 2 (alpine); hepsi `etiket@sha256:` ile sabit. Derleme internet gerektirir (NuGet/npm); çalışma gerektirmez.

## 3. Yapılandırma şeması

Sır kaynağı `deploy/.env.stack` (**Git dışı**; `.env.*` kuralı yoksayar). Şablon: `deploy/stack.env.example` (yalnız yer tutucu). Rastgele sırlar: `sh deploy/scripts/gen-env.sh` (ekrana yazmaz, var olan dosyanın üzerine yazmaz).

| Değişken | Servis | Anlamı |
|---|---|---|
| `POSTGRES_DB`, `PG_SUPERUSER`, `PG_SUPERUSER_PASSWORD` | db | Küme süper kullanıcısı; yalnız bakım, uygulama/göç bağlanmaz |
| `SSC_OWNER_PASSWORD` | db, migrate | `ssc_owner`: şema sahibi + göç (DDL) |
| `SSC_APP_PASSWORD` | db, api | `ssc_app`: çalışma zamanı, asgari yetki |
| `SSC_BACKUP_PASSWORD` | db, backup | `ssc_backup`: `pg_read_all_data` (yalnız okuma) |
| `SSC_PUBLIC_ORIGIN` | api | Tarayıcının gördüğü köken → `Security__AllowedOrigins__0` (CSRF Origin kontrolü). Mutlak `https`, yol/sorgu yok |
| `EDGE_SITE`, `HSTS_MAX_AGE` | edge | Site adı; HSTS süresi (yerelde 300 sn, gerçek alan adında 31536000) |
| `ORDERING_ENABLED` | api | `Ordering__Enabled`. **Varsayılan `false`** (D-072): kapalıyken `POST /api/v1/orders` sunucuda 503 `ordering_disabled` |
| `SSC_VERSION`, `SSC_GIT_SHA` | build | İmaj etiketi ve OCI etiketleri |

compose'un sabitlediği (değiştirilmemesi gereken) girdiler: `API_BASE_URL` (web'de **derleme zamanında** gömülür ve `src/proxy.ts` çalışma zamanında aynı değeri okur → ikisi aynı olmalı; derlemede yoksa `next build` hata verir), `WEB_HOST`, `PORT`, `WEB_TRUSTED_PROXIES=172.28.77.10` (yalnız edge), `Security__TrustedProxies__0=172.28.77.11` (yalnız web), `DataProtection__KeysPath=/keys`, `Metrics__Port=9100`, `Metrics__BindAddress`, `HostOptions__ShutdownTimeout`, `Logging__Console__FormatterName=json`.

**Üretim fail-fast (B5):** `ASPNETCORE_ENVIRONMENT` `Production`/`Staging` iken `ConnectionStrings__Postgres`, `Security__AllowedOrigins__*` (mutlak https) ve `DataProtection__KeysPath` yoksa API **açılmaz**; hata iletisi değer içermez.

## 4. Komutlar

```sh
sh deploy/scripts/gen-env.sh
docker compose -f deploy/compose.yml --env-file deploy/.env.stack up -d --build   # db -> migrate (tek seferlik) -> api -> web -> edge
docker compose -f deploy/compose.yml --env-file deploy/.env.stack ps              # hepsi healthy; migrate "exited (0)" olmalı
```

Sıra bağımlılıkla zorlanır: `migrate`, `db` sağlıklı olunca çalışır; `api`, `migrate` başarıyla bitince; `web`, `api` sağlıklıyken; `edge`, `web` sağlıklıyken. Göç **idempotent**tir (aynı betik tekrar çalıştırılabilir). `up` hiçbir veriyi silmez; `down -v` ve volume silme **kullanılmaz**.

- **Yeniden başlatma / yeniden dağıtım**: `docker compose … restart api web` ya da `up -d --build api web` (volume'ler kalır; oturumlar sürer, bkz. §6).
- **İlk Admin** (HTTP ucu yok, D-070): önce siteden normal kayıt, sonra `sh deploy/scripts/api-command.sh bootstrap-admin <e-posta>` (gerçek `--bootstrap-admin` komutu; tek kullanımlık konteyner DB konteynerinin ağ ad alanına katılır, böylece `Host=127.0.0.1` kuralı sağlanır; çalışma rolüyle çalışır, ağ açmaz).
- **Demo veri** (yalnız drill/demo): `sh deploy/scripts/api-command.sh seed-demo-catalog`. Gerçek mağaza kataloğu Admin panelinden girilir.
- **Derleme uyarısı:** Dockerfile `ARG` adı `VERSION` OLAMAZ (Docker bunu ortam değişkeni yapar, MSBuild `$(Version)` sanıp `NETSDK1018` verir; derleme başarısız olur ve yığın sessizce ESKİ imajla çalışır). API için `BUILD_VERSION` kullanılır. **Her `build` sonrası çıkış kodunu denetleyin** ve `docker images` ile imaj yaşına bakın.
- **Sürüm damgası**: `SSC_VERSION=1.0.0 SSC_GIT_SHA=$(git rev-parse --short HEAD) docker compose … build`. İmajlar `sscommerce-{api,web,migrate}:<SSC_VERSION>`; etiket + OCI `revision` etiketi hangi commit'ten üretildiğini söyler.

## 5. Veritabanı: şema, göç ve en az yetki

- Şema göçü, derleme sırasında `dotnet ef migrations script --idempotent` ile üretilip `migrate` imajına konur; göç işi `psql` ile uygular, ardından `deploy/db/post-migrate.sql` yetkileri normalleştirir. Uygulama kodu göç imajında yoktur.
- Roller (`deploy/db/init/10-roles.sh`, **yalnız boş veri dizininde bir kez**): `ssc_owner` (sahip/göç), `ssc_app` (çalışma), `ssc_backup` (okuma). `ssc_app`: tablo sahibi değil → tetikleyicileri kapatamaz, DDL/TRUNCATE yok; yalnız-ekleme tablolarında (admin_audit_log, stock_movements, order_status_history, order_items, order_inventory_allocations) UPDATE/DELETE yetkisi de alınmıştır (tetikleyici ikinci engeldir); `orders`'ta DELETE yok; `__EFMigrationsHistory` yalnız okunur.
- Yeni göç = yeni imaj. **Yalnızca ileri** (forward-only): bir şema göçünü geri alan otomatik yol yoktur. Uygulama sürümünü geri almak, şema yeni sürümdeyse uyumsuz olabilir → bkz. `docs/OPERATIONS.md` §6.
- Doğrulama betikleri: `deploy/scripts/upgrade-drill.sh` (eski dökümden yükseltme), `restore-verify.sh` (yedekten geri yükleme), `deploy/db/invariants.sql` (iş değişmezleri).

## 6. TLS, çerezler ve proxy güveni

- Yerel doğrulamada Caddy `tls internal` (kendi yerel CA'sı) kullanır: **herkese açık güvenilir sertifika değildir**. Gerçek alan adında `tls internal` satırı silinir; Caddy Let's Encrypt/ZeroSSL'den otomatik sertifika alır (80/443 gerekir, `EDGE_SITE=<alan adı>`, `EDGE_PORT_SUFFIX=` boş, `EDGE_HTTP_PORT=80`, `EDGE_HTTPS_PORT=443`).
- Çerezler: non-Development'ta `__Host-ssc.auth` (Secure, HttpOnly, Lax, Path=/, Domain yok, oturum çerezi) ve `__Host-ssc.csrf` (Strict). `__Host-` öneki Domain özniteliğini tarayıcı düzeyinde yasaklar.
- Güvenilir proxy zinciri **tam adreslerle**: edge → web (`WEB_TRUSTED_PROXIES`), web → api (`Security__TrustedProxies__*`). Güvenilmeyen eşten gelen `X-Forwarded-*` yok sayılır/ezilir; böylece IP başına hız sınırı sahte XFF ile bölünemez (canlı kanıt: `https-check.sh`, `resilience-check.sh`). Sabit IP'ler bu yüzden vardır; başka ağda eşin adresini yazın, joker KULLANMAYIN.
- **Data Protection anahtarları** `sscommerce-stack-dpkeys` volume'ünde (`/keys`). **Şifrelenmemiş XML**'dir (uygulama "No XML encryptor configured" uyarısı verir): volume'e yalnız servis kimliği erişmeli, disk/volume şifrelemesi önerilir, yedeği ayrı ve korumalı tutulmalı. Anahtarlar kaybolursa tüm oturumlar düşer (veri kaybı yok). Başka bir ortam aynı anahtar halkasını paylaşmamalıdır (canlı kanıt: boş halkalı ikinci örnek çerezi çözemedi).
- Güvenlik başlıkları: HTML için CSP (nonce + hash; `script-src`'de `unsafe-inline/eval` yok; `style-src-attr 'unsafe-inline'` bilinen Low), API için `default-src 'none'`. `WEB_CSP_MODE=report-only` yalnız hata ayıklama içindir.

## 7. Sağlık, günlük ve metrik

- API: `/health/live`, `/health/ready` (PostgreSQL + bekleyen migration yok). Web: `/healthz`, `/readyz` (API hazır mı). Dışarıya **kapalı** (edge 404); konteyner sağlık yoklamaları iç adresten yapılır.
- Günlük: stdout JSON (API ve web), çerez/CSRF/gövde/sorgu dizesi/IP yok; `X-Request-Id` edge'den API'ye taşınır.
- Metrik: yalnız API iç portu 9100, Prometheus metin biçimi; ana portta 404. Bkz. `docs/OPERATIONS.md`.

## 8. Sürümleme, sürüm geri alma, tedarik zinciri

- Sürüm: SemVer etiketi + commit kısa SHA (`SSC_VERSION`, `SSC_GIT_SHA`). Etiket (tag) ve değişiklik günlüğü `docs/RELEASE_NOTES.md`; **etiket/PR/main birleştirmesi sahip onayıyla** (Faz 9 bunları yapmaz).
- Hızlı geri alma (uygulama): önceki imaj etiketine dönüp `up -d api web`; **şema geri alınmaz** → önce yedek (OPERATIONS §5).
- Temel imaj güncelleme: `docker pull` → yeni digest'i `deploy/Dockerfile.*` ve `compose.yml`'de değiştir → yeniden derle → tüm kapıları koş.
- Tedarik zinciri notları: temel imajlar digest ile sabit; GitHub Actions adımları `@v4` etiketli (SHA sabitlemesi önerilir); `npm ci`/`dotnet restore` kilit dosyalarıyla; sır taraması (örn. gitleaks) ve `npm audit`/`dotnet list package --vulnerable` CI'ye eklenebilir (henüz yok). Web imajı 987 MB (üretim `node_modules`); küçültme ayrı iştir.

## 9. Gerçek barındırmaya geçiş (sahip eylemi; YAPILMADI)

Şunlar **`PENDING_OWNER_ACTION`**: barındırma sağlayıcısı/sunucu seçimi (Docker çalıştırabilen Linux), alan adı ve DNS A/AAAA kaydı, 80/443 erişimi, sırların güvenli üretimi (`gen-env.sh`) ve saklanması, Data Protection ve PostgreSQL volume şifrelemesi/yedek hedefi, `SSC_PUBLIC_ORIGIN=https://<alan adı>`, `EDGE_SITE`, gerçek `tls internal` kaldırma, `HSTS_MAX_AGE=31536000`, `WEB_TRUSTED_PROXIES`/`Security__TrustedProxies__*` değerlerinin barındırma ağına göre kontrolü. **Herkese açık sipariş**: D-061 (ödenmemiş sipariş stoğu tutar, otomatik süre dolumu yok), D-071 (Warehouse teslimat verisi), D-072 (üretim sipariş eşikleri) ve ödeme yokluğu için yazılı iş-riski kararı olmadan `ORDERING_ENABLED=true` YAPILMAZ. Aşamalar: (1) kısıtlı erişimli staging (IP kısıtı/temel kimlik doğrulama) + `STAGING_PASS` denetimi, (2) sahip imzasıyla herkese açık yayın. Herkese açık CA sertifikası, internet istemcileri ve uzak sunucu sağlığı **o ana makinede** doğrulanmadan `STAGING_PASS` denmez.

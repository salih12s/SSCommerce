# Faz 9 güvenlik incelemesi

Kapsam: Faz 9'da eklenen/değişen yüzey (dağıtım, kenar, API ve web giriş katmanı, veritabanı rolleri, betikler). **Durum etiketi: `LOCAL_PASS` (yerel üretim-eşdeğeri yığın).** Herkese açık barındırma, herkese açık CA sertifikası ve internet istemcileriyle doğrulama **yapılmadı** (`BLOCKED_EXTERNAL`). Bağımsız denetçi: `security-auditor` (iki tur, **statik**; kod okur, test koşturmaz). Çalışma zamanı kanıtı lead'in betiklerinden gelir (`deploy/scripts/*`, `apps/web/tests/prod/*`). Tur 1: PASS_WITH_FINDINGS (3 Medium). Tur 2 (düzeltmelerden sonra): **PASS_WITH_FINDINGS, açık Critical/High/Medium yok.**

## 1. Tehdit yüzeyi

| Yüzey | Güven sınırı | Ana kontroller |
|---|---|---|
| İnternet → edge (Caddy) | TLS, tek yayımlanan giriş | HTTP→HTTPS 308, HSTS, gövde ≤1 MB, `X-Request-Id` kenarca üretilir, iç yollar 404, günlük filtresi (çerez/başlık/sorgu yok) |
| edge → web (Next `server.mjs`) | Yalnız `172.28.77.10` güvenilir eş | XFF/XFP temizliği (`forwarding.mjs`), CSP (nonce+hash), nosniff/XFO/Referrer/Permissions/COOP, `Cache-Control: private, no-store` kişisel yollarda |
| web → API | Yalnız `172.28.77.11` güvenilir eş; `AllowedHosts` | Origin + CSRF filtreleri, `__Host-` çerezler, rate limit, Identity yetkilendirme (Faz 8 aynen) |
| API → PostgreSQL | `ssc_app`, internal ağ | DDL yok, tablo sahibi değil, yalnız-ekleme tablolarında UPDATE/DELETE yok, TCP loopback `scram-sha-256` |
| Operatör | Docker erişimi (kök eşdeğeri) | `gen-env.sh` izinleri, `api-command.sh` yalnız 2 değişken, yedek rolü salt okunur |

## 2. Kanıt (çalışma zamanı, bu makinede)

- **HTTPS ve zincir:** `deploy/scripts/https-check.sh` → **69 geçti / 0 kaldı** (son koşu). Yerel CA ile `ssl_verify_result=0`; CA'sız bağlantı reddedilir. **Bu, herkese açık CA doğrulaması DEĞİLDİR.**
- **Çerezler (curl + gerçek Chromium):** `__Host-ssc.auth` Secure, HttpOnly, Path=/, Domain yok, SameSite=Lax, oturum çerezi; `__Host-ssc.csrf` Secure, HttpOnly, Path=/, Strict. `document.cookie` bu çerezleri görmez. Çıkış sonrası eski çerez 401 (`apps/web/tests/prod/01-cookies.spec.ts`).
- **CSRF/Origin:** CSRF başlığı yok → reddedilir; yabancı Origin → 403; sahte jeton → reddedilir; doğru çift → 201.
- **Proxy güveni:** farklı sahte `X-Forwarded-For` ile 14 giriş denemesi rate limit'i atlatamaz (429 + `Retry-After`); güvenilmeyen eşten (iç ağdaki yanlış IP) gelen sahte XFF yok sayılır ve o eşin kovası bağımsızdır (`resilience-check.sh` §5). Çok-hop zincir davranışı `apps/web/tests/server/*.test.mjs` birim testlerindedir (canlı çok-proxy zinciri kurulmadı).
- **Data Protection:** api/web restart, `--force-recreate` ve DB restart sonrası aynı oturum çerezi geçerli; boş anahtar halkalı ikinci örnek aynı çerezi çözemez (401) (`resilience-check.sh` §1–4).
- **İç uçlar:** `/api/backend/health*`, `/metrics`, `/openapi*` ve **14 yol varyantı** (`\`, `%5c`, `%68`, `..`, `//`, büyük/küçük harf, `;`, `%2f`) kenarda 200 vermez ve içerik sızdırmaz; metrik yalnız iç portta (`ana port /metrics → 404`).
- **Yetki ve D-071:** Depo hesabı sipariş detayında alıcı adı/telefon/adres görmez (sayfa, DOM ve tüm API yanıtlarında anahtar taraması); Depo iptali 403; yönetim rotaları 403/yönlendirme (`04-roles.spec.ts`). Faz 8 yetkilendirme/kimlik dosyalarında `git diff HEAD` boş.
- **Veritabanı yetkisi (15 sorgu):** `ssc_app` DDL/TRUNCATE/DISABLE TRIGGER/DROP/CREATE yapamaz; `admin_audit_log`, `stock_movements` UPDATE/DELETE ve `orders` DELETE reddedilir; göç geçmişine yazamaz. `ssc_backup` yazamaz.
- **Telemetri sızıntısı:** metrik çıktısında e-posta/çerez/parola yok (tarama 0); günlük testleri parola/çerez/CSRF sızdırmadığını gösterir.

## 3. Bulgular ve kararlar

| No | Ciddiyet | Bulgu | Karar |
|---|---|---|---|
| B1 | **Medium** | Rate limit kovası tam IP: IPv6 /64 içinde adres döndürerek parola deneme sınırı aşılabilirdi | **Düzeltildi** (`ApiSecurityExtensions.RateLimitKey`: IPv4-mapped→IPv4, IPv6→/64; 6 birim test). Kalan Info: /48–/56 sahibi /64'ler arası geçebilir; NAT64 istemcileri kova paylaşır |
| B2 | **Medium** | `Ordering:Enabled` kod varsayılanı `true` (fail-open); korumayı yalnız compose varsayılanı sağlıyordu | **Düzeltildi** (Production/Staging'de açık `true`/`false` zorunlu, yoksa API açılmaz; 7 test) |
| B3 | **Medium** | Resmi postgres imajı TCP loopback için `trust`: DB ağ ad alanına katılan süreç parolasız süper kullanıcı | **Gerçek yığında doğrulandı** (parolasız `select 1` başarılıydı), **düzeltildi** (`99-harden-hba.sh` + mevcut kümeye uygulandı; parolasız bağlantı `fe_sendauth` ile reddedilir). `api-command.sh` yalnız `POSTGRES_DB` ve `SSC_APP_PASSWORD` geçirir. Yerel Unix soketi `trust` kalır (yalnız konteyner içi) |
| B4 | Low | Caddy yol eşleşmesi `\`/kodlu varyantlarla atlatılabilir mi? | **İstismar edilemedi** (14 varyant 404, içerik sızmadı); kalıcı regresyon `https-check.sh` §2b. Not: ilk sürümde betikte ham `\` varyantı bozulmuştu (denetçi yakaladı), düzeltildi ve doğrudan doğrulandı |
| B5 | Low | Parolalar konteyner env'inde (`docker inspect`); DP anahtarları şifresiz XML; yedek şifresiz | **Kabul edildi** (Docker erişimi zaten kök eşdeğeri). İyileştirme: Docker secrets / Npgsql `Passfile`, volume şifrelemesi (sahip eylemi) |
| B6 | Low | `restore-verify.sh` ad parametresi `sh -c` içine giriyordu | **Düzeltildi** (`[A-Za-z0-9_-]` doğrulaması) |
| B7 | Low | `compose.perf.yml` kazara birleştirilirse hız sınırı fiilen kalkar | **Kabul edildi + belgelendi** (`OPERATIONS.md` §11; üretim komutları yalnız `-f deploy/compose.yml`). Son yığın perf katmanı olmadan çalışıyor (api env'inde `RateLimit` yok) |
| B8 | Low | `Cache-Control` Next ezerse kaybolabilir | **Sorun yok** (9 kişisel yolda canlı `no-store`; regresyon §2c) |
| B9 | Info | `style-src-attr 'unsafe-inline'` | **Kabul edildi** (betik çalıştırmaz; D-081) |
| B10 | Info | `SSC_PUBLIC_ORIGIN` varsayılanı, `stack-fixtures.sh` `|| true` | (d) **Düzeltildi**; diğerleri bilgi |
| — | Info | GitHub Actions `@v4` etiket (SHA değil), sabit test parolaları | Kabul; SHA sabitleme önerilir (`DEPLOYMENT.md` §8) |

## 4. Kabul edilen artık riskler ve kapsanmayanlar

- **`NOT_VERIFIED`:** herkese açık CA ile TLS, gerçek internet istemcileri, gerçek ters proxy zinciri (çok-hop yalnız birim test), uzun süreli kesinti/soak, gerçek ağ gecikmesi altında yük, SIGTERM'in Linux'ta gerçek süreç testi (Windows'ta atlanır; mantık birim testli), Next'in ham `\` normalizasyonu kodla değil canlı yanıtla kanıtlandı.
- **Statik denetim sınırı:** denetçi test koşturmadı; çalışma zamanı iddiaları lead'in betik çıktılarına dayanır. Denetçinin `git`/Bash aracı yoktu: Faz 8 yetkilendirme dosyalarının değişmediği `git diff HEAD` ile **lead** tarafından doğrulandı (boş çıktı).
- **Açık sahip kararları (güvenlik değil iş riski):** herkese açık sipariş (D-061/D-071/D-072); varsayılan `ORDERING_ENABLED=false`. Hiçbiri sessizce onaylanmadı.

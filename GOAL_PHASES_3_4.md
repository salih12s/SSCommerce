# SSCommerce — Autonomous Build Goal: Faz 3 (Catalog) + Faz 4 (Identity & Security)

**Belge:** `GOAL_PHASES_3_4.md` · **Sürüm:** 1.0 · **Tarih:** 2026-10-28  
**Repo:** `https://github.com/salih12s/SSCommerce.git` · **Ortam:** Windows, VS Code, Claude Code  
**Hedef:** Faz 3 ve Faz 4'ü küçük, doğrulanabilir görevlerle bitir; görevler arasında yeni prompt isteme. **Faz 5'e geçme.**  
**Ana ajan:** Opus 5.5 / high · **Alt ajanlar:** Sonnet 5.5 / high (mevcut `.claude/agents` tanımları).

> Bu belge **hedef ve kabul sözleşmesidir**, bitmiş kod değildir. Daha önceki `GOAL_PHASES_0_2.md`, `CLAUDE.md`, `docs/DECISIONS.md`, `docs/ARCHITECTURE.md`, `docs/ROADMAP.md` ve `docs/PROGRESS.md` ile birlikte çalışır. Çelişkide mevcut çalışan kodu incele, güvenlikten taviz verme, kararı belgele ve gerekirse `BLOCKED` raporla.

## 0. İLK KAPI — Mevcut çalışmayı KORU

İlk işlem **sadece okumak ve raporlamak**:

1. `pwd`, `git status --short`, `git branch --show-current`, `git remote -v`, `git log -3 --oneline` kontrol et. Repo `salih12s/SSCommerce` olmalı. 2026-10-28'de uzaktaki son teyitli commit `9c562f7` idi; yerelde daha yeni commit bulunabilir, eski durumu gerçek sayma.
2. `CLAUDE.md`, `.claude/agents/*`, `.claude/settings.json`, `docs/{ROADMAP,ARCHITECTURE,DECISIONS,PROGRESS}.md`, `backend/`, `apps/web/`, `docker-compose.yml`, `.github/workflows/ci.yml` dosyalarını **ilgili kısımlarıyla** incele.
3. Özellikle Faz 2'nin **henüz commit edilmemiş görsel yenilemeleri** olabilir. **Hiçbir değişikliği silme, üzerine yazma, geri alma, stash etme veya commit etme.** `git reset --hard`, `git clean`, force push, otomatik rebase yasak. Dirty worktree varsa dosya listesini kaydet; öncelikle çakışmayan backend görevlerinde ilerle. Aynı frontend dosyasına dokunmak gerektiğinde mevcut değişiklikleri koruyacak kullanıcı onaylı snapshot/branch kararı al; izin verilmezse ilgili frontend alt görevi `PENDING_APPROVAL` bırak. Bu durum fazın tamamını sahte `PASS` yapmaz.
4. Mevcut `CLAUDE.md` dosyasının hedef referansı yalnızca 0–2 ise, **ajan kurallarına dokunmadan**, yeni aktif hedefi `GOAL_PHASES_3_4.md` olarak ekle/güncelle. Eski GOAL belgesini sakla. Dizin sahipliğini değiştirme.
5. Docker PostgreSQL volume'u mevcut olabilir (`sscommerce-pgdata`). **Volume silmek, `docker compose down -v`, mevcut veriyi sıfırlamak kesinlikle yasak.** Kullanıcı `.env` dosyası yasak/korumalı; izni aşarak okumaya çalışma. Veritabanına bağlantı gerekiyorsa mevcut güvenli ortam değişkeni/bağlantı yöntemini kullan; yoksa sır istemeden nasıl yerelde sağlanacağını anlat.
6. Faz 2 görsel onayı henüz kesin değilse bunu not et. Yeni katalog entegrasyonunda onaylı **kömür + sıcak krem + turuncu** tasarım token'larını ve varsa son yerel görsel iyileştirmeleri koru; hero, fotoğraflar ve tasarım kimliğini yeniden tasarlama.

## 1. Genel kurallar ve başarı tanımı

- **Sırayla** F3.0 → F3.1 → F3.2 → F3.3 → F3.4 → F3.5 → F4.0 → F4.1 → F4.2 → F4.3 → F4.4 → F4.5; alt görevleri bağımlılıklarına göre kendi içinde parçalara ayır. Her görev bitince sıradakine geç; yalnızca gerçek blocker/izin kararında dur.
- Kod yalnızca mevcut **ASP.NET Core 10 + EF Core 10 + PostgreSQL 17 + Next.js App Router + TypeScript + Tailwind** yapısında ilerlesin. Repo ve marka adı **SSCommerce**. Tek Next.js uygulaması ve mevcut same-origin `/api/backend/*` rewrite yaklaşımı korunsun. İş mantığı backend'de.
- Modüler monolit, sade Clean Architecture sınırları: `Domain` saf; `Application` kullanım senaryoları/kontratlar; `Infrastructure` EF Core ve Identity; `Api` HTTP, auth middleware ve DI. Gereksiz CQRS, MediatR, generic repository, microservice, Redis, queue ekleme.
- **Üç durum birbirinden ayrıdır:** `LOCAL_PASS`, `GIT_PUSHED`, `REMOTE_CI_PASS`. Yalnızca yerel test geçti diye uzak CI yeşil denmez. Önceki GitHub Actions çalışmaları faturalandırma blokajı nedeniyle başarısızdı; güncel durumu kontrol et, sebebi tahmin etme.
- Her alt görev için kapsam, değişen dosyalar, gerçek komut + exit code, test sayısı, risk/engeller ve bir sonraki adım `docs/PROGRESS.md` içinde kayıtlı olsun. `docs/ROADMAP.md`, `docs/ARCHITECTURE.md`, `docs/DECISIONS.md`, `README.md` gerektiği ölçüde güncellensin. Belge şişirme ve tekrar yok.
- **Eğitim hedefi:** Her kilometre taşından sonra en fazla 6–10 cümle Türkçe açıkla: `request → API → application service → EF → PostgreSQL → response`, Identity cookie ve CSRF konularında da gerçek kod/dosya referansları ver. Kullanıcı C# öğreniyor; kararların 'neden'ini kısa açıkla.
- Opus görev parçalayıp inceler. Uygulama ağırlığını Sonnet uzmanlarına ver: `database-dev` (schema/migration), `backend-dev` (endpoint/Identity), `frontend-dev` (web), `qa-reviewer` (bağımsız inceleme). **En az sayıda ajan**, çoğunlukla 1; en fazla 2 eşzamanlı ve aynı dosyada paralel yazma yok. Kullanıcıya binlerce satır log değil kısa kanıt sun.
- Testi çalıştırmadıysan `NOT_VERIFIED`, başarısızsa `FAIL`, izin gerekiyorsa `PENDING_APPROVAL`, altyapı engeliyse `BLOCKED_EXTERNAL`. Bunların hiçbiri `PASS` değildir. Hata varsa planı güncelle ve gerçek nedeni çöz; testi devre dışı bırakıp yeşil yapma.
- **Git commit/push kullanıcı onayı gerektirir.** Yeni bir hedef başlatılmış olması Git yetkisi veya yıkıcı değişiklik onayı değildir. İncelemeden sonra önerilen commit/branch ve dosya özetini sun. `main` üzerine izinsiz push yapma.

## 2. Kapsam SINIRLARI

**Bu hedefte VAR:** Public catalog, gerçek EF migration, geliştime/test demo seed verisi, arama/filtreleme/sıralama/sayfalama, gerçek API'ye bağlanan liste/detay, Identity kullanıcıları, browser cookie oturumu, CSRF, müşteri/admin/depo rol temeli, kullanıcıya özel `/hesabim` temel ekranı, kapsamlı authorization/integration/E2E testleri.

**Bu hedefte YOK:** Depo/InventoryBalance ve stok rezervasyonu (Faz 5), sepet ve adres/checkout (Faz 6), sipariş/idempotency (Faz 7), **gerçek ödeme, gerçek kredi kartı alma, ödeme formu** (MVP dışı), tam ERP yönetim ekranı ve admin ürün CRUD (Faz 8), B2B, prod deployment, otomatik kullanıcılara mail yollama, ödeme webhooks. Bu özellikleri erken ekleme.

**Güvenlik-katalog geçiş kuralı:** Faz 3 boyunca yalnızca herkese açık **okuma** endpoint'leri aktif edilir; anonim `POST/PATCH/DELETE` katalog endpoint'i yayınlanmaz. Faz 4'te gerçek policy'ler tanımlanır; kontrol amaçlı admin-only endpoint açılabilir, ama admin CRUD Faz 8'e saklanır. Geliştirme seed'i HTTP public yazma endpoint'i olarak açılmaz.

## 3. SABİT TEKNİK SÖZLEŞMELER

- Fiyat Domain: C# `decimal`, PostgreSQL `numeric(18,2)`, API: kuruş cinsinden **`long ...Minor`**. TRY. TypeScript'te `number` tam sayı ve safe integer kontrolü; UI yalnızca formatlar, fiyat uydurmaz.
- Kalıcı saat UTC / `timestamptz`; JSON'da ISO-8601 UTC.
- Public web SSR/SEO; gereksiz Next.js BFF/Route Handler üzerinden ikinci iş mantığı yazma; auth/proxy/sunucu component ayrımı doğru tasarlansın.
- Hata modeli RFC 9457 `ProblemDetails`, sabit makine okunur `code` alanıyla ve güvenli `traceId` ile; asla stack trace, connection string ya da kişisel sır çıkarmadan.
- OpenAPI endpoint tanımları doğru; uygun araç kuruluyken TypeScript tiplerini OpenAPI'den türetmeyi değerlendir; el yazısı farklı DTO'lar ve API şeması arasında drift oluşturma. Aşırı araç kurmadan aynı tutarlılığı test et.
- Public ürün listesi `GET /api/v1/products?category=...&q=...&minPriceMinor=...&maxPriceMinor=...&sort=price_asc&page=1&pageSize=20`; varsayılan `page=1`, `pageSize=20`, üst sınır `50`, sorgular parametrik. Desteklenen sort anahtarlarını explicit allowlist ile kısıtla, deterministik ikincil sort uygula.
- `GET /api/v1/categories` ve `GET /api/v1/products/{slug}` endpoint'leri de sun. Public cevap yalnızca aktif/yayında ürünleri ve varyasyonları içerir, admin gizli alanları sızmaz. Gerçek stok henüz yoksa sahte `inStock` veya satışa hazır durumu üretme.
- Beklenen filtre veya SKU bulunamadığında 404/boş liste davranışları belirli, tutarlı ve testli olsun; `pageSize=-1`, aşırı sayfa boyutu, bilinmeyen sort ve SQL injection girdileri doğrulansın.

## 4. FAZ 3 — CATALOG

### F3.0 — Keşif, plan ve veri sözleşmesi

- Mevcut `SSCommerceDbContext`, Infrastructure DI, API `Program.cs`, domain katmanları ve Next.js demo fixture'larını incele. Eski kodu körlemesine kopyalama.
- Tablo alanlarını, URL/slug stratejisini, varyasyon formatını, fiyat ve katalog API DTO'larını kısa mimari kararda kaydet. `apps/web` yerel görsel düzenlemelerine dokunma koşulunu doğrula.
- `ProductVariant` bir **satılabilir SKU**'dur; genel `Product` ile karıştırma. `Stock` hâlâ Faz 5 kapsamıdır; katalog 'ürün satılabilir mi?' diye stok varmış gibi yalan söylemez.

### F3.1 — PostgreSQL şeması ve ilk EF Core migration

**Asgari tablolar** (Identity tabloları Faz 4'te gelecek):

1. `categories`: `id uuid`, `name`, `slug`, `parent_id?` self-FK, `sort_order`, `is_active`, UTC created/updated.
2. `products`: `id uuid`, `category_id uuid`, `name`, `slug`, `brand?`, `description?`, `specifications jsonb?`, `is_active`, UTC created/updated.
3. `product_variants`: `id uuid`, `product_id uuid`, `sku`, `display_name`, `options jsonb`, `option_signature`, `price numeric(18,2)`, `currency char(3)` (MVP TRY), `is_active`, UTC created/updated.
4. `product_images`: `id uuid`, `product_id uuid`, `storage_key` veya doğrulanmış yerel/public image path, `alt_text`, `sort_order`, `is_primary`, kaynak/credit bağı değerlendir.

**DB kuralları:** PK/FK; category slug, product slug, SKU unique; `(product_id, option_signature)` unique; ürün başına en fazla bir primary görsel (partial unique index); price >= 0; no invalid quantity (henüz yok); `category.parent_id != category.id` (en azından direkt self-reference engelle); uygun delete restrict/no-action (geçmişi silme), `is_active` ile vitrinden kaldırma. Listeleme için category + is_active ve ürün-varyasyon FK index'leri. Gereksiz index yok. `specifications/options jsonb` alanlarına boyut ve doğrulama sınırları koy. Alanlar ve navigasyonlar EF Fluent API ile tasarlansın.

- Mevcut `SSCommerceDbContext` ile uyumlu **ilk gerçek migration** üret; migration snapshot ve SQL'i incele; `EnsureCreated` veya `EnsureDeleted` ile uygulama veritabanını yönetme.
- **Mevcut `sscommerce-pgdata` volume'unu koru**. Üretim DB'si varsayma; yerelde migrate yalnızca şema değişikliği gözden geçirilince. Destructive DROP/renaming veriyı kaybettiriyorsa açık onay istemeden uygulama.
- Migration temiz ve mevcut kurulu yerel PostgreSQL'de tekrar uygulanabilir/idempotent olmalı; ayrı test DB'sinde sıfırdan migration test et. Test için asıl `sscommerce` veritabanını sıfırlama.

### F3.2 — Public read API ve application service

- API endpoint'leri Application katmanındaki kullanım senaryolarına dayanmalı: kategori listesi, arama/filtre/sıralama/sayfalama, ürün detayı + varyasyonlar + görseller.
- DB sorguları `AsNoTracking`, projection, makul sayfalama; `N+1` sorgudan kaçın. Search için başlangıçta PostgreSQL ile güvenli case-insensitive metin araması yeterli; Elastic/Meilisearch ekleme.
- List DTO: `id`, `slug`, `name`, `category`, `primaryImage`, `startingPriceMinor`, `currency`, `isDemo`/uygun geçici satış etiketi, pagination metadata; detail DTO: güvenli alanlar, her varyasyonun `sku`, `options`, `priceMinor`, görseller. Sözleşmeyi OpenAPI'da doğrula.
- Slug biliniyorsa 200; yoksa 404. `is_active=false` ürünler public API'ye sızmasın. Düzenli hata cevapları ve input validation.
- **Gerekmedikçe CRUD yazma endpoint'i yok; auth gelmeden tüm yazma endpoint'leri kapalı.**

### F3.3 — Güvenli geliştirme seed'i

- Yalnızca `Development`/test ortamı için **idempotent**, tekrarlı çalıştırıldığında çoğalmayan 4 kategorilik, birden fazla SKU ve görselli **örnek katalog** üret. Ürünler `demo/test` olarak açıkça işaretlensin; gerçek marka/model/indirim/stok/garanti vaadi uydurma.
- Mevcut lisanslı fotoğrafların kaynak kayıtlarını ve kredi dosyasını koru. Yeni dış görsel eklemen gerekirse lisansı ve kullanım uygunluğu kanıtlanmadan ekleme. Ürün fotoğrafı ile ürün adı yanlış eşleşmesin; gerektiğinde özgün temsilî çizim.
- Seed prod ortamında otomatik çalışmasın; açık yerel komut/flag ile çalışsın. Seed rol ve admin parolası oluşturmasın (Faz 4'e ait).

### F3.4 — Next.js mağaza entegrasyonu

- Mevcut onaylı/kullanıcının son yerel tasarımını koruyarak `/kategori/[slug]`, `/arama?q=...`, ürün detay `/urun/[slug]` ve ana sayfadaki ürün alanını **gerçek Catalog API** ile besle. Filtre/sort/page query param URL'ye yansısın, yenileyince korunsun; boş, yükleniyor, hata ve API erişilemiyor durumları düzgün olsun.
- İstekler mevcut same-origin `/api/backend/*` proxy yaklaşımına uysun; Next.js 16 `cacheComponents` davranışı dikkate alınsın. Dinamik ürün verilerinin stale olmasını engelle. Fiyatlar backend yanıtından gelir; UI yalnızca formatlar.
- SEO: anlamlı `<title>`, description ve canonical stratejisi; olmayan slug gerçek 404; iç linkler ve semantik başlıklar. Desteksiz URL `href="#"` yerleştirme.
- Katalog henüz checkout yapmıyor: sepete ekle, satın al, kartla ödeme gibi çalışan eylemleri taklit etme. Demo/satış yakında bilgisi korunacak. Geçerli görsel düzeni, mobil alt menü ve erişilebilirlik testleri bozulmayacak.
- Frontend çalışma ağacında Faz 2'nin commit edilmemiş dosyaları varsa **önce F3.0 koruma kuralı** uygulanmalı. Üzerine yazma!

### F3.5 — Faz 3 kabul kapısı (tamamı gerekli)

- `dotnet build backend/SSCommerce.slnx -c Release` exit 0.
- **Gerçek PostgreSQL'de** ilk migration oluşturuldu, SQL incelemesi ve ayrı test DB'de apply doğrulaması var. `__EFMigrationsHistory` ve dört tablo gerçekten gözlemlendi; DB `CHECK/UNIQUE/FK` hatalı kayıtları reddediyor (otomatik test).
- `dotnet test backend/SSCommerce.slnx -c Release` exit 0 ve **DB testleri 0 skipped** (bağlantı ortamı doğru). Seed iki defa çalıştırıldığında satır sayısı çoğalmıyor.
- Public API testleri: liste, detay, category, 404, inactive, pagination, sort, filtre, invalid input, SKU unique, ürün fiyatı kuruş dönüşümü, veri sınırı.
- `npm run lint`, `npm run typecheck`, `npm run build`, `npm run e2e` (Playwright build sonrası) başarılı; list/detail/search navigasyonları 1440/768/390'da kontrol edildi. API kapalıyken sahte başarılı katalog göstermesin.
- `docs/PROGRESS.md` içinde F3.0–F3.5 kanıtları ve gerçek test sayıları. F3 kabul kapısı PASS değilse F4'e başlamış gibi raporlama; gerekliyse engelleyen sorunu çöz.

## 5. FAZ 4 — IDENTITY / SECURITY

### F4.0 — Güvenlik tasarımı ve test matrisi

- ASP.NET Core 10 **Identity** kullan; kendi şifre saklama veya JWT altyapını yazma. Identity `Guid` kullanıcı/rol kimlikleri önerilir; mevcut Catalog DbContext ve migrations ile tek PostgreSQL şemasında uyumlu çözümü tasarla (gerekirse mevcut DbContext `IdentityDbContext<...>` türetebilir). Bağımsız gereksiz auth servisi yok.
- Model: `Customer`, `Warehouse`, `Admin`. Tek hesap kendi rolünü asla belirleyemez. Kullanıcı kayıtları varsayılan `Customer`; admin/depo rolünü yalnız güvenli, erişimi sınırlı kurulum mekanizması atayabilir.
- **Şifre sıfırlama/e-posta doğrulama** gerçek email sağlayıcısı yoksa yapılmış gibi gösterme. Faz 4 kabulde email servisinin olup olmadığını ve eksik güvenlik/ürün akışlarını net raporla; sahte 'doğrulandı' durumu oluşturma. Gerçek e-posta göndermeden `EmailConfirmed=true` yapma.
- Auth kararı: browser için **HttpOnly cookie, JWT'yi localStorage'da tutma**. Production HTTPS ve Secure; geliştirmede HTTP `localhost` sınırları ile test. `SameSite=Lax`/gereken sıkılık, uygun Path, CORS istemsiz açılmıyor, cookie süresi ve logout/session geçersizliği belgelensin.

### F4.1 — Identity persistence, migration ve güvenli rol temeli

- `AppUser` ve Identity rolleri; password hashing ASP.NET Identity tarafından. Unique normalized email, makul parola politikası, login başarısızlığı/lockout, giriş rate limit.
- Catalog migration'ına zarar vermeyen **ayrı Identity migration'ı** oluştur; SQL'i incele, temiz ayrı test DB'de sırayla migration'ları uygula. Kayıtlı ürünler kaybolmamalı.
- Customer/ Warehouse / Admin rol tanımları idempotent initializer ile **yalnız rol adları** için oluşturulabilir. **Sabit admin e-posta/parola koyma; herkese açık endpoint ile rol yükseltme yok.** İlk admin gerekiyorsa açık kullanıcı onayına bağlı ve local secret/env kullanan güvenli bootstrap CLI yaklaşımını belgele; bu hedefte varsayılan privileged kullanıcı oluşturma.

### F4.2 — Auth API, cookie, antiforgery ve policy

- Aşağıdaki sözleşmeleri gerekirse küçük gerekçeli değişiklikle uygula:
  - `GET /api/v1/auth/csrf` → JS'nin kullanacağı antiforgery token'ı güvenli, cache'siz, same-origin olarak sağlar.
  - `POST /api/v1/auth/register` → kullanıcı kayıt (rol parametresi **kabul etmez**); giriş akışı ve doğrulama politikası belgelensin.
  - `POST /api/v1/auth/login` → cookie oturumu; dönüşte şifre/token sızdırma yok.
  - `POST /api/v1/auth/logout` → auth cookie'sini geçersizleştir/sil. Cookie replay'in sunucu tarafında da engellendiği iddia ediliyorsa gerçek server-side ticket revocation/security-stamp davranışını ve testini göster; varsayılan cookie sign-out tek başına ele geçirilmiş eski ticket'ın sunucuda anında iptal edildiğini kanıtlamaz.
  - `GET /api/v1/auth/me` → mevcut kullanıcı ve güvenli role/capability özeti; anonim için kesin 401, cache yok.
  - Role-gated, **yan etkisiz** `GET /api/v1/admin/me` veya mevcut endpoint policy testi (Admin gerekli); admin CRUD kapsam dışı.
- **CSRF tüm cookie-auth mutating endpoint'lerde**, özellikle login/register/logout için de korunsun. .NET antiforgery servisinin yalnız kaydedilmiş olması yeterli değil: istek token'ının doğrulandığı gerçek entegrasyon testleri istenir. SameSite tek başına güvenlik kanıtı değildir. `Origin`/`Referer` kontrolleri defense-in-depth olarak düşünülebilir, CSRF yerine geçmez. Antiforgery tokenı user secret veya auth cookie yerine geçmez.
- `401` = unauthenticated, `403` = authenticated but unauthorized; API için hiçbirinde HTML login redirect/302 döndürme. ASP.NET Core 10 cookie davranışının endpoint metadata'sına bağlı olabileceğini bil; özellikle test et.
- Basit, standart RFC 9457 hataları; field validation, email/password hatalarında hesap varlığı ifşa etmemek için tutarlı yanıt stratejisi. Şifre/token/Set-Cookie/PII güvenli loglaması.
- GET uçları durum değiştirmez. CSRF token bootstrap endpoint'i cache'siz ve güvenli; Cookie proxy'sinin `Set-Cookie`, request Cookie ve CSRF header'ını gerçek tarayıcıda doğru taşıdığı doğrulansın.
- **Bu fazda adrese, sepete, siparişe, ödeme veya stok sistemine geçme.**

### F4.3 — Next.js kullanıcı arayüzü

- Mevcut header'daki `Hesap Yakında` öğesini gerçek giriş/kayıt/çıkış akışına bağla. Yol örnekleri: `/giris`, `/kayit`, `/hesabim`. Kendi sunucusunda çalışan login HTML, parola alanı, erişilebilir hata mesajları; gerçek backend API ile giriş.
- Kişisel sayfalar asla statik cache veya farklı kullanıcılara paylaşılan veriyle sunulmasın. Oturum yoksa login'e doğru ve güvenli UX; oturum varsa kullanıcıya özel içerik. **API tarafında authorization zorunlu**, Next.js yönlendirmesi yalnız UX katmanı.
- Mobil ve masaüstünde mevcut SSCommerce tasarımı korunur. `Sepet` butonu halen pasif/yakında; yanlış alışveriş özelliği açılmaz. Basit yönetici UI taslağı oluşturmak zorunda değilsin; `/admin` işlevsel ERP Faz 8.
- CSRF header'ı browser requests'te doğru taşınır; cookie JS tarafından okunamaz, sırlar Client Component props veya localStorage içine yazılmaz.

### F4.4 — Güvenlik testleri (bağımsız QA zorunlu)

- Backend gerçek PostgreSQL integration tests: kayıt, unique email, parola hashing, login, yanlış parola, lockout/rate limit, logout sonrası oturum reddi, cookie özellikleri, `/me`, 401/403 policy, role escalation denemesi, farklı kullanıcıyla oturum, CSRF token yok/yanlış/doğru, cross-origin isteği ve public katalog anonim erişimi.
- İki farklı müşteri oturumunun verileri karışmamalı; ileride IDOR için güvenli temel oluştur. Gereksiz kişisel veri/log sızma testleri. Veritabanındaki `AspNetUsers.PasswordHash` plaintext olmamalı. Admin'in rolü public register/login body üzerinden atanamamalı.
- Güvenli test kullanıcıları ve parolalar yalnız test ortamında, **tamamen izole DB**; test çıktılarında gerçek sır yok. Testler mevcut yerel `sscommerce` veritabanını silmez.
- Playwright: kaydol, giriş, cookie ile session, sayfa yenileme, çıkış, yetkisiz admin, bozuk/eksik CSRF durumu, mobil form ve erişilebilirlik. E2E testleri güvenilir, rastgele çakışmayan izole test kullanıcılarıyla koşmalı. Gerçek email gönderimi yoksa hesap doğrulandı gibi gösterme.
- `qa-reviewer` salt okunur kod/security incelemesi yapar; bulgular severity ile listelenir, kritik/yüksek bulgular çözülmeden `PASS` olmaz. Write/Edit kapalı olsa bile shell'in yazabildiği unutulmaz; sadece doğrulama araçlarını kullan.

### F4.5 — Faz 4 kabul kapısı ve regresyon

- .NET build/test ve **izole gerçek PostgreSQL** testleri geçiyor; **0 skipped**. Hem Catalog hem Identity migration'ları test DB'de sıfırdan sırayla çalışıyor; mevcut data bozulmuyor.
- Public katalog giriş gerektirmiyor; auth endpoints rate-limit / CSRF / role guard doğru; unauth 401, wrong-role 403; cookie Secure-prod, HttpOnly, SameSite testleri.
- Next.js `lint`, `typecheck`, `build` ve tarayıcı E2E tüm testleri PASS. Login/logout session uçtan uca same-origin rewrite üzerinden kanıtlandı (sadece doğrudan API yeterli değil).
- Responsive: 390, 768 ve 1440px; mobil menü, auth formları, footer/BottomNav erişilebilir, yatay taşma yok. Gerçek fiziksel cihaz testi yapılmadıysa `NOT_VERIFIED`.
- Regresyon: `/health/live`, `/health/ready`, OpenAPI, kategori/ürün listeleme, filtre, detay, SEO 404; faz 2 vitrin görünümü korunmuş.
- Sonunda `docs/ROADMAP.md`, `docs/ARCHITECTURE.md`, `docs/DECISIONS.md`, `docs/PROGRESS.md`, README ve gerekiyorsa OpenAPI istemci sözleşmesi güncellenmiş.

## 6. ZORUNLU TEST / KANIT MATRİSİ

| Test | Kabul |
|---|---|
| Aynı SKU iki kez oluşturuluyor | `UNIQUE` engeller; gerçek PostgreSQL testi |
| Aynı product/variant signature iki kez | `UNIQUE` engeller |
| İki kez development seed çalışıyor | Satır çoğalmaz |
| Negatif ürün fiyatı | DB `CHECK` engeller |
| Pasif ürün / bilinmeyen slug | Public ürün detayı 404 |
| Fiyat biçimi | `priceMinor` doğru tam sayı, TRY |
| Sayfalama, filtre, arama, yanlış sort | Deterministik ve validation testli |
| Kimliksiz admin isteği | 401; HTML redirect değil |
| Customer ile Admin isteği | 403 |
| Register isteğinde `role=Admin` | Rol yükselmez |
| CSRF yok/yanlış/doğru | İlk ikisi reddedilir, geçerli istek işler |
| Yanlış parola / lockout | Politika çalışır; sır sızmaz |
| Logout sonrası `/auth/me` | 401 |
| İki farklı kullanıcı oturumu | Veriler karışmaz |
| Frontend proxy auth ve cookie | Gerçek browser'da çalışır |
| Mevcut public katalog | Anonim hâlâ erişir |
| UI 390/768/1440 | Taşma, kırık nav, major a11y ihlali yok |
| Migration temiz test DB | Apply + ikinci apply + Catalog→Identity zinciri PASS |
| Git güvenliği | `git diff --check`, ignore & secret scan temiz |

## 7. ÇALIŞTIRILACAK DOĞRULAMALAR (duruma uygun uyarlayabilirsin)

```powershell
# Repo kökünde
git status --short
git diff --check

dotnet build backend/SSCommerce.slnx -c Release
# SSCOMMERCE_TEST_POSTGRES: izole ve güvenli test DB'sine işaret etmeli.
dotnet test backend/SSCommerce.slnx -c Release

# Web klasöründe
cd apps/web
npm ci
npm run lint
npm run typecheck
npm run build
npm run e2e
```

- Geliştirme PostgreSQL için `docker compose config --quiet`, `docker compose ps` ve healthcheck; **silme/reset yapma**.
- Migration oluşturma/uygulama adımlarında `dotnet ef migrations add`, `dotnet ef migrations script`, `dotnet ef database update` uygun `--project`/`--startup-project` ile kullanılabilir. Üretim migration'ını otomatik uygulama; SQL'i incele.
- `dotnet test` stdout gerçek test sayısı ve skipped sayısını kaydet. Gerçek DB testleri atlanıyorsa `PASS` değildir. Playwright'da backend/DB bağımlılığı varsa servisi başlatmayı ve kapatmayı tutarlı otomatikleştir; testin mock'a sessizce dönmesini engelle.
- CI GitHub faturalandırma/hesap kilidini kod değiştirerek çözemezsin. Local PASS, Remote CI BLOCKED_EXTERNAL olarak ayrılır; GitHub Actions yeşil olmadıkça `REMOTE_CI_PASS` yazma.

## 8. DURMA, ONAY, RAPOR

- **Faz 3'ün teknik kriterleri geçtiyse Faz 4'e otomatik geç.** Arada yeni kullanıcı prompt'u isteme; ama mevcut kod veya görsel değişiklikler için izinsiz yıkıcı işlem yapma.
- Faz 4 bitince **dur**. Faz 5'e geçme. Eğer dış engel/izin eksikse güvenli bağımsız işleri tamamla, gerisini `BLOCKED_EXTERNAL`/`PENDING_APPROVAL` işaretle, kullanıcıya gereken **tek somut eylemi** söyle. Görevi tamammış gibi raporlama.
- Commit/push otomatik değil, **açık kullanıcı izni ve inceleme** gerekiyor. Fazlara göre önerilen feature branch ve commit mesajlarını sun. `.env`, credentials, test dump, generated artifacts, screenshot (Git dışı) ve çalıştırma sırlarını stage etme.
- Bitiş raporu **en fazla ~45 satır Türkçe**: Faz 3 ve Faz 4 status, git SHA/branch/dirty-state, değişen ana dosyalar, tablo/migration sayısı, endpoint listesi, test komutları+exit code+test sayıları, UI ekran görüntüleri, güvenlik bulguları, açık blocker'lar, Git/CI durumu, sonraki fazı başlatmama teyidi.
- Bir turda bitmezse her kritik kilometre taşını `docs/PROGRESS.md`'ye kaydederek sonraki turda kaldığı yerden devam et; dosyaları yeniden üretme veya tamamlanmış adımları sebepsiz tekrar test etme.

## 9. GÜVENİLİR TEKNİK KAYNAKLAR

- EF Core migrations: <https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying>
- ASP.NET Core 10 API cookie 401/403: <https://learn.microsoft.com/en-us/aspnet/core/security/authentication/api-endpoint-auth?view=aspnetcore-10.0>
- ASP.NET Core CSRF: <https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0>
- ASP.NET Core Identity: <https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0>
- Next.js App Router: <https://nextjs.org/docs/app>

**SON KOMUT:** Hedefi uygula, küçük görevler halinde otonom ilerle, kanıtları koru, kullanıcı izinlerini atlama, Faz 5'e geçme. Kaynak ve test kanıtı eksikse tur sınırına rağmen sahte COMPLETE üretme.

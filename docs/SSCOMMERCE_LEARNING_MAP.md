# SSCommerce öğrenme haritası

Hazırlayan: Faz 9; yapı değişirse yolları yeniden doğrula

Bu belge ders DEĞİL, derslerin kaynak-temelli indeksidir. Her modülde: amaç, gerçek dosya yolları, temel kavramlar, örnek mülakat soruları ve "önce şunu oku" sırası vardır. Satır numarası verilmez (kod değişince kayar); dosyada sınıf/fonksiyon adını arayın. Sayı iddiası (test sayısı vb.) bilerek yoktur; güncel sayılar için bkz. `docs/PROGRESS.md`.

## 0. Nasıl kullanılır + büyük resim

**Nasıl kullanılır.** Her ders için ilgili modülü aç, "Önce şunu oku" sırasını izle, kavramları kendi cümlenle anlat, sonra bölüm 17'deki küçük gözlem alıştırmasını yap. Kod değiştirme; yalnız oku ve test çalıştır. Takıldığında sırayla `docs/ARCHITECTURE.md` (mimari), `docs/DECISIONS.md` (neden böyle), `docs/PROGRESS.md` (ne zaman, hangi kanıtla) dosyalarına bak.

**Bir isteğin yolculuğu (ör. sepete ürün ekleme):**

```
Tarayıcı
  │  HTTPS, tek origin
  ▼
Caddy (deploy/Caddyfile)            TLS'i çözer, X-Request-Id üretir, iç uçları (health/metrics/openapi) 404'ler
  ▼
Next özel sunucu (apps/web/server.mjs + apps/web/server/*.mjs)   proxy başlıklarını temizler, CSP nonce, no-store, /healthz
  ▼
Next rewrite  /api/backend/*  (apps/web/next.config.ts)   → API_BASE_URL
  ▼
ASP.NET uç noktası (backend/src/SSCommerce.Api/**)        CSRF/Origin, rate limit, kimlik, yetki, ProblemDetails
  ▼
Application (kullanım senaryosu sözleşmeleri, doğrulayıcılar, hesaplayıcılar)
  ▼
Domain (kurallar, varlıklar; hiçbir şeye bağlı değil)
  ▼
EF Core / ham Npgsql (backend/src/SSCommerce.Infrastructure/**) → PostgreSQL (kısıtlar + trigger'lar son savunma)
```

Not: `src/proxy.ts` Next içinde ayrı bir kapıdır (`/admin`, `/kategori/:slug`, `/urun/:slug`); rewrite ile karıştırma.

**Katmanlar ve bağımlılık yönü** (csproj `ProjectReference` satırlarıyla doğrulandı):

| Proje | Bağımlı olduğu | Anlamı |
|---|---|---|
| `backend/src/SSCommerce.Domain/SSCommerce.Domain.csproj` | hiçbiri | Saf kurallar; paket ve proje referansı yok |
| `backend/src/SSCommerce.Application/SSCommerce.Application.csproj` | Domain | DTO, arayüz (port), doğrulayıcı, hesaplayıcı |
| `backend/src/SSCommerce.Infrastructure/SSCommerce.Infrastructure.csproj` | Application, Domain | EF Core, Npgsql, SQL, Identity |
| `backend/src/SSCommerce.Api/SSCommerce.Api.csproj` | Application, Infrastructure | HTTP uçları, güvenlik, composition root |

Bu kural bir teste de bağlıdır: `backend/tests/SSCommerce.Api.Tests/ArchitectureTests.cs` csproj dosyalarını okur. İş mantığı yalnız backend'dedir; Next fiyat, stok ya da sipariş kuralı hesaplamaz.

**Ortak sözlük (ilk kez görene):**
- *Kuruş (`...Minor`)*: API'de para `long` kuruştur (129900 = 1.299,00 TL); domain'de `decimal`, DB'de `numeric(18,2)`.
- *Migration*: veritabanı şemasındaki değişikliğin sürümlenmiş betiği.
- *Idempotency (eşkuvvetlilik)*: aynı isteğin iki kez gelmesi tek etki bırakır.
- *Transaction*: birden çok SQL adımını "ya hepsi ya hiçbiri" yapan sarmal.
- *Kilit*: iki isteğin aynı veriyi aynı anda bozmasını önleyen bekletme mekanizması.

Okuma sırası (bölüm 0): 1) `docs/ARCHITECTURE.md` 2) `backend/src/SSCommerce.Api/Program.cs` 3) `apps/web/next.config.ts` 4) `deploy/Caddyfile` 5) `backend/tests/SSCommerce.Api.Tests/ArchitectureTests.cs`

## 1. Vitrin (Storefront)

**Amaç.** Müşterinin gördüğü Next.js sayfaları: katalog, arama, sepet, adres, checkout, siparişler. Veriyi API'den alır, hesap yapmaz.

**Dosyalar**
- Sayfalar: `apps/web/src/app/(shop)/layout.tsx`, `apps/web/src/app/(shop)/page.tsx`, `apps/web/src/app/(shop)/urun/[slug]/page.tsx`, `apps/web/src/app/(shop)/sepet/page.tsx`, `apps/web/src/app/(shop)/checkout/page.tsx`, `apps/web/src/app/(shop)/siparisler/page.tsx`
- Bileşenler: `apps/web/src/components/ProductCard.tsx`, `apps/web/src/components/CartView.tsx`, `apps/web/src/components/CheckoutView.tsx`, `apps/web/src/components/AuthForm.tsx`, `apps/web/src/components/AddressManager.tsx`
- İstemci kodu: `apps/web/src/lib/api.ts`, `apps/web/src/lib/auth-client.ts`, `apps/web/src/lib/cart.ts`, `apps/web/src/lib/catalog.ts`, `apps/web/src/lib/orders.ts`, `apps/web/src/lib/order-key.ts`, `apps/web/src/lib/safe-next.ts`
- Sadece sunum içeriği: `apps/web/src/content/demo-catalog.ts`

**Temel kavramlar**
1. *Server Component / Client Component*: sunucuda üretilen sayfa parçası ile tarayıcıda çalışan etkileşimli parça. Katalog sunucuda okunur (`lib/catalog.ts`), sepet ve hesap tarayıcıda.
2. *Kişisel veri HTML'e gömülmez*: hesap ve sipariş verisi tarayıcıda `no-store` ile okunur (bkz. `docs/ARCHITECTURE.md`, Frontend bölümü).
3. *Her yazmadan önce taze CSRF token'ı*: `lib/api.ts` bunu yapar (CSRF: başka sitenin senin adına istek göndermesi saldırısı).
4. *İstemci sepet deposu ve nesil sayacı*: eski bir yanıtın yeni durumu ezmesini engeller (`lib/cart.ts`).
5. *Açık yönlendirme koruması*: `lib/safe-next.ts` giriş sonrası dönüş adresini doğrular.

**Mülakat soruları**
1. Fiyatı neden tarayıcıda değil sunucuda hesaplıyoruz? Tarayıcıdan gelen fiyata güvenseydik ne olurdu?
2. Bir Server Component ile Client Component arasındaki fark nedir; kişisel veriyi hangisinde okumak güvenlidir ve neden?

**Önce şunu oku:** 1) `apps/web/src/lib/api.ts` 2) `apps/web/src/lib/catalog.ts` 3) `apps/web/src/app/(shop)/urun/[slug]/page.tsx` 4) `apps/web/src/lib/cart.ts` 5) `apps/web/src/components/CartView.tsx`

## 2. Next giriş katmanı ve güvenlik

**Amaç.** Next'i saran özel Node sunucusu: istemci IP'sini güvenilir biçimde belirler, güvenlik başlıklarını ve CSP'yi ekler, sağlık uçlarını sunar, düzgün kapanır.

**Dosyalar**
- `apps/web/server.mjs` (giriş noktası; `npm run dev/start` bunu çalıştırır)
- `apps/web/server/forwarding.mjs`, `apps/web/server/privacy.mjs`, `apps/web/server/handler.mjs`, `apps/web/server/security.mjs`, `apps/web/server/csp.mjs`, `apps/web/server/observability.mjs`, `apps/web/server/health.mjs`, `apps/web/server/shutdown.mjs`
- `apps/web/src/proxy.ts` (kategori/ürün 404 ön kontrolü ve `/admin` kapısı), `apps/web/next.config.ts` (rewrite)
- Testleri: `apps/web/tests/server/forwarding.test.mjs`, `apps/web/tests/server/csp.test.mjs`, `apps/web/tests/server/handler.test.mjs`, `apps/web/tests/server/shutdown.test.mjs`

**Temel kavramlar**
1. *Güvenilir proxy / X-Forwarded-For*: istemci IP'sini başlıktan değil, güvendiğin ara sunucudan gelen zincirden okursun; yoksa saldırgan sahte IP ile rate limit'i atlatır (`forwarding.mjs`, `WEB_TRUSTED_PROXIES`, D-042).
2. *CSP (Content-Security-Policy) ve nonce*: tarayıcıya "yalnız bu betikleri çalıştır" der; nonce her istekte üretilen tek kullanımlık işarettir (`security.mjs` içinde `newNonce`/`buildCsp`, `csp.mjs`). `report-only` modu önce izleme, sonra zorlama içindir (`cspMode`).
3. *`private, no-store`*: özel sayfaların ara belleğe girmemesi (`privacy.mjs`).
4. *Rewrite build zamanında sabitlenir*: `API_BASE_URL` `next build` sırasında rewrite hedefine yazılır (`next.config.ts` içindeki yorum).
5. *`/admin` kapısı yalnız arayüz içindir*: asıl yetki API'dedir; `proxy.ts` `/auth/me` ile doğru yönlendirmeyi/403 sayfasını verir.
6. *Graceful shutdown*: kapanırken yeni istek almayıp devam edenleri bekleme (`shutdown.mjs`; `/healthz` ve `/readyz` için `health.mjs`).

**Mülakat soruları**
1. Neden `X-Forwarded-For` başlığına körü körüne güvenilmez? Güvenilir proxy listesi bunu nasıl çözer?
2. CSP'de `nonce` ile `unsafe-inline` arasındaki fark nedir; neden nonce daha güvenlidir?

**Önce şunu oku:** 1) `apps/web/server.mjs` 2) `apps/web/server/handler.mjs` 3) `apps/web/server/forwarding.mjs` 4) `apps/web/server/security.mjs` 5) `apps/web/src/proxy.ts`

## 3. Katalog (kategori ağacı, ürün, varyant, fiyat)

**Amaç.** Kategori ağacı, ürünler ve satılabilir varyantlar (SKU); herkese açık okuma uçları ile Admin yazma uçları.

**Dosyalar**
- Domain: `backend/src/SSCommerce.Domain/Catalog/Category.cs`, `backend/src/SSCommerce.Domain/Catalog/Product.cs`, `backend/src/SSCommerce.Domain/Catalog/ProductVariant.cs`, `backend/src/SSCommerce.Domain/Catalog/Money.cs`, `backend/src/SSCommerce.Domain/Catalog/CatalogRules.cs`
- Application: `backend/src/SSCommerce.Application/Catalog/ICatalogQueries.cs`, `backend/src/SSCommerce.Application/Catalog/ProductListQuery.cs`, `backend/src/SSCommerce.Application/Catalog/CatalogDtos.cs`, `backend/src/SSCommerce.Application/Catalog/AdminCatalogValidator.cs`
- Infrastructure: `backend/src/SSCommerce.Infrastructure/Catalog/CatalogQueries.cs`, `backend/src/SSCommerce.Infrastructure/Catalog/LikePattern.cs`, `backend/src/SSCommerce.Infrastructure/Catalog/AdminCatalogService.cs`, `backend/src/SSCommerce.Infrastructure/Persistence/Seed/DemoCatalogSeeder.cs`
- Api: `backend/src/SSCommerce.Api/Catalog/CatalogEndpoints.cs`, `backend/src/SSCommerce.Api/Admin/AdminCatalogEndpoints.cs`
- Web: `apps/web/src/app/(shop)/kategori/[slug]/page.tsx`, `apps/web/src/app/(shop)/arama/page.tsx`

**Temel kavramlar**
1. *Product ile ProductVariant (SKU) ayrımı*: ürün vitrindeki kalem, varyant satılan/stoklanan birimdir (D-026).
2. *Fiyat kuruş*: domain `decimal`, DB `numeric(18,2)`, API `long` kuruş; `Money` üst sınırı JavaScript güvenli tam sayısına göre seçilmiştir (D-004, D-040).
3. *Sorgu doğrulama ve allowlist*: sıralama alanı yalnız izinli değerlerden gelebilir, sayfa boyutu sınırlıdır; hata 400 `validation_failed` (`ProductListQuery.cs`).
4. *`AsNoTracking` + projection*: okuma uçlarında EF'in değişiklik izleme yükünü ve gereksiz sütunları atmak.
5. *Parametrik arama (`ILIKE`)*: kullanıcı metni SQL'e birleştirilmez, parametre olur; joker karakterler `LikePattern.cs` ile kaçırılır.
6. *Kategori ağacı koruması*: döngü ve derinlik DB trigger'ıyla da korunur (D-070).

**Mülakat soruları**
1. Parayı neden `float` yerine tam sayı kuruş ya da `decimal` olarak saklarız?
2. Kullanıcıdan gelen sıralama parametresini doğrudan `ORDER BY`'a koymak neden tehlikelidir; allowlist bunu nasıl çözer?

**Önce şunu oku:** 1) `backend/src/SSCommerce.Domain/Catalog/Money.cs` 2) `backend/src/SSCommerce.Api/Catalog/CatalogEndpoints.cs` 3) `backend/src/SSCommerce.Application/Catalog/ProductListQuery.cs` 4) `backend/src/SSCommerce.Infrastructure/Catalog/CatalogQueries.cs` 5) `backend/src/SSCommerce.Infrastructure/Persistence/Configurations/ProductConfiguration.cs`

## 4. Kimlik: oturum, CSRF/Origin, rate limit, roller

**Amaç.** Kayıt, giriş, çıkış, "ben kimim" uçları; çerez tabanlı oturum ve onu koruyan filtreler.

**Dosyalar**
- Api: `backend/src/SSCommerce.Api/Auth/AuthEndpoints.cs`, `backend/src/SSCommerce.Api/Security/ApiSecurityExtensions.cs`, `backend/src/SSCommerce.Api/Security/AuthCookieSettings.cs`, `backend/src/SSCommerce.Api/Security/CookieEvents.cs`, `backend/src/SSCommerce.Api/Security/RequestProtectionFilters.cs`, `backend/src/SSCommerce.Api/Security/AuthPolicies.cs`, `backend/src/SSCommerce.Api/Security/SecurityOptions.cs`, `backend/src/SSCommerce.Api/Security/DataProtectionSetup.cs`, `backend/src/SSCommerce.Api/Security/ProductionConfigurationValidator.cs`
- Infrastructure: `backend/src/SSCommerce.Infrastructure/Identity/AppUser.cs`, `backend/src/SSCommerce.Infrastructure/Identity/AppRoles.cs`, `backend/src/SSCommerce.Infrastructure/Identity/CredentialVerifier.cs`, `backend/src/SSCommerce.Infrastructure/Identity/SessionRevoker.cs`, `backend/src/SSCommerce.Infrastructure/Identity/UserRegistration.cs`, `backend/src/SSCommerce.Infrastructure/Identity/LetterPasswordValidator.cs`
- Testleri: `backend/tests/SSCommerce.Api.Tests/Auth/CsrfAndOriginTests.cs`, `backend/tests/SSCommerce.Api.Tests/Auth/CookieAndPolicyTests.cs`, `backend/tests/SSCommerce.Api.Tests/Auth/RateLimitAndLoggingTests.cs`, `backend/tests/SSCommerce.Api.Tests/Auth/SessionAndLockoutHardeningTests.cs`

**Temel kavramlar**
1. *Çerez oturumu*: JWT/localStorage yok; HttpOnly (JS okuyamaz), SameSite=Lax, üretimde `Secure` + `__Host-` öneki (öneki taşıyan çerez yalnız HTTPS, `Path=/` ve alt alan adsız kabul edilir) (D-033, `AuthCookieSettings.cs`).
2. *CSRF*: ASP.NET antiforgery; token `GET /api/v1/auth/csrf` ile alınır, `X-CSRF-TOKEN` başlığında gelir (`CsrfProtectionFilter`). *Origin kontrolü*: izinli kaynak listesi dışındaki tarayıcı POST'ları 403 (`OriginCheckFilter`, D-034/D-038).
3. *Rate limit politikaları*: `auth-credentials` (login/register, sıkı), `auth-general`, `mutation` (IP başına) — `RateLimitPolicies` içinde. Limiter kimlik doğrulamadan önce çalışır, bu yüzden kova IP'dir (D-053).
4. *Güvenlik damgası (security stamp) ve oturum iptali*: çıkış ve rol değişimi eski çerezi hemen geçersiz kılar (`SessionRevoker`, D-037). Hesap kilitleme: 5 hatalı denemede 15 dk (D-035).
5. *Roller ve politikalar*: `Customer`, `Warehouse`, `Admin` (`AppRoles`); `AdminOnly`, `WarehouseOrAdmin` (`AuthPolicies`). 401 = kimliksiz, 403 = yetkisiz; yönlendirme yok, ProblemDetails döner (`CookieEvents`).
6. *Üretim yapılandırma doğrulaması*: eksik/yanlış üretim ayarında uygulama açılmaz (`ProductionConfigurationValidator.cs`; Faz 9).

**Mülakat soruları**
1. Çerez tabanlı oturumda CSRF neden bir sorundur, JWT'yi Authorization başlığında taşıyan bir API'de neden genelde değildir? SameSite bunu tek başına çözer mi?
2. 401 ile 403 arasındaki fark nedir? "Kullanıcı yok" ile "parola yanlış" için neden aynı hata mesajını döneriz?

**Önce şunu oku:** 1) `backend/src/SSCommerce.Api/Security/ApiSecurityExtensions.cs` 2) `backend/src/SSCommerce.Api/Security/RequestProtectionFilters.cs` 3) `backend/src/SSCommerce.Api/Auth/AuthEndpoints.cs` 4) `backend/src/SSCommerce.Infrastructure/Identity/CredentialVerifier.cs` 5) `backend/tests/SSCommerce.Api.Tests/Auth/CsrfAndOriginTests.cs`

## 5. Stok / envanter

**Amaç.** Depolar, depo-varyant bakiyeleri ve değişmez stok hareket defteri; Admin'in stok girişi/çıkışı.

**Dosyalar**
- Domain: `backend/src/SSCommerce.Domain/Inventory/InventoryBalance.cs`, `backend/src/SSCommerce.Domain/Inventory/StockMovement.cs`, `backend/src/SSCommerce.Domain/Inventory/Warehouse.cs`, `backend/src/SSCommerce.Domain/Inventory/InventoryRules.cs`, `backend/src/SSCommerce.Domain/Inventory/StockOperationType.cs`
- Application: `backend/src/SSCommerce.Application/Inventory/StockCommand.cs`, `backend/src/SSCommerce.Application/Inventory/InventoryRequestValidator.cs`, `backend/src/SSCommerce.Application/Inventory/IInventoryService.cs`
- Infrastructure: `backend/src/SSCommerce.Infrastructure/Inventory/InventoryService.cs`, `backend/src/SSCommerce.Infrastructure/Inventory/InventoryQueries.cs`, `backend/src/SSCommerce.Infrastructure/Catalog/StockAvailability.cs`, `backend/src/SSCommerce.Infrastructure/Persistence/Configurations/StockMovementConfiguration.cs`
- Api: `backend/src/SSCommerce.Api/Inventory/InventoryEndpoints.cs`
- Migration: `backend/src/SSCommerce.Infrastructure/Persistence/Migrations/20261101003238_AddInventory.cs`

**Temel kavramlar**
1. *Defter mantığı (ledger)*: bakiye değişimi `stock_movements`'a satır olarak yazılır; satır sonradan değiştirilemez/silinemez (trigger `trg_stock_movements_no_update_delete`, D-047).
2. *Atomik koşullu güncelleme*: "oku, hesapla, yaz" yerine tek SQL: `UPDATE … WHERE on_hand + delta >= 0` (D-046). Böylece iki eşzamanlı çıkış stoğu eksiye düşüremez.
3. *Idempotency-Key*: aynı anahtar + aynı içerik tekrar gelirse ilk sonuç döner, yazma olmaz; anahtar küreseldir, aktöre bağlı değildir (D-055).
4. *Ayrılmış anahtar öneki*: `order-` öneki sipariş iadeleri içindir, admin stok anahtarlarına kapalıdır (`InventoryRequestValidator.cs`, test: `Inventory/ReservedKeyTests.cs`).
5. *Public stok soyutlaması*: yalnız aktif depolar toplanır, `availableQuantity` 20 ile sınırlanır; depo bilgisi sızmaz (`StockAvailability`, D-048).

**Mülakat soruları**
1. İki kişi aynı anda son 1 ürünü satın almaya çalışırsa "oku-kontrol et-yaz" neden hatalı çalışır? Koşullu `UPDATE` bunu nasıl çözer?
2. Idempotency anahtarı olmadan "stok ekle" isteği ağ hatası yüzünden iki kez gönderilirse ne olur?

**Önce şunu oku:** 1) `backend/src/SSCommerce.Domain/Inventory/StockMovement.cs` 2) `backend/src/SSCommerce.Application/Inventory/StockCommand.cs` 3) `backend/src/SSCommerce.Infrastructure/Inventory/InventoryService.cs` 4) `backend/tests/SSCommerce.Api.Tests/Inventory/StockConcurrencyTests.cs` 5) `backend/tests/SSCommerce.Api.Tests/Inventory/StockAtomicityTests.cs`

## 6. Sepet, adresler, checkout önizleme

**Amaç.** Kullanıcı başına tek sepet, Türkiye'ye özel adres defteri ve salt okunur "sipariş özeti" (quote).

**Dosyalar**
- Domain: `backend/src/SSCommerce.Domain/Cart/Cart.cs`, `backend/src/SSCommerce.Domain/Cart/CartRules.cs`, `backend/src/SSCommerce.Domain/Customers/CustomerAddress.cs`, `backend/src/SSCommerce.Domain/Customers/AddressRules.cs`
- Application: `backend/src/SSCommerce.Application/Carts/CartCalculator.cs`, `backend/src/SSCommerce.Application/Carts/CartRequestValidator.cs`, `backend/src/SSCommerce.Application/Checkout/CheckoutContracts.cs`
- Infrastructure: `backend/src/SSCommerce.Infrastructure/Carts/CartService.cs`, `backend/src/SSCommerce.Infrastructure/Carts/CartLineReader.cs`, `backend/src/SSCommerce.Infrastructure/Addresses/AddressService.cs`, `backend/src/SSCommerce.Infrastructure/Checkout/CheckoutPreviewService.cs`
- Api: `backend/src/SSCommerce.Api/Carts/CartEndpoints.cs`, `backend/src/SSCommerce.Api/Addresses/AddressEndpoints.cs`, `backend/src/SSCommerce.Api/Checkout/CheckoutEndpoints.cs`
- Web: `apps/web/src/app/(shop)/hesabim/adresler/page.tsx`, `apps/web/src/lib/addresses.ts`

**Temel kavramlar**
1. *Sahiplik = oturum*: kullanıcı kimliği yalnız çerezden gelir; başkasının sepeti/adresi 404 döner (varlığı bile sızdırmaz) (D-051). Test: `Carts/CartIsolationTests.cs`.
2. *Tutar her okumada güncel fiyattan hesaplanır*: `unit_price_at_add` yalnız "fiyat değişti" uyarısı içindir (`CartCalculator`, D-049).
3. *Sepet stok kuralı*: adet artışı stoğu aşarsa 409 `insufficient_stock`, azaltma her zaman serbest (D-050).
4. *Kullanıcı başına sıralama*: sepet yazmaları `carts` satır kilidiyle, adres yazmaları advisory lock ile sıraya girer (bölüm 8).
5. *Preview salt okunurdur*: sipariş/rezervasyon yaratmaz; kargo hesaplanmadığı için `grandTotalMinor` `null` (D-054).
6. *Bilinçli kabul edilen risk*: `POST /cart/items` idempotent değildir (D-057).

**Mülakat soruları**
1. Başka kullanıcının sepetine erişimde neden 403 değil 404 döneriz?
2. Sepet satırında fiyatı saklamak yerine her okumada yeniden hesaplamanın artısı ve eksisi nedir?

**Önce şunu oku:** 1) `backend/src/SSCommerce.Application/Carts/CartCalculator.cs` 2) `backend/src/SSCommerce.Api/Carts/CartEndpoints.cs` 3) `backend/src/SSCommerce.Infrastructure/Carts/CartService.cs` 4) `backend/src/SSCommerce.Infrastructure/Checkout/CheckoutPreviewService.cs` 5) `backend/tests/SSCommerce.Api.Tests/Carts/CartConcurrencyTests.cs`

## 7. Sipariş verme, idempotency, sınırlar, durum geçişleri

**Amaç.** Sepetten tek transaction'da sipariş oluşturmak (ödeme yok), stoğu düşmek, fiyat/adres kopyasını (snapshot) saklamak, siparişin yaşam döngüsünü yönetmek.

**Dosyalar**
- `backend/src/SSCommerce.Infrastructure/Orders/OrderPlacementService.cs`, `backend/src/SSCommerce.Infrastructure/Orders/OrderStatusService.cs`, `backend/src/SSCommerce.Infrastructure/Orders/OrderLedger.cs`, `backend/src/SSCommerce.Infrastructure/Orders/OrderReader.cs`, `backend/src/SSCommerce.Infrastructure/Orders/OrderQueries.cs`
- `backend/src/SSCommerce.Application/Orders/OrderContracts.cs`, `backend/src/SSCommerce.Application/Orders/OrderQuote.cs`, `backend/src/SSCommerce.Application/Orders/OrderLimits.cs` (içinde `OrderLimitOptions`, `OrderLimitMath`), `backend/src/SSCommerce.Application/Orders/OrderRequestValidator.cs`
- `backend/src/SSCommerce.Domain/Ordering/Order.cs`, `backend/src/SSCommerce.Domain/Ordering/OrderStateMachine.cs`, `backend/src/SSCommerce.Domain/Ordering/OrderRules.cs`, `backend/src/SSCommerce.Domain/Ordering/OrderStatus.cs`
- `backend/src/SSCommerce.Api/Orders/OrderEndpoints.cs`; sözleşme: `docs/CONTRACT_PHASE_7.md`
- Web: `apps/web/src/lib/order-key.ts`, `apps/web/src/components/CheckoutView.tsx`, `apps/web/src/components/OrderDetailView.tsx`

**Temel kavramlar**
1. *Sipariş = tek transaction*: kilit sırası sözleşmede sabit: (kullanıcı, anahtar) advisory lock → anahtar kontrolü → sepet `FOR UPDATE` → adres → satırlar `FOR SHARE` → quote → bakiyeler `FOR UPDATE` → koşullu `UPDATE` → snapshot/hareket/geçmiş → sepet temizliği → commit (D-059).
2. *Idempotency-Key + parmak izi*: ağ koparsa istemci aynı anahtarla yeniden dener, çift sipariş oluşmaz. İstemci tarafında "sonuç bilinmiyor" durumunda yeni anahtar üreten yol kapatılır (`lib/order-key.ts`, D-066).
3. *Quote onayı*: müşterinin gördüğü fiyat değiştiyse sipariş 409 `order_quote_changed` ile reddedilir (D-060).
4. *Snapshot*: ad, SKU, fiyat ve teslimat adresi sipariş satırına kopyalanır; katalog sonradan değişse sipariş değişmez; adrese FK yoktur (D-064).
5. *Durum makinesi*: `placed → processing → shipped → completed`, `cancelled` yalnız `placed`/`processing`'ten; tek kaynak `OrderStateMachine`; DB trigger'ı da aynısını zorlar (`trg_orders_status_transition`, `trg_orders_insert_placed`). Çift iade `order-cancel-<allocationId>` anahtarının unique indeksiyle engellenir (D-063).
6. *Ödenmemiş sipariş sınırları*: hesap başına en çok 3 açık sipariş (409 `open_order_limit`), kayan 24 saatte 10 sipariş (429 `order_daily_limit`, `Retry-After`) (D-072). Üretim eşikleri sahip kararı bekliyor (`PRODUCTION_ORDERING_POLICY_PENDING`).

**Mülakat soruları**
1. Kullanıcı "Sipariş ver"e iki kez basarsa ya da bağlantı kopup istemci yeniden denerse çift sipariş nasıl engellenir? Aynı anahtar ama farklı içerik gelirse ne dönmeli?
2. Siparişte ürün adını/fiyatını neden `products` tablosuna FK ile bağlamak yerine kopyalarız?

**Önce şunu oku:** 1) `docs/CONTRACT_PHASE_7.md` 2) `backend/src/SSCommerce.Domain/Ordering/OrderStateMachine.cs` 3) `backend/src/SSCommerce.Api/Orders/OrderEndpoints.cs` 4) `backend/src/SSCommerce.Infrastructure/Orders/OrderPlacementService.cs` 5) `backend/src/SSCommerce.Application/Orders/OrderLimits.cs`

## 8. Eşzamanlılık ve kilitler

**Amaç.** Aynı anda gelen istekler veriyi bozmasın; bozulacaksa hızlıca ve açıkça (409/503) başarısız olsun.

**Dosyalar**
- `backend/src/SSCommerce.Infrastructure/Common/TransactionalSql.cs` (kilit zaman aşımı ve `PostgresOf`)
- `backend/src/SSCommerce.Infrastructure/Orders/OrderPlacementService.cs`, `backend/src/SSCommerce.Infrastructure/Orders/OrderStatusService.cs`, `backend/src/SSCommerce.Infrastructure/Inventory/InventoryService.cs`
- `backend/src/SSCommerce.Infrastructure/Catalog/AdminCatalogService.cs`, `backend/src/SSCommerce.Infrastructure/Administration/StaffRoleService.cs`, `backend/src/SSCommerce.Infrastructure/Addresses/AddressService.cs`, `backend/src/SSCommerce.Infrastructure/Carts/CartService.cs`
- Testleri: `backend/tests/SSCommerce.Api.Tests/Carts/LockTimeoutTests.cs`, `backend/tests/SSCommerce.Api.Tests/Admin/AdminBusyTests.cs`, `backend/tests/SSCommerce.Api.Tests/Orders/OrderConcurrencyTests.cs`, `backend/tests/SSCommerce.Api.Tests/Inventory/StockConcurrencyTests.cs`, `backend/tests/SSCommerce.Api.Tests/Addresses/AddressConcurrencyTests.cs`

**Temel kavramlar**
1. *Satır kilidi*: `SELECT … FOR UPDATE` satırı yazmaya karşı kilitler; `FOR SHARE` "okuyorum, değişmesin" der, birden çok okuyucu aynı anda tutabilir.
2. *Advisory lock*: tabloya bağlı olmayan, anahtar (ör. kullanıcı+idempotency anahtarı) üzerinden alınan uygulama kilidi (`pg_advisory_xact_lock`; transaction bitince kalkar).
3. *Deadlock ve sabit kilit sırası*: iki işlem karşılıklı beklerse kilitlenir; çözüm herkesin kilitleri aynı sırayla (ör. `(varyant, depo kodu)`) almasıdır (`OrderPlacementService` başındaki açıklama).
4. *`lock_timeout` → 503 `busy`*: kilidi sonsuz beklemek yerine 5 sn sonra vazgeç; 55P03 (kilit zaman aşımı) ve 40P01 (deadlock) 503 `busy` olur (D-074). `PostgresOf`, EF/Npgsql'in bu hatayı başka bir istisnanın içine sarmasına karşı iç istisna zincirini gezer.
5. *İyimser sürüm (`version`)*: iki yönetici aynı ürünü düzenlerse ikincisi 409 `version_conflict` + `currentVersion` alır; sürüm satır kilidinden SONRA karşılaştırılır (D-068).
6. *READ COMMITTED*: PostgreSQL varsayılan izolasyonu; kilitler olmadan "kontrol et sonra yaz" yarışı mümkündür.

**Mülakat soruları**
1. Pesimistik (kilitleyen) ve iyimser (sürüm numaralı) eşzamanlılık kontrolü arasındaki fark nedir; hangisini ne zaman seçersin?
2. Deadlock nedir, nasıl oluşur ve uygulama düzeyinde nasıl önlenir?

**Önce şunu oku:** 1) `backend/src/SSCommerce.Infrastructure/Common/TransactionalSql.cs` 2) `backend/src/SSCommerce.Infrastructure/Inventory/InventoryService.cs` 3) `backend/src/SSCommerce.Infrastructure/Catalog/AdminCatalogService.cs` 4) `backend/tests/SSCommerce.Api.Tests/Orders/OrderConcurrencyTests.cs` 5) `backend/tests/SSCommerce.Api.Tests/Carts/LockTimeoutTests.cs`

## 9. Admin ve RBAC (rol tabanlı erişim)

**Amaç.** Yönetim API'si ve `/admin` arayüzü: katalog, stok, depo, sipariş, rapor, kullanıcı rolleri; her yönetim yazması denetim günlüğüne düşer.

**Dosyalar**
- Api: `backend/src/SSCommerce.Api/Admin/AdminEndpoints.cs`, `backend/src/SSCommerce.Api/Admin/AdminCatalogEndpoints.cs`, `backend/src/SSCommerce.Api/Admin/StaffEndpoints.cs`, `backend/src/SSCommerce.Api/Admin/AdminReportEndpoints.cs`
- Infrastructure: `backend/src/SSCommerce.Infrastructure/Administration/AdminAudit.cs`, `backend/src/SSCommerce.Infrastructure/Administration/StaffRoleService.cs`, `backend/src/SSCommerce.Infrastructure/Administration/BootstrapAdminCommand.cs`, `backend/src/SSCommerce.Infrastructure/Administration/DashboardQueries.cs`
- Domain: `backend/src/SSCommerce.Domain/Administration/AdminAuditEntry.cs`, `backend/src/SSCommerce.Domain/Administration/AdminAuditRules.cs`
- Web: `apps/web/src/app/admin/layout.tsx`, `apps/web/src/components/admin/AdminShell.tsx`, `apps/web/src/components/admin/UsersAdmin.tsx`, `apps/web/src/lib/admin/client.ts`
- Sözleşme: `docs/CONTRACT_PHASE_8.md`

**Temel kavramlar**
1. *RBAC*: yetki kişiye değil role bağlanır. `AdminOnly`: katalog, stok, depo, pano, rapor, denetim, rol yönetimi. `WarehouseOrAdmin`: yalnız `/api/v1/admin/orders` (iptal yine Admin) (D-045/D-071).
2. *Denetim günlüğü*: `admin_audit_log` yalnız eklenir (trigger), işlemle AYNI transaction'da yazılır; e-posta, telefon, parola gibi kişisel veri alanda yasaktır (CHECK) (D-069).
3. *Rol yönetimi korumaları*: `role_self_demotion` (kendini düşüremezsin) ve `last_admin` (son Admin kaldırılamaz); tek advisory kilit `identity:roles`; yapanın hâlâ Admin olduğu taze SQL ile doğrulanır; rol değişince hedefin oturumları iptal edilir.
4. *`--bootstrap-admin`*: ilk Admin HTTP ile değil, yerel komutla ve yalnız Development'ta verilir (D-073); HTTP rotası yoktur çünkü rotası olan bootstrap, saldırı yüzeyidir.
5. *Dürüst KPI*: pano "ciro/gelir" demez; ödeme kaydı olmadığından yalnız ödenmemiş sipariş ara toplamı gösterilir.
6. *Arayüz kapısı ≠ yetki*: `/admin` ön kontrolü `proxy.ts`'te, gerçek yetki her API ucunda.

**Mülakat soruları**
1. Neden yetki kontrolünü yalnız arayüzde (gizli menü) yapmak yetmez?
2. Son Admin'in kendini silmesini/düşürmesini nasıl engellersin ve iki Admin aynı anda birbirini düşürürse ne olur?

**Önce şunu oku:** 1) `backend/src/SSCommerce.Api/Security/AuthPolicies.cs` 2) `backend/src/SSCommerce.Api/Admin/StaffEndpoints.cs` 3) `backend/src/SSCommerce.Infrastructure/Administration/StaffRoleService.cs` 4) `backend/src/SSCommerce.Infrastructure/Administration/AdminAudit.cs` 5) `backend/tests/SSCommerce.Api.Tests/Admin/StaffRoleApiTests.cs`

## 10. Veritabanı ve migration'lar

**Amaç.** Şemayı EF Core migration'ları ile sürümlemek; asıl kuralları (kısıtlar, trigger'lar) veritabanında da zorlamak; en az yetkili roller.

**Dosyalar**
- `backend/src/SSCommerce.Infrastructure/Persistence/SSCommerceDbContext.cs`, `backend/src/SSCommerce.Infrastructure/Persistence/SSCommerceDbContextFactory.cs`
- Entity yapılandırmaları: `backend/src/SSCommerce.Infrastructure/Persistence/Configurations/OrderConfiguration.cs`, `backend/src/SSCommerce.Infrastructure/Persistence/Configurations/InventoryBalanceConfiguration.cs`, `backend/src/SSCommerce.Infrastructure/Persistence/Configurations/AdminAuditEntryConfiguration.cs` (diğerleri aynı klasörde)
- Migration'lar (eskiden yeniye) `backend/src/SSCommerce.Infrastructure/Persistence/Migrations/`: `20261028101658_InitialCatalog.cs`, `20261028104450_AddIdentity.cs`, `20261101003238_AddInventory.cs`, `20261101012902_AddCartAndAddresses.cs`, `20261101193031_AddOrdering.cs`, `20261102073215_AddAdminOperations.cs`
- Dağıtım tarafı: `deploy/db/init/10-roles.sh`, `deploy/db/migrate.sh`, `deploy/db/post-migrate.sql`, `deploy/db/invariants.sql`
- Yerel veritabanı: `docker-compose.yml` (kök; PostgreSQL, host portu 5433, D-007)

**Temel kavramlar**
1. *Code-first migration*: C# modelinden şema betiği üretilir; mevcut migration düzenlenmez, yenisi eklenir (Faz 8 betiği `DROP` içermez).
2. *Kuralı iki yerde zorlamak*: uygulama doğrular, DB de reddeder (CHECK, unique, FK, trigger). Örnek: `trg_stock_movements_no_update_delete`, `trg_orders_restrict_update`, `trg_admin_audit_log_no_update_delete`, `trg_categories_guard_parent`.
3. *Append-only tablo*: UPDATE/DELETE/TRUNCATE trigger ile reddedilir; geçmişin değişmezliği garanti olur.
4. *Üç rol (en az yetki)*: `ssc_owner` (şemayı göç sırasında değiştirir), `ssc_app` (uygulama: SELECT/INSERT/UPDATE/DELETE), `ssc_backup` (yalnız okur, yedek için). Uygulama şemayı değiştiremez (`10-roles.sh`, `post-migrate.sql`).
5. *Idempotent göç betiği*: tekrar çalıştırmak güvenlidir (`migrate.sh`).
6. *Snake_case + `timestamptz` UTC*: ad ve zaman kuralları (D-005, D-026).

**Mülakat soruları**
1. Uygulama hesabına neden şema değiştirme (DDL) yetkisi vermeyiz? Bir SQL injection açığında bu neyi sınırlar?
2. İş kuralını hem uygulamada hem veritabanı kısıtında tutmak tekrar değil midir? Hangi hataya karşı sigortadır?

**Önce şunu oku:** 1) `backend/src/SSCommerce.Infrastructure/Persistence/SSCommerceDbContext.cs` 2) `backend/src/SSCommerce.Infrastructure/Persistence/Migrations/20261101193031_AddOrdering.cs` 3) `backend/src/SSCommerce.Infrastructure/Persistence/Migrations/20261102073215_AddAdminOperations.cs` 4) `deploy/db/init/10-roles.sh` 5) `deploy/db/migrate.sh`

## 11. Hata modeli ve sağlık

**Amaç.** Tüm hatalar tek biçimde (RFC 9457 ProblemDetails) dönsün; veritabanı çökünce 500 değil anlamlı 503; canlılık ve hazırlık ayrı sorulsun.

**Dosyalar**
- `backend/src/SSCommerce.Api/Errors/ApiProblemDetails.cs` (içinde `ApiErrorCodes`), `backend/src/SSCommerce.Api/Errors/ApiResults.cs`, `backend/src/SSCommerce.Api/Errors/DatabaseUnavailableMiddleware.cs`, `backend/src/SSCommerce.Api/Http/JsonRequestBody.cs`
- `backend/src/SSCommerce.Api/Health/HealthEndpoints.cs`
- `backend/src/SSCommerce.Infrastructure/Health/PostgresReadinessHealthCheck.cs`, `backend/src/SSCommerce.Infrastructure/Health/PendingMigrationsHealthCheck.cs`, `backend/src/SSCommerce.Infrastructure/Health/ReadinessWarmUp.cs`
- Testleri: `backend/tests/SSCommerce.Api.Tests/ErrorModelAndOpenApiTests.cs`, `backend/tests/SSCommerce.Api.Tests/DatabaseUnavailableTests.cs`, `backend/tests/SSCommerce.Api.Tests/HealthEndpointTests.cs`, `backend/tests/SSCommerce.Api.Tests/ReadinessProofTests.cs`, `backend/tests/SSCommerce.Api.Tests/UnhandledExceptionTests.cs`

**Temel kavramlar**
1. *ProblemDetails*: `application/problem+json`; sabit bir `code` (ör. `validation_failed`, `busy`, `version_conflict`) istemcinin hatayı makine olarak anlamasını sağlar (D-009).
2. *Live ve ready*: `/health/live` süreç ayakta mı (DB'ye bakmaz); `/health/ready` PostgreSQL'e gerçekten bağlanır ve bekleyen migration'ı denetler, başarısızsa 503 (D-055).
3. *Bağlantı düzeyi DB hatası = 503 `service_unavailable`* (`DatabaseUnavailableMiddleware`); iş hatası ve altyapı hatası farklı kodlarla ayrılır.
4. *Isınma (warm-up)*: ilk `/health/ready` çağrısında EF modeli/JIT yüzünden yanlış 503 vermesin diye dinlemeden önce ısıtılır; kontrol EF'siz ham Npgsql ile yapılır (D-065).
5. *Hata detayı sızdırmama*: istemciye iç ayrıntı (SQL, yığın izi) değil genel mesaj gider.

**Mülakat soruları**
1. Liveness ile readiness probe'u arasındaki fark nedir; neden liveness DB'ye bakmamalı?
2. Hata gövdesinde sabit bir hata kodu (`code`) neden insan okuyan mesajdan daha önemlidir?

**Önce şunu oku:** 1) `backend/src/SSCommerce.Api/Errors/ApiProblemDetails.cs` 2) `backend/src/SSCommerce.Api/Errors/DatabaseUnavailableMiddleware.cs` 3) `backend/src/SSCommerce.Api/Health/HealthEndpoints.cs` 4) `backend/src/SSCommerce.Infrastructure/Health/ReadinessWarmUp.cs` 5) `backend/tests/SSCommerce.Api.Tests/DatabaseUnavailableTests.cs`

## 12. Gözlemlenebilirlik

**Amaç.** "Sistem şu an ne yapıyor, nerede yavaşladı/bozuldu?" sorusunu loglar, korelasyon kimliği ve metriklerle yanıtlamak.

**Dosyalar**
- `backend/src/SSCommerce.Api/Observability/ObservabilityExtensions.cs`, `backend/src/SSCommerce.Api/Observability/RequestTelemetryMiddleware.cs` (içinde `SecurityHeadersMiddleware` da var), `backend/src/SSCommerce.Api/Observability/SscMetrics.cs`, `backend/src/SSCommerce.Api/Observability/PrometheusCollector.cs`, `backend/src/SSCommerce.Api/Observability/MetricsEndpointMiddleware.cs`
- Web: `apps/web/server/observability.mjs`
- Kenar: `deploy/Caddyfile` (`X-Request-Id` ataması)
- Testleri: `backend/tests/SSCommerce.Api.Tests/Observability/RequestLoggingTests.cs`, `backend/tests/SSCommerce.Api.Tests/Observability/MetricsTests.cs`, `backend/tests/SSCommerce.Api.Tests/Observability/SecurityHeadersAndRequestIdTests.cs`, `backend/tests/SSCommerce.Api.Tests/Observability/ObservabilityKit.cs`

**Temel kavramlar**
1. *Korelasyon kimliği (`X-Request-Id`)*: bir isteğin tüm katmanlardaki loglarını tek anahtarla bağlar. Kenar (Caddy) istemcinin gönderdiğini yok sayıp kendisi üretir, böylece sahtelenemez.
2. *Yapısal log*: satır başına bir JSON nesnesi (`observability.mjs` içinde `accessLogEntry`/`writeLog`); aranabilir ve makinece okunur.
3. *Metrik*: sayaç/histogram (istek sayısı, süre, sipariş sonuçları: `SscMetrics`, `OrderOutcomes`); Prometheus metin biçiminde sunulur (`PrometheusCollector`).
4. *İç dinleyici*: `/metrics` herkese açık porttan değil, ayrı iç portta sunulur; port 0 = kapalı (`MetricsOptions`). Caddy ayrıca `/metrics`, health ve openapi yollarını 404'ler.
5. *Gizlilik*: log ve metriklere parola, çerez, kişisel veri girmemeli (bkz. `RequestLoggingTests.cs`).

**Mülakat soruları**
1. Log, metrik ve trace arasındaki fark nedir; hangi soruya hangisi cevap verir?
2. Korelasyon kimliğini neden istemciden değil kenarda üretiriz?

**Önce şunu oku:** 1) `backend/src/SSCommerce.Api/Observability/RequestTelemetryMiddleware.cs` 2) `backend/src/SSCommerce.Api/Observability/SscMetrics.cs` 3) `backend/src/SSCommerce.Api/Observability/MetricsEndpointMiddleware.cs` 4) `apps/web/server/observability.mjs` 5) `docs/OPERATIONS.md`

## 13. Dağıtım

**Amaç.** Tek komutla üretim-eşdeğeri yığın: veritabanı, göç işi, API, web, TLS kenarı, yedek; geri yükleme provası ile.

**Dosyalar**
- `deploy/compose.yml` (servisler: db, migrate, api, web, edge, backup), `deploy/compose.perf.yml` (yalnız yük sınaması katmanı), `deploy/stack.env.example`
- İmajlar: `deploy/Dockerfile.api`, `deploy/Dockerfile.web`; kök `.dockerignore`
- Kenar: `deploy/Caddyfile`
- Betikler: `deploy/scripts/gen-env.sh`, `deploy/scripts/https-check.sh`, `deploy/scripts/resilience-check.sh`, `deploy/scripts/backup-inner.sh`, `deploy/scripts/restore-verify.sh`, `deploy/scripts/upgrade-drill.sh`, `deploy/scripts/api-command.sh`, `deploy/scripts/stack-fixtures.sh`
- Belgeler: `docs/DEPLOYMENT.md`, `docs/OPERATIONS.md`
- Web üretim testleri: `apps/web/playwright.prod.config.ts`, `apps/web/tests/prod/01-cookies.spec.ts`, `apps/web/tests/prod/06-leak.spec.ts` (kalan `tests/prod` dosyaları: Faz 9'da eklenir)

**Temel kavramlar**
1. *TLS sonlandıran ters proxy*: HTTPS'i Caddy çözer, arkadaki Next/API düz HTTP konuşur; `Secure`/`__Host-` çerezleri bu yüzden ancak gerçek TLS ile doğrulanır (D-013, `https-check.sh`).
2. *Göç işi ayrı imaj/servis*: şema `ssc_owner` ile `migrate` servisinde değişir, API `ssc_app` ile çalışır (bölüm 10).
3. *Sırlar ortamda, kodda değil*: `gen-env.sh` sırları ekrana yazmadan üretir; `stack.env.example` sahte değerlidir.
4. *Kalıcı birimler*: veritabanı, Data Protection anahtarları (çerez şifreleme anahtarı; kaybolursa tüm oturumlar düşer), Caddy verisi, yedekler ayrı volume'lardadır.
5. *Yedek ancak geri yükleme denenmişse yedektir*: `restore-verify.sh` tek kullanımlık veritabanına geri yükleyip değişmezleri doğrular; `upgrade-drill.sh` eski şemadan yükseltmeyi dener.
6. *Dayanıklılık*: yeniden başlatma sırasında bağlantı kopmaları kısa süre yeniden denenir (`resilience-check.sh`, Caddy `lb_try_duration`).
7. *Gerçek barındırma geçişi YAPILMADI*: sahip eylemi, bkz. `docs/DEPLOYMENT.md` bölüm 9.

**Mülakat soruları**
1. Veritabanı göçünü uygulama açılırken mi yoksa ayrı bir işte mi çalıştırırsın? Her birinin riski nedir?
2. "Yedeğimiz var" ile "yedeğimizi geri yükleyebildiğimizi kanıtladık" arasındaki fark nedir?

**Önce şunu oku:** 1) `docs/DEPLOYMENT.md` 2) `deploy/compose.yml` 3) `deploy/Caddyfile` 4) `deploy/Dockerfile.api` 5) `docs/OPERATIONS.md`

## 14. CI

**Amaç.** Her değişiklikte derleme, lint, tip denetimi, test ve imaj derlemesini otomatik çalıştırmak.

**Dosyalar:** `.github/workflows/ci.yml`; yerel karşılığı: `apps/web/package.json` script'leri; durum: `docs/PROGRESS.md`

**Temel kavramlar**
1. *Üç iş (job)*: `backend` (restore, build, test), `web` (lint, typecheck, HTTP ingress testleri, build, Playwright + axe E2E, proxy rate-limit), `images` (compose geçerli mi, actionlint, API/göç/web imajı derleme — yalnız derleme, yayın yok).
2. *CI'da gerçek PostgreSQL testi atlanamaz*: yapılandırma eksikse test KIRMIZI olur, sessizce geçmez (D-018).
3. *Statik kontroller test değildir ama ilk savunmadır*: `tsc --noEmit`, ESLint.
4. *Bilinen engel*: GitHub Actions faturalandırma yüzünden uzakta çalışmıyor (`BLOCKED_EXTERNAL`); sonuçlar yerelde üretilip `docs/PROGRESS.md`'ye yazılıyor. CI dosyası var ama "yeşil rozet" kanıtı yoktur.
5. *Sabit sürümler / tedarik zinciri*: bkz. `docs/DEPLOYMENT.md` bölüm 8.

**Mülakat soruları**
1. CI ile CD arasındaki fark nedir? Bu depoda hangisi var?
2. Yeşil bir CI "kod doğru" demek midir? Neyi garanti eder, neyi etmez?

**Önce şunu oku:** 1) `.github/workflows/ci.yml` 2) `apps/web/package.json` 3) `docs/PROGRESS.md` (CI bölümü) 4) `backend/tests/SSCommerce.Api.Tests/Database/PostgresTestDatabase.cs`

## 15. Testler

**Amaç.** Kodun doğru, güvenli ve eşzamanlılığa dayanıklı olduğunu kanıtlamak. Rehber terimleri (`~/.claude/testing-guide.md`) bu bölümdeki örneklerle eşleşir.

**Dosyalar**
- Backend: `backend/tests/SSCommerce.Api.Tests/**` (klasörler: `Domain`, `Database`, `Auth`, `Carts`, `Orders`, `Inventory`, `Admin`, `Catalog`, `Addresses`, `Checkout`, `Observability`)
- Ortak altyapı: `backend/tests/SSCommerce.Api.Tests/ApiFactory.cs`, `backend/tests/SSCommerce.Api.Tests/Database/PostgresTestDatabase.cs`
- Web sunucu birim testleri: `apps/web/tests/server/*.test.mjs` (`npm run test:server`)
- Web E2E: `apps/web/tests/e2e/*.spec.ts`, `apps/web/playwright.config.ts`, `apps/web/tests/run-e2e.mjs`, `apps/web/tests/e2e/start-api.mjs`
- Üretim-eşdeğeri web testleri: `apps/web/tests/prod/01-cookies.spec.ts`, `apps/web/tests/prod/06-leak.spec.ts` (diğerleri: Faz 9'da eklenir)
- E2E sunucusu: `backend/tools/SSCommerce.E2eHost/Program.cs`, `backend/tools/SSCommerce.E2eHost/FixtureControl.cs`

**Test türü haritası: hangi test neden o türde**

| Tür | Örnek dosyalar | Neden bu tür |
|---|---|---|
| Unit (saf, DB/ağ yok) | `Carts/CartCalculatorTests.cs`, `Admin/OrderLimitMathTests.cs`, `Domain/OrderingStateMachineTests.cs`, `Admin/AdminCatalogValidatorTests.cs`, `Inventory/InventoryRequestValidatorTests.cs`, `Inventory/UnicodeTextRulesTests.cs` | Girdi → çıktı hesabı; hızlı, çok sayıda kenar durum denenir |
| Mimari (statik) | `ArchitectureTests.cs` | Katman yönünün bozulmasını derlemeden önce yakalar (csproj okur) |
| Integration (gerçek PostgreSQL) | `Database/OrderStatusGuardTests.cs`, `Database/InventoryConstraintTests.cs`, `Database/CategoryTreeGuardTests.cs`, `Database/AdminAuditLogTests.cs`, `Database/*MigrationTests.cs` | Trigger, CHECK, unique ve migration ancak gerçek DB'de vardır; mock'ta bu kurallar hiç çalışmaz |
| API (HTTP kapısı) | `Auth/AccountApiTests.cs`, `Carts/CartApiTests.cs`, `Admin/AdminCatalogApiTests.cs`, `Orders/OrderPlacementTests.cs` | Durum kodu, ProblemDetails şeması, yetki; `ApiFactory` ile gerçek HTTP hattı |
| Race (eşzamanlılık) | `Orders/OrderConcurrencyTests.cs`, `Inventory/StockConcurrencyTests.cs`, `Carts/CartConcurrencyTests.cs`, `Addresses/AddressConcurrencyTests.cs` | Yarış, kilit ve gerçek bağlantılar ister; mock'ta tek iş parçacığı vardır, yarış yoktur |
| Rollback / atomiklik | `Inventory/StockAtomicityTests.cs`, `Admin/AdminAtomicityTests.cs`, `Orders/OrderFailureRecoveryTests.cs` | "İkinci adım patlarsa ilki geri alınıyor mu" yalnız gerçek transaction'la sınanır |
| Negative (güvenlik) | `Auth/CsrfAndOriginTests.cs`, `Orders/OrderSecurityTests.cs`, `Carts/CartSecurityTests.cs`, `Carts/CartIsolationTests.cs` | 400/401/403/404/409 her biri ayrı beklenti |
| Regression | `Domain/CatalogRulesRegressionTests.cs` | Bulunan hatanın geri gelmemesi için hatayı önce yakalayan test yazılır |
| Smoke / dayanıklılık | `HealthEndpointTests.cs`, `ReadinessProofTests.cs`, `DatabaseUnavailableTests.cs`, `deploy/scripts/https-check.sh` | "Ayakta mı, DB gidince doğru davranıyor mu?" |
| E2E (tarayıcı) | `apps/web/tests/e2e/flows.spec.ts`, `cart.spec.ts`, `orders.spec.ts`, `admin-access.spec.ts`, `csp.spec.ts` | Yalnız kritik kullanıcı akışları; yavaş ve kırılgan olduğu için az sayıda |
| Erişilebilirlik | `apps/web/tests/e2e/support.ts` (`axeSerious`) | Playwright sayfası üzerinde axe taraması |
| Node sunucu birim | `apps/web/tests/server/csp.test.mjs`, `forwarding.test.mjs`, `handler.test.mjs`, `shutdown.test.mjs`, `privacy.test.mjs` | Saf Node fonksiyonları; Next/tarayıcı gerekmez |
| Load / stress / spike / soak | `deploy/compose.perf.yml` (yük katmanı) | Ayrıntı ve sonuçlar için bkz. `docs/PROGRESS.md`; yük/stres Faz 9 işidir, bu haritada doğrulanmış tek bir yük betiği dosyası verilmedi |

**Temel kavramlar (rehber terimleriyle)**
1. *Test piramidi*: çok sayıda hızlı unit → daha az integration/API → birkaç kritik E2E. Bu depoda gerçek PostgreSQL integration katmanı bilinçli olarak kalındır, çünkü ticaretin kritik kuralları (stok, sipariş, yetki) veritabanı kısıtlarına ve kilitlere dayanır.
2. *Isolation ve fixture*: her test grubu kendi `sscommerce_test_<guid>` veritabanını açar, migration'ları sıfırdan uygular, sonunda siler; adı desene uymayan DB asla silinmez (`PostgresTestDatabase`, D-029). Hazır veri kurucuları: `Auth/AuthApiFixture.cs`, `Catalog/CatalogApiFixture.cs`, `Inventory/InventoryApiFixture.cs`, `Carts/CartKit.cs`, `Orders/OrderKit.cs`, `Admin/AdminKit.cs`, `Database/OrderingTestData.cs`.
3. *Mock/stub/fake*: bu projede ağırlıkla gerçek parçalar kullanılır; basit fake örnekleri: `FakePeerStartupFilter` (`Auth/ClientAddressAndRevocationTests.cs`) ve `FakeEnvironment` (`Auth/CookieAndPolicyTests.cs`). Neden az mock: mock'lanmış DB kilit/trigger davranışını taklit edemez.
4. *Flaky (kararsız) test*: D-039 örnek vaka — havuzsuz bağlantılarda Windows geçici portları tükendi, koşuların bir kısmı kırmızı oldu; çözüm havuzlu bağlantı + `ClearPool`. D-018: CI'da testin sessizce atlanması da bir flaky/yanlış güven kaynağıdır.
5. *Happy path / edge case / negative*: örnek üçlü — normal sipariş (`Orders/OrderPlacementTests.cs`), sınır durumları (`Admin/OrderLimitMathTests.cs`), yetkisiz/yanlış istek (`Orders/OrderSecurityTests.cs`). Her endpoint için rehberdeki soru listesini (404, 409, yetki, race, rollback, idempotency, şema tutarlılığı, doğrulama) sor.
6. *Assertion*: beklenen sonucun açık ifadesi (`Assert.Equal`, `Assert.Empty`...). Örnek okuma: `ArchitectureTests.cs`.
7. *Coverage*: ipucudur, hedef değil; %100 "her satır doğru" demez.
8. *E2E ortamı*: `E2eHost` gerçek API'yi ayrı test DB'siyle çalıştırır; rol atama yalnız loopback `/fixture/role` ucundandır (D-044, D-076).

**Mülakat soruları**
1. "Yarış durumunu neden gerçek veritabanı olmadan test edemeyiz?" Bir mock tabanlı test bunu neden göremez?
2. Test piramidinin tepesini (E2E) neden dar tutarız? Flaky bir E2E testi ekibe ne yapar?

**Önce şunu oku:** 1) `backend/tests/SSCommerce.Api.Tests/Database/PostgresTestDatabase.cs` 2) `backend/tests/SSCommerce.Api.Tests/ApiFactory.cs` 3) `backend/tests/SSCommerce.Api.Tests/Carts/CartCalculatorTests.cs` 4) `backend/tests/SSCommerce.Api.Tests/Inventory/StockConcurrencyTests.cs` 5) `apps/web/tests/e2e/flows.spec.ts`

## 16. Kararlar: öğretici ilk 10 (`docs/DECISIONS.md`)

| Karar | Neden önemli |
|---|---|
| D-003 Modüler monolit, sade 4 katman; MediatR/CQRS/generic repository yok | Erken soyutlama karmaşıklık getirir; "gerekmedikçe ekleme" disiplini |
| D-004 Para: domain `decimal`, DB `numeric(18,2)`, API kuruş `long` | Kayan nokta hatası yok; birim tek ve açık |
| D-006 Same-origin: `/api/backend/*` rewrite | CORS gerekmez; çerez oturumu aynı origin'de güvenli kalır |
| D-018 CI'da gerçek PostgreSQL testi atlanamaz | Sessiz skip yeşil CI'da yanlış güven verir |
| D-034 CSRF: antiforgery token + `X-CSRF-TOKEN` + Origin kontrolü | Çerez oturumunun asıl zayıflığına karşı katmanlı koruma |
| D-046 Stok atomikliği: tek koşullu SQL, okuma-değiştirme-yazma yok | Eşzamanlı satışta eksiye düşmeyi veritabanı düzeyinde engeller |
| D-059 Sipariş tek transaction, sabit kilit sırası | Deadlock ve çift sipariş bu sözleşmeyle önlenir |
| D-061 Stok sipariş anında düşer, ödeme alınmaz, otomatik serbest bırakma yok | Kapsam sınırının dürüstçe yazılması; ödeme yokken "ödendi" izlenimi verilmez |
| D-064 `orders` silinmez; kalemler ve geçmiş yalnız eklenir (trigger) | Denetlenebilirlik: geçmişin değiştirilememesi DB'de garanti |
| D-074 Kilit zaman aşımı/deadlock → 503 `busy`; `PostgresOf` iç istisnayı gezer | Çerçevenin sarmaladığı hatayı doğru sınıflama dersi |

## 17. Önerilen ders sırası (12 ders)

Her ders için alıştırmalar KOD DEĞİŞTİRMEZ. Gerçek PostgreSQL gerektiren testler için test sunucusunun ayarı `backend/tests/SSCommerce.Api.Tests/Database/PostgresTestDatabase.cs` içinde anlatılan ortam değişkeniyle verilir (parola bilgisi bu belgeye yazılmaz); kurulum için `docs/PROGRESS.md`'ye bak.

| # | Ders | Hedef | Okunacak dosyalar | Alıştırma (gözlem / test çalıştırma) |
|---|---|---|---|---|
| 1 | Büyük resim ve katmanlar | İstek yolculuğunu ve bağımlılık yönünü anlatabilmek | `docs/ARCHITECTURE.md`, `backend/src/SSCommerce.Api/Program.cs`, `ArchitectureTests.cs` | `dotnet test backend/SSCommerce.slnx --filter ArchitectureTests` çalıştır, hangi csproj kurallarını okuduğunu açıkla |
| 2 | Next giriş katmanı | Proxy güveni, CSP nonce ve no-store mantığı | bölüm 2 listesi | `apps/web` içinde `npm run test:server` çalıştır; bir test adını seçip neyi kanıtladığını söyle |
| 3 | Vitrin ve katalog | Server/Client ayrımı, kuruş, sorgu doğrulama | bölüm 1 ve 3 "Önce şunu oku" | Tarayıcıda ürün sayfasını aç, DevTools Network'te `/api/backend/...` isteğini ve yanıttaki `...Minor` alanlarını bul |
| 4 | Kimlik ve oturum | Çerez, CSRF, Origin, rate limit | bölüm 4 | `--filter CsrfAndOriginTests` çalıştır; her testin hangi negative durumu denediğini listele |
| 5 | Stok | Defter, atomik güncelleme, idempotency | bölüm 5 | `--filter StockAtomicityTests` ve `StockConcurrencyTests` çalıştır; neden gerçek DB gerektiğini yaz |
| 6 | Sepet, adres, checkout önizleme | Sahiplik, hesaplama, salt okunur preview | bölüm 6 | `--filter CartIsolationTests` çalıştır; "403 değil 404" kararının testteki karşılığını göster |
| 7 | Sipariş verme | Tek transaction, snapshot, durum makinesi | bölüm 7 | `--filter OrderStateTransitionTests` çalıştır; `OrderStateMachine`'deki geçiş tablosunu çizerek testlerle eşleştir |
| 8 | Eşzamanlılık ve kilitler | FOR UPDATE/SHARE, advisory lock, busy, sürüm | bölüm 8 | `--filter LockTimeoutTests` çalıştır; `TransactionalSql.cs`'de `PostgresOf`'un neden gerektiğini anlat |
| 9 | Admin, RBAC, denetim | Politikalar, audit aynı transaction, last_admin | bölüm 9 | `--filter StaffRoleApiTests` çalıştır; `role_self_demotion` ve `last_admin` testlerini bul |
| 10 | Veritabanı ve migration | Kısıt/trigger, rol ayrımı | bölüm 10 | `--filter OrderStatusGuardTests` çalıştır; hangi trigger'ın hangi geçişi reddettiğini migration dosyasında bul |
| 11 | Hata, sağlık, gözlem, dağıtım, CI | ProblemDetails, live/ready, metrik, compose, CI | bölüm 11–14 | `--filter HealthEndpointTests` çalıştır; `deploy/compose.yml` servislerini ve `ci.yml` işlerini tek sayfalık şemada eşleştir |
| 12 | Test haritası ve kapanış | Piramit, flaky, "hangi test neden o türde" | bölüm 15–16, `docs/PROGRESS.md` | Bölüm 15 tablosundan her türden bir test seç, kendi cümlenle "bu neden unit/integration/E2E?" yaz; E2E için `npm run e2e` (apps/web; Docker gerektirebilir, bkz. `apps/web/tests/run-e2e.mjs`) |

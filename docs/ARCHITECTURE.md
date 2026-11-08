# SSCommerce Mimari

## Genel görünüm

```
Tarayıcı ──► Next.js (apps/web) ──/api/backend/* rewrite──► ASP.NET Core API (backend) ──► PostgreSQL
              sunum katmanı                                  domain iş mantığının tek sahibi
```

- **Modüler monolit:** tek API süreci, tek veritabanı. Modüller (Catalog, Inventory, Ordering…) ileride `Application`/`Domain` içinde klasör olarak ayrılır.
- **Same-origin:** Tarayıcı yalnızca Next.js origin'ine istek atar. Next.js `rewrites` ile `/api/backend/*` isteklerini API'ye iletir. Böylece geliştirmede CORS gerekmez ve ileride cookie tabanlı auth (Faz 4) aynı origin'de kalır.
- **İş mantığı yalnızca backend'de** bulunur. Next.js fiyat, stok ya da sipariş kuralı hesaplamaz; API'den gelen veriyi gösterir.

## Backend katmanları (`backend/`)

| Proje | Sorumluluk | Bağımlı olduğu |
|---|---|---|
| `SSCommerce.Domain` | Varlıklar, değer nesneleri, domain kuralları | Hiçbiri |
| `SSCommerce.Application` | Kullanım senaryoları, arayüzler (port'lar), DTO'lar | Domain |
| `SSCommerce.Infrastructure` | EF Core `DbContext`, Npgsql, dış servis adaptörleri | Application, Domain |
| `SSCommerce.Api` | HTTP uçları, OpenAPI, hata modeli, composition root | Application, Infrastructure (yalnızca DI kaydı için) |
| `tests/SSCommerce.Api.Tests` | `WebApplicationFactory` ile gerçek HTTP testleri; izole PostgreSQL test veritabanları (D-029) | Api, Infrastructure |

Bilinçli olarak eklenmeyenler: MediatR/CQRS, generic repository, event bus. Gerektikleri gün gerekçesiyle eklenir.

### Faz 1 uçları
- `GET /health/live`: Süreç ayakta mı? Veritabanına bakmaz, her zaman 200 döner.
- `GET /health/ready`: PostgreSQL'e gerçekten bağlanır (`SELECT 1`). Başarısızsa **503** döner, hatayı gizlemez.
- `GET /openapi/v1.json`: OpenAPI belgesi (yalnızca Development).
- Hatalar RFC 9457 `ProblemDetails` biçiminde döner (`application/problem+json`).

### Faz 3 Catalog (D-026–D-030)
Bir istek şu yoldan geçer: `GET /api/v1/products?...` → `Api/Catalog/CatalogEndpoints.cs` (ham sorgu parametrelerini string olarak alır) → `Application/Catalog/ProductListQuery.cs` doğrulayıcısı (sort allowlist, sayfa sınırları; hatada 400 `validation_failed`) → `ICatalogQueries` arayüzü → `Infrastructure/Catalog/CatalogQueries.cs` (EF Core, `AsNoTracking` + projection, parametrik `ILIKE`) → PostgreSQL → DTO (fiyat `Money` ile kuruşa çevrilir) → JSON.
- Tablolar: `categories`, `products`, `product_variants` (satılabilir SKU), `product_images`. Kurallar DB'de de var: slug/SKU biçimi ve unique, `price >= 0`, `currency = 'TRY'`, tek primary görsel, `ON DELETE RESTRICT`.
- Public uçlar yalnız aktif kategori + aktif ürün + aktif varyantı gösterir. Faz 5'ten beri `inStock` ve cap'li `availableQuantity` de döner.
- Seed: `Infrastructure/Persistence/Seed`, yalnız Development'ta `--seed-demo-catalog` ile.

### Faz 4 Identity / Security (D-032–D-036)
- `SSCommerceDbContext : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>`; `AspNet*` tabloları aynı şemada, ayrı `AddIdentity` migration'ı. Roller `Customer`, `Warehouse`, `Admin` migration'da sabit.
- Oturum: Identity application cookie'si (HttpOnly, SameSite=Lax, üretimde `__Host-` + Secure). API yönlendirme yapmaz: 401/403 ProblemDetails (`Api/Security/CookieEvents`).
- Logout güvenlik damgasını yeniler; `SecurityStampValidator` aralığı 0 olduğundan eski cookie hemen reddedilir.
- CSRF: `/api/v1` grubundaki her değiştiren uçta antiforgery filtresi (`X-CSRF-TOKEN`) ve izinli Origin kontrolü (`Api/Security/RequestProtectionFilters`). Auth uçlarında IP başına rate limit.
- Policy'ler: `AdminOnly`, `WarehouseOrAdmin` (`Api/Security/AuthPolicies`). Public katalog anonimdir.

### Faz 5 Inventory (D-045–D-048, D-055)
- Tablolar: `warehouses`, `inventory_balances` (depo + varyant unique, `on_hand_quantity` 0..1.000.000), `stock_movements` (trigger ile yalnız ekleme). Kod `Domain/Inventory`, `Application/Inventory`, `Infrastructure/Inventory`, `Api/Inventory`.
- Stok yazması tek transaction'dır: `Idempotency-Key` için advisory lock → anahtar kontrolü → depo ve varyantı `FOR SHARE` okuma → koşullu tek SQL → hareket kaydı → commit. Okuma-değiştirme-yazma yoktur; CHECK ve unique index son savunma hattıdır. Yazmalar ve okumalar yalnız Admin'e açıktır (`/api/v1/admin`).
- `Infrastructure/Catalog/StockAvailability` public stoğun tek tanımıdır: yalnız aktif depolar toplanır, `availableQuantity` 20 ile sınırlanır. Depo bilgisi public yanıtlarda yoktur. Demo katalog için stok seed edilmez.

### Faz 6 Cart / Checkout (D-049–D-054, D-056, D-057)
- Tablolar: `carts` (kullanıcı başına tek), `cart_items` (sepet + varyant unique, adet 1..20), `customer_addresses` (kullanıcı başına tek varsayılan, partial unique index).
- Kod `Domain/Cart`, `Domain/Customers`, `Application/{Carts,Addresses,Checkout}`, `Infrastructure/{Carts,Addresses,Checkout}`, `Api/{Carts,Addresses,Checkout}`. Tutarlar `CartCalculator` ile her okumada güncel fiyattan hesaplanır; `unit_price_at_add` yalnız "fiyat değişti" bilgisidir.
- Sepet yazmaları `carts` satır kilidiyle, adres yazmaları iki argümanlı advisory lock ile kullanıcı başına sıraya girer. Kullanıcı kimliği yalnız oturumdan gelir; başkasının kaynağı 404.
- `POST /checkout/preview` iş açısından salt okunurdur; kargo hesaplanmadığı için `grandTotalMinor` `null` döner, sipariş ve ödeme oluşturmaz. Faz 7'den beri devam edilebilir özetlerde `quote` döndürür.
- `DatabaseUnavailableMiddleware` bağlantı düzeyindeki DB hatalarını 503 `service_unavailable` yapar. `/health/ready` bekleyen migration'ı da denetler.

### Faz 7 Ordering (D-059–D-067)
- Tablolar: `orders`, `order_items`, `order_inventory_allocations`, `order_status_history`. Teslimat adresi, fiyat, ad ve SKU sipariş satırlarında kopyadır; adrese FK yoktur. Trigger'lar kalemleri, dağıtımları ve geçmişi yalnız eklenir yapar; `orders` satırında yalnız `status` ve `updated_at` değişir, satır silinemez.
- Kod `Domain/Ordering`, `Application/Orders`, `Infrastructure/Orders`, `Api/Orders`. Sözleşme ve kilit sırası `docs/CONTRACT_PHASE_7.md`'de sabittir: `(kullanıcı, anahtar)` advisory lock → anahtar kontrolü → sepet `FOR UPDATE` → adres → satırlar `FOR SHARE` → quote → bakiyeler `(varyant, depo kodu)` sırasıyla `FOR UPDATE` → koşullu `UPDATE` → snapshot, hareket, geçmiş → sepet temizliği → commit.
- Durum geçişleri `OrderStateMachine`'dedir ve tek kaynaktır. İptal `orders` satır kilidiyle sıraya girer; iade özgün depoya gider ve `order-cancel-<allocationId>` anahtarının unique indeksi çift iadeyi veritabanında engeller. `order-` öneki admin stok anahtarlarına kapalıdır.
- Stok sipariş commit'inde düşer, ödeme alınmaz, otomatik serbest bırakma yoktur (D-061). `shippingMinor` ve `grandTotalMinor` `null`'dır.
- Personel uçları `/api/v1/admin/orders` ayrı grupta `WarehouseOrAdmin` ister; iptal Admin gerektirir. Personel yanıtlarında müşteri verisi yoktur.
- `ReadinessWarmUp` dinlemeye başlamadan önce çalışır, `postgres` kontrolü EF'siz ham Npgsql kullanır (D-065).

### Faz 8 ERP / Admin (D-068–D-076)
- Sözleşme `docs/CONTRACT_PHASE_8.md` (FROZEN v1.1). Tek ek, yalnız ekleyen migration `AddAdminOperations` (`categories`, `products`, `product_variants` üzerinde `version`; append-only `admin_audit_log`; kategori üst-düğüm trigger'ı; sipariş durum trigger'ı; kısmi indeksler). Mevcut migration'lar düzenlenmedi, betik `DROP` içermez.
- Roller: `AdminOnly` (katalog, stok, depo, pano, rapor, denetim, kullanıcı/rol, teslimat görüntüleme) ve `WarehouseOrAdmin` (yalnız `/api/v1/admin/orders`: liste, detay, ileri geçişler; iptal Admin). Warehouse katalog, stok, pano, rapor, denetim, rol ve teslimat verisine erişemez. Teslimat verisini Warehouse'a açmak sahip kararı bekliyor (D-071) ve açılmadı.
- Katalog yazmaları (`Infrastructure/Catalog/AdminCatalogService`): tek transaction, `lock_timeout` 5 sn, kategori yazmalarında önce `catalog:category-tree` advisory kilidi, sonra satır kilidi; iyimser sürüm (`version_conflict` 409 + `currentVersion`) sürüm satır kilidinden SONRA karşılaştırılır. Kilit zaman aşımı ve deadlock 503 `busy` olur; `TransactionalSql.PostgresOf` iç istisna zincirini gezer, çünkü Npgsql yürütme stratejisi 55P03/40P01'i `InvalidOperationException` içine sarar (D-074).
- Her yönetim yazması aynı transaction'da `admin_audit_log` satırı bırakır; `changes` alanında e-posta, telefon, adres, parola vb. yasaktır (CHECK). Teslimat görüntüleme POST'tur ve `order.delivery_view` denetimi yazar.
- Rol yönetimi: kimlik hesap kimliğiyledir (e-posta araması yok), tek advisory kilit `identity:roles`, yapanın Admin olduğu taze SQL ile yeniden doğrulanır, `role_self_demotion` ve `last_admin` (kilitsiz Admin sayısı) korumaları, değişimde hedefin oturumları iptal edilir. İlk Admin yalnız yerel komutla: `dotnet run --project backend/src/SSCommerce.Api -- --bootstrap-admin` (yalnız Development, loopback bağlantı, HTTP rotası yok; D-073).
- Pano KPI'ları dürüsttür: sipariş sayıları durum bazlı; `openOrders.unpaidSubtotalMinor` yalnız `placed`+`processing` kalem ara toplamıdır ve hiçbir yerde ciro/gelir/tahsilat diye anılmaz (ödeme kaydı yok). Stok sayıları satılabilir varyant (varyant, ürün ve kategori aktif) ve aktif depolar üzerindendir; "az" `0 < onHand ≤ eşik`.
- Ödenmemiş sipariş sınırları (F8.5, D-072): hesap başına açık `placed`+`processing` ≤ 3 (409 `open_order_limit`), kayan 24 saatte ≤ 10 sipariş durumdan bağımsız (429 `order_daily_limit`, `Retry-After` = pencereden çıkışa kalan sürenin tavanı, 1..86400). Sepet satır kilidi altında sayılır; idempotency tekrarı sayılmaz. Üretim eşikleri `PRODUCTION_ORDERING_POLICY_PENDING`. E2E ortamında günlük sınır yalnız E2eHost alt sürecinde 11'e çıkarılır (`E2E_MAX_ORDERS_PER_DAY`, D-075); `SSCommerce.Api`'de varsayılanlar 10 ve 3'tür.
- Web: vitrin `src/app/(shop)/` route group'unda, yönetim `src/app/admin/` altında (11 rota; kendi düzeni, vitrin başlığı/altlığı yok). `src/proxy.ts` `/admin` için `/auth/me` ile arayüz kapısıdır; yetki API'dedir. Admin verisi tarayıcıda `no-store` ile okunur, HTML/RSC'ye gömülmez.
- E2E: `/fixture/role` yalnız E2eHost'un loopback kontrol ucunda bulunur (yalnız tam eşleşen `Admin`/`Warehouse`, her çağrıda güvenlik damgasını yeniler, üretim API'sinde yoktur; D-076). Playwright rol atamasını yalnız bu uçtan yapar, başarısızlıkta açıkça düşer; Docker CLI gerekmez.

## Veri kuralları
- Para: domain'de C# `decimal`, veritabanında `numeric(18,2)`. API'de **kuruş cinsinden tam sayı** (`priceMinor: 129900` = 1.299,00 TL).
- Zaman: kalıcı veride UTC (`timestamptz`).
- Gelecek model: Product → ProductVariant/SKU (Faz 3 Catalog) → Warehouse/InventoryBalance (Faz 5 Inventory); siparişte fiyat snapshot'ı ve idempotency anahtarı (Faz 7 Ordering). Catalog Faz 3'te, Inventory Faz 5'te, Ordering Faz 7'de oluşturuldu. Stok rezervasyonu MVP dışıdır.

## Frontend (`apps/web`)
- Next.js App Router + React + TypeScript + Tailwind CSS. Tek web uygulaması; `/admin` aynı uygulamada açılır (Faz 8).
- `src/lib/catalog.ts` kategori/ürün DTO'larını API'den `no-store` ile okur. Server Component'lar fiyat ve filtre kurallarını backend'e bırakır. `demo-catalog.ts` artık yalnız görsel sunum, hero/banner ve sekiz kategori kartı için kullanılır; ürün fixture'ları API hatasında yedek kaynak değildir.
- `src/components/`: Header, SearchForm, CategoryNav, MobileNav (client), BottomNav (yalnız mobil alt gezinti), SoonItem (pasif "Yakında" öğesi: favoriler), Icons, Hero, CategoryCard, ProductCard, CollectionSection, TrustSection, Footer, art (çizimler). Token'lar `globals.css` içindeki `@theme` bloğunda.
- Bileşenler (D-025 sonrası): Header, SearchForm, CategoryNav, MobileNav, BottomNav, SoonItem, Icons, Logo, Hero, ServiceStrip, CategoryCard, Banners, ProductTabs, ProductCard, Footer. `art.tsx`, TrustSection ve CollectionSection kaldırıldı.
- Görseller: `public/images/{hero,promo,banner,kategori,urun}/*.webp` (26 Unsplash fotoğrafı, 592 KB), `next/image` ile yerelde; çalışma anında dış istek yok. Kayıt `src/content/image-credits.ts` + `public/images/CREDITS.md`, liste `/gorsel-kaynaklari`'nda (D-025). Test, her kayıtlı dosyanın diskte olduğunu ve sayfadaki her `/images/` yolunun kayıtlı olduğunu denetler.
- Rotalar: `/`, `/kategori/[slug]`, `/arama?q=&category=&minPrice=&maxPrice=&sort=&page=&pageSize=`, `/urun/[slug]`, `/giris`, `/kayit`, `/hesabim`, `/hesabim/adresler`, `/sepet`, `/checkout`, `/siparisler`, `/siparisler/[id]`, `/status`, `/gorsel-kaynaklari`. `src/proxy.ts` kategori ve ürün yokluğunu gerçek API'den stream öncesi kontrol eder ve HTTP 404 üretir. API erişilemezse sayfa hata durumu gösterir.
- `AuthForm` + `AccountPanel` → `auth-client.ts` → same-origin rewrite → Identity. Her mutasyondan önce yeni CSRF token'ı alınır; HttpOnly auth cookie'sini JS okumaz. Hesap verisi yalnız browser'da `no-store` `/me` ile alınır; HTML/RSC içine kişisel bilgi gömülmez. Kayıt butonu hydration tamamlanana kadar devre dışıdır.
- Node `server.mjs`, Next'ten önce socket peer IP'sinden forwarding başlıklarını üretir. `WEB_TRUSTED_PROXIES` varsayılan boş; exact-IP zinciri yalnız yetkili ingress'ten işlenir. API mevcut `KnownProxies` + `ForwardLimit=1` ile bu doğrulanmış tek istemci IP'sini kullanır. Next routing/rewrite aynı kalır; standalone output yoktur (D-042).
- `lib/cart.ts` istemci sepet deposudur: nesil sayacı eski kimliğin ve eski GET'in yanıtını yok sayar, ağ/5xx hatasında sepeti yeniden okur. `lib/api.ts` her yazmadan önce taze CSRF token'ı alır. Tutar hesaplanmaz, API'nin kuruş değerleri gösterilir. `lib/safe-next.ts` açık yönlendirmeyi engeller. `server/privacy.mjs` özel rotalara `private, no-store` ekler.
- `lib/orders.ts` sipariş uçlarını, `lib/order-key.ts` Idempotency-Key niyetini yönetir: anahtar `sessionStorage`'da kullanıcı + adres + quote'a bağlıdır, istekten önce "sonuç bilinmiyor" olarak işaretlenir. Sonuç bilinmeyen durumda yeni anahtar üreten yol kapanır, tek seçenek aynı anahtarla yeniden göndermektir (D-066). Sipariş sayfaları tarayıcıda okunur; kişisel veri HTML/RSC'ye girmez.
- UI testleri: Playwright + axe, 1440/768/390. `main` projesi paralel, fiyat/stok değiştiren `mutating` projesi ondan sonra, stok tüketen `ordering` projesi en son, ikisi de tek dosyada seri koşar.

## Güvenlik sınırları
- Sırlar yalnızca ortam değişkenlerinde (`.env`, Git dışı). Repoda yalnızca `.env.example` bulunur.
- Faz 3 public okuma uçları; Faz 4 cookie auth, CSRF ve rol policy'leri. Web bu uçlara bağlıdır (D-041); admin CRUD Faz 8 ile gelmiştir (aşağıdaki Faz 8 bölümü). E-posta doğrulama/parola sıfırlama yoktur.
- MVP'de (Faz 0–9) gerçek ödeme yok. Faz 7 siparişi ödeme almadan oluşturur; gerçek ödeme entegrasyonu sonraki sürüme aittir. Arayüz hiçbir zaman ödeme alınmış izlenimi vermez.

## Faz 9 Üretim-eşdeğeri dağıtım (D-077–D-083)

`edge (Caddy, TLS) → web (node server.mjs) → api (ASP.NET, Production) → db (PostgreSQL 17)`; kod: `deploy/` (Dockerfile.api hedefleri `api`/`schema`/`migrate`, Dockerfile.web, compose.yml, Caddyfile, db/*, scripts/*). `backend` ağı internal; sabit IP'ler güvenilir-proxy listelerini tam adresle yazmayı sağlar. Veritabanı rolleri `ssc_owner` (göç), `ssc_app` (çalışma, en az yetki), `ssc_backup` (okuma). API tarafı: `backend/src/SSCommerce.Api/Observability/**` (güvenlik başlıkları, `X-Request-Id`, `System.Diagnostics.Metrics`, iç metrik portu), `Security/ProductionConfigurationValidator.cs` (Production/Staging fail-fast), `Ordering:Enabled` sunucu anahtarı. Web tarafı: `apps/web/server/{handler,security,csp,observability,health,shutdown}.mjs`. Ayrıntı: `docs/DEPLOYMENT.md`, `docs/OPERATIONS.md`, `docs/SECURITY_REVIEW_PHASE_9.md`.

## Altyapı
- `docker-compose.yml`: PostgreSQL 17, healthcheck, `sscommerce-pgdata` named volume, host portu `5433` (bkz. DECISIONS D-007).
- CI tanımı .NET build/test ve web lint/typecheck/build, HTTP ingress, gerçek DB/API Playwright, proxy rate-limit kontrollerini içerir. E2eHost test DB'sini normal teardown'da temizler. Uzak çalıştırma hâlâ faturalandırma nedeniyle BLOCKED_EXTERNAL; yerel sonuçlar PROGRESS'tedir.

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
| `tests/SSCommerce.Api.Tests` | `WebApplicationFactory` ile gerçek HTTP testleri | Api |

Bilinçli olarak eklenmeyenler: MediatR/CQRS, generic repository, event bus. Gerektikleri gün gerekçesiyle eklenir.

### Faz 1 uçları
- `GET /health/live`: Süreç ayakta mı? Veritabanına bakmaz, her zaman 200 döner.
- `GET /health/ready`: PostgreSQL'e gerçekten bağlanır (`SELECT 1`). Başarısızsa **503** döner, hatayı gizlemez.
- `GET /openapi/v1.json`: OpenAPI belgesi (yalnızca Development).
- Hatalar RFC 9457 `ProblemDetails` biçiminde döner (`application/problem+json`).

## Veri kuralları
- Para: domain'de C# `decimal`, veritabanında `numeric(18,2)`. API'de **kuruş cinsinden tam sayı** (`priceMinor: 129900` = 1.299,00 TL).
- Zaman: kalıcı veride UTC (`timestamptz`).
- Gelecek model (Faz 3, 5, 6): Product → ProductVariant/SKU → Warehouse/InventoryBalance; siparişte fiyat snapshot'ı ve idempotency anahtarı. **Faz 1'de oluşturulmadı.**

## Frontend (`apps/web`)
- Next.js App Router + React + TypeScript + Tailwind CSS. Tek web uygulaması; `/admin` aynı uygulamada açılır (Faz 8).
- Faz 2 ürün verisi `src/content/demo-catalog.ts` altındaki **demo fixture'larıdır**, backend kataloğu değildir. Fiyatlar kuruş (`priceMinor`) olarak tutulur ve yalnızca biçimlendirilir; hesap yapılmaz.
- `src/components/`: Header, SearchForm, CategoryNav, MobileNav (tek client bileşen), Hero, CategoryCard, ProductCard, CollectionSection, TrustSection, Footer. Token'lar `globals.css` içindeki `@theme` bloğunda.
- Rotalar: `/`, `/kategori/[slug]` (Faz 3'e kadar "yakında"), `/arama?q=` (yakında), `/status` (backend health). `src/proxy.ts` bilinmeyen kategori için 404 üretir (D-014).
- UI testleri: Playwright + axe (`tests/e2e/home.spec.ts`), 1440/768/390.

## Güvenlik sınırları
- Sırlar yalnızca ortam değişkenlerinde (`.env`, Git dışı). Repoda yalnızca `.env.example` bulunur.
- Faz 1–3'te kimlik doğrulama yok, bu yüzden yazma ucu da yok. Auth ve CSRF Faz 4'te gelir.
- Ödeme Faz 7'ye kadar yok; arayüz ödeme alınmış izlenimi vermez.

## Altyapı
- `docker-compose.yml`: PostgreSQL 17, healthcheck, `sscommerce-pgdata` named volume, host portu `5433` (bkz. DECISIONS D-007).
- CI: `.github/workflows/ci.yml` dosyası .NET build/test ve web lint/typecheck/build adımlarını çalıştırır.

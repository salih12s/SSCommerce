# Teknik Kararlar

Her karar: kimlik, tarih, durum, karar, gerekçe. Değişen karar silinmez; yeni sürümle `Yerine geçti` olarak işaretlenir.

| ID | Tarih | Durum | Karar | Gerekçe |
|---|---|---|---|---|
| D-001 | 2026-10-28 | Kabul | Backend ASP.NET Core 10 + EF Core 10 + PostgreSQL, REST + OpenAPI | Teknik sözleşme (GOAL §4) |
| D-002 | 2026-10-28 | Kabul | Frontend Next.js App Router + TypeScript + Tailwind, tek uygulama `apps/web`; `/admin` aynı uygulamada | Teknik sözleşme; iki ayrı frontend bakım yükü getirir |
| D-003 | 2026-10-28 | Kabul | Modüler monolit, sade 4 katman. MediatR/CQRS/generic repository/event bus yok | Erken soyutlama karmaşıklık getirir; ihtiyaç doğunca eklenir |
| D-004 | 2026-10-28 | Kabul | Para: domain `decimal`, DB `numeric(18,2)`, API kuruş cinsinden `long` (`...Minor`) | Kayan nokta hatası yok; API'de birim tek ve açık |
| D-005 | 2026-10-28 | Kabul | Kalıcı zaman UTC (`timestamptz`) | Saat dilimi karışıklığını önler |
| D-006 | 2026-10-28 | Kabul | Same-origin: Next.js `rewrites` `/api/backend/*` → API (`API_BASE_URL`) | CORS gerekmez; Faz 4 cookie auth'a uygun |
| D-007 | 2026-10-28 | Kabul | Docker PostgreSQL host portu `5433` | Geliştirici makinesinde yerel PostgreSQL 15 servisi 5432'yi kullanıyor; çakışmayı önler |
| D-008 | 2026-10-28 | Kabul | Faz 1'de business entity ve migration yok; `DbContext` boş, bağlantı `/health/ready` ile doğrulanır | Sahte/boş migration anlam taşımaz; ilk migration Faz 3'te Catalog ile gelir |
| D-009 | 2026-10-28 | Kabul | Hata modeli RFC 9457 `ProblemDetails` | ASP.NET Core yerleşik desteği; tek tip hata gövdesi |
| D-010 | 2026-10-28 | Kabul | Faz 2 ürün/kampanya verisi frontend'de demo fixture; görseller lisans sorunu olmayan yerel SVG/CSS placeholder | Gerçek katalog Faz 3; telifli görsel kullanılmaz |
| D-011 | 2026-10-28 | Kabul (kullanıcı kararı) | Marka yazımı her yerde `SSCommerce`. Eski vitrin yazımı `SSCommerse` kullanılmaz | Kullanıcı 2026-10-28'de seçti; repo, namespace ve vitrin metni aynı |
| D-012 | 2026-10-28 | Kabul | Solution biçimi `.slnx` | .NET 10 SDK destekliyor; sade XML |
| D-013 | 2026-10-28 | Kabul | API'de HTTPS yönlendirmesi yok; API Next.js proxy'sinin arkasında HTTP dinler | Same-origin (D-006). TLS, yayın fazında (Faz 9) ters proxy'de sonlanır |
| D-014 | 2026-10-28 | Kabul | Next.js 16 `cacheComponents` açık bırakıldı. `/status` için `connection()` + `Suspense`; bilinmeyen kategori slug'ı için `src/proxy.ts` gerçek 404 üretir | `cacheComponents` açıkken `dynamic`/`dynamicParams` kullanılamıyor ve akış başladıktan sonra `notFound()` 200 dönüyor. Slug listesinin tek kaynağı `demo-catalog`; Faz 3'te gerçek katalogla değişecek |
| D-015 | 2026-10-28 | Kabul | Tasarım dili: kömür `#16181b`, sıcak zemin `#f6f1e8`, vurgu turuncu `#ff5a14` (yalnızca zemin/çizim), metin turuncusu `#a63400` (AA 6:1). Fontlar Bricolage Grotesque / Instrument Sans / DM Mono. İmza: "ürün künyesi" (SSC kodu, mono teknik satır) | GOAL §7 görsel yönü; açık tema ana tema, bu fazda koyu tema yok |
| D-016 | 2026-10-28 | Kabul | Fontlar `next/font/google` ile build sırasında indirilip self-host edilir | Çalışma anında dış istek yok; build ağ ister (CI'da sorun değil) |
| D-017 | 2026-10-28 | Kabul | Kök `global.json`: SDK 10.0.100, `rollForward: latestFeature`; CI `global-json-file` kullanır | Yerel ve CI SDK'sı aynı çizgide kalır |
| D-018 | 2026-10-28 | Kabul | CI'da (`CI=true`) gerçek PostgreSQL testi atlanamaz, eksik yapılandırmada kalır | Sessiz skip yeşil CI'da yanlış güven verir (QA-F1) |
| D-020 | 2026-10-28 | Kabul (kullanıcı kararı) | MVP fazları: 3 Catalog, 4 Identity/Security, 5 Inventory, 6 Cart/Checkout, 7 Ordering (ödemesiz), 8 ERP/Admin, 9 Kalite ve deployment. Gerçek ödeme ve stok rezervasyonu MVP dışında | İlk ROADMAP sürümü ödemeyi Faz 7'ye, stok rezervasyonunu Faz 5'e koymuştu ve teknik planla çelişiyordu |
| D-019 | 2026-10-28 | Kabul | Yerel bağlantı dizesinde `Host=127.0.0.1` (localhost değil). Compose portu yalnızca 127.0.0.1'e bağlı kalır; ready zaman aşımı 3 sn kalır | Ölçüm: Windows'ta `::1:5433` reddi 2.657 ms sürüyor; `localhost` ile ilk ready 2.987 ms (bir koşuda 3.079 ms → 503), `127.0.0.1` ile 414/16/93 ms. Sorunun kaynağı adres çözümü, zaman aşımı değil |

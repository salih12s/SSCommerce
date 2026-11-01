# SSCommerce Yol Haritası

Tek satıcılı, fiziksel ürün satan, TRY ile çalışan teknoloji/yaşam tarzı mağazası. İlk sürümde gerçek ödeme alınmaz.

## Ana fazlar

| Faz | Başlık | Özet | Durum |
|---|---|---|---|
| 0 | Repository ve geliştirici hazırlığı | Git, temel dosyalar, araç envanteri, dokümantasyon | PASS (yerel) |
| 1 | Çalışan full-stack temel | ASP.NET Core 10 API, PostgreSQL + Docker Compose, Next.js iskeleti, CI | PASS (yerel); uzak CI BLOCKED_EXTERNAL |
| 2 | Storefront tasarım temeli | Tasarım token'ları, bileşenler, responsive ana sayfa (demo verisiyle) | PASS; tasarım 2026-10-29 onaylandı, korundu |
| 3 | Catalog | Product → ProductVariant/SKU, kategori, gerçek katalog API'si ve sayfaları; ilk EF Core migration | PASS (yerel), 2026-10-29; commit onayı bekliyor |
| 4 | Identity / Security | ASP.NET Core Identity, cookie oturumu, CSRF, rol modeli (müşteri/depo/yönetici) | PASS (yerel), 2026-10-29; commit onayı bekliyor |
| 5 | Inventory | Warehouse/InventoryBalance, atomik stok hareketleri, public stok görünürlüğü (stok **rezervasyonu yok**) | PASS (yerel), 2026-11-01; commit onayı bekliyor |
| 6 | Cart / Checkout | Sepet, adres ve teslimat bilgisi, salt okunur önizleme; sipariş ve ödeme adımı yok | PASS (yerel), 2026-11-01; commit onayı bekliyor |
| 7 | Ordering | Sipariş oluşturma, fiyat snapshot'ı, idempotency anahtarı, sipariş durumları. **Gerçek ödeme olmadan** | Planlandı |
| 8 | ERP / Admin | Aynı web uygulamasında `/admin`: katalog, stok ve sipariş yönetimi, ERP entegrasyon noktaları | Planlandı |
| 9 | Kalite ve deployment | Uçtan uca test kapsamı, performans, gözlemlenebilirlik, yedekleme, yayın (TLS ters proxy) | Planlandı |

### MVP dışı (sonraki sürümler)
- **Gerçek ödeme entegrasyonu** (ödeme sağlayıcısı, 3D Secure, iade akışları).
- **Stok rezervasyonu** (sepet/sipariş sırasında stok ayırma, süre aşımıyla serbest bırakma).

## Faz 0–2 görevleri

### Faz 0
- **F0.1** Envanter ve emniyet: Git durumu, remote, araç sürümleri.
- **F0.2** Temel dosyalar: `.gitignore`, `.editorconfig`, `README.md`, `docs/*`, `.env` dışlama kontrolü.

### Faz 1
- **F1.1** Backend solution: Api/Application/Domain/Infrastructure + testler; `/health/live`, `/health/ready`, OpenAPI, merkezi hata modeli.
- **F1.2** PostgreSQL + Docker Compose: healthcheck, named volume, Npgsql/EF Core bağlantısı.
- **F1.3** Next.js iskeleti (`apps/web`) ve backend health entegrasyonu (same-origin proxy).
- **F1.4** CI (`.github/workflows/ci.yml`) ve README'de PowerShell adımları.

### Faz 2
- **F2.1** Tasarım token'ları ve yeniden kullanılabilir bileşenler.
- **F2.2** Ana sayfa (hero, dört kategori, koleksiyonlar, ürün kartları, footer).
- **F2.3** Responsive (1440/768/390), erişilebilirlik, tarayıcı smoke testi, tüm build/test kontrollerinin yeniden çalıştırılması.

## Faz 3–4 görevleri (`GOAL_PHASES_3_4.md`)

- **F3.0–F3.5 PASS (yerel):** Keşif, şema/migration, public API, seed, web entegrasyonu ve kabul kapısı. Kanıt: PROGRESS 2026-10-29.
- **F4.0–F4.5 PASS (yerel):** Identity/migration, cookie/CSRF/policy, giriş/kayıt/hesap ekranları, bağımsız QA ve kabul kapısı. Backend 539/0/0, Playwright 81/0; proxy rate limit PASS.
- Git commit/push ayrıca kullanıcı onayı ister.

## Faz 5–6 görevleri (`GOAL_PHASES_5_6_MULTI_AGENT_EN.md`)

- **F5.1–F5.4:** Veri modeli, ekleyici migration, atomik stok işlemleri, admin API ve dürüst public stok. Sözleşme: `docs/CONTRACT_PHASES_5_6.md`.
- **F6.1–F6.5:** Kullanıcı başına sepet, sepet API'si, teslimat adresleri, salt okunur checkout önizlemesi, `/sepet`, `/checkout`, `/hesabim/adresler`. Sipariş, ödeme ve rezervasyon yok; bunlar Faz 7 ve sonrası.

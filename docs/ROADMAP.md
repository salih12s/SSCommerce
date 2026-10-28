# SSCommerce Yol Haritası

Tek satıcılı, fiziksel ürün satan, TRY ile çalışan teknoloji/yaşam tarzı mağazası. İlk sürümde gerçek ödeme alınmaz.

## Ana fazlar

| Faz | Başlık | Özet | Durum |
|---|---|---|---|
| 0 | Repository ve geliştirici hazırlığı | Git, temel dosyalar, araç envanteri, dokümantasyon | Bu hedefte |
| 1 | Çalışan full-stack temel | ASP.NET Core 10 API, PostgreSQL + Docker Compose, Next.js iskeleti, CI | Bu hedefte |
| 2 | Storefront tasarım temeli | Tasarım token'ları, bileşenler, responsive ana sayfa (demo verisiyle) | Bu hedefte |
| 3 | Catalog | Product → ProductVariant/SKU, kategori, gerçek katalog API'si ve sayfaları; ilk EF Core migration | Planlandı |
| 4 | Identity / Security | ASP.NET Core Identity, cookie oturumu, CSRF, rol modeli (müşteri/yönetici) | Planlandı |
| 5 | Inventory | Warehouse/InventoryBalance, stok sorgulama ve görünürlüğü (stok **rezervasyonu yok**) | Planlandı |
| 6 | Cart / Checkout | Sepet, adres ve teslimat bilgisi, sipariş özeti; ödeme adımı yok | Planlandı |
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

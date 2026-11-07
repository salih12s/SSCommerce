# SSCommerce Yol Haritası

Tek satıcılı, fiziksel ürün satan, TRY ile çalışan teknoloji/yaşam tarzı mağazası. İlk sürümde gerçek ödeme alınmaz.

## Ana fazlar

| Faz | Başlık | Özet | Durum |
|---|---|---|---|
| 0 | Repository ve geliştirici hazırlığı | Git, temel dosyalar, araç envanteri, dokümantasyon | PASS (yerel) |
| 1 | Çalışan full-stack temel | ASP.NET Core 10 API, PostgreSQL + Docker Compose, Next.js iskeleti, CI | PASS (yerel); uzak CI BLOCKED_EXTERNAL |
| 2 | Storefront tasarım temeli | Tasarım token'ları, bileşenler, responsive ana sayfa (demo verisiyle) | PASS; tasarım 2026-10-29 onaylandı, korundu |
| 3 | Catalog | Product → ProductVariant/SKU, kategori, gerçek katalog API'si ve sayfaları; ilk EF Core migration | PASS (yerel), 2026-10-29; commit onayı bekliyor |
| 4 | Identity / Security | ASP.NET Core Identity, cookie oturumu, CSRF, rol modeli (müşteri/depo/yönetici) | PASS (yerel), 2026-10-29; `40f030b`, `feat/phase-3-4-complete` dalında |
| 5 | Inventory | Warehouse/InventoryBalance, atomik stok hareketleri, public stok görünürlüğü (stok **rezervasyonu yok**) | PASS (yerel), 2026-11-01; `40f030b`, `feat/phase-3-4-complete` dalında |
| 6 | Cart / Checkout | Sepet, adres ve teslimat bilgisi, salt okunur önizleme; sipariş ve ödeme adımı yok | PASS (yerel), 2026-11-01; `40f030b`, `feat/phase-3-4-complete` dalında |
| 7 | Ordering | Sipariş oluşturma, fiyat snapshot'ı, idempotency anahtarı, sipariş durumları. **Gerçek ödeme olmadan** | PASS (yerel), 2026-11-02; `feat/phase-3-4-complete` dalında; uzak CI BLOCKED_EXTERNAL |
| 8 | ERP / Admin | Aynı web uygulamasında `/admin`: katalog, stok ve sipariş yönetimi, ERP entegrasyon noktaları | VERIFIED, 2026-11-07; dev veritabanına AddAdminOperations uygulandı; `c8bba63` push edildi; main birleştirmesi onay bekliyor; uzak CI BLOCKED_EXTERNAL |
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

## Faz 7 görevleri (`GOAL_PHASE_7_ORDERING_EN.md`)

- **F7.1:** `AddOrdering` migration'ı: `orders`, `order_items`, `order_inventory_allocations`, `order_status_history`, değişmezlik trigger'ları. Sözleşme: `docs/CONTRACT_PHASE_7.md`.
- **F7.2:** Tek transaction'da sipariş: idempotency, quote ile fiyat onayı, depolara dağıtım, stok düşümü, snapshot, sepet temizliği.
- **F7.3:** Durum makinesi, müşteri ve Admin iptali, özgün depoya tek seferlik iade, minimal personel uçları.
- **F7.4:** `/checkout` sipariş akışı, `/siparisler`, `/siparisler/[id]`. Ödeme, kargo tutarı, genel toplam ve otomatik stok serbest bırakma yok.

## Faz 8 görevleri (`GOAL_PHASE_8_ERP_ADMIN_EN.md`, sözleşme `docs/CONTRACT_PHASE_8.md`)

- **F8.1:** `AddAdminOperations` migration'ı: `version` (iyimser eşzamanlılık), append-only `admin_audit_log`, kategori ağacı ve sipariş durum trigger'ları, kısmi indeksler. Yalnız ekleyici.
- **F8.2:** Katalog yönetimi: kategori, ürün, varyant, fiyat, aktiflik, görsel; sürüm çakışması 409, kilit zaman aşımı/deadlock 503 `busy`, her yazma denetim kaydı bırakır.
- **F8.3:** Stok ve depo yönetimi (Faz 5 uçları korunur), hareketlerde işlemi yapan kişi, düşük stok ve kalıntı raporları.
- **F8.4:** Sipariş yönetimi: personel listesi/ayrıntısı, iptalde tek seferlik stok iadesi, Admin'e özel denetimli teslimat görüntüleme.
- **F8.5:** Ödenmemiş sipariş sınırları (açık ≤ 3, 24 saatte ≤ 10), sepet kilidi altında. Üretim eşikleri `PRODUCTION_ORDERING_POLICY_PENDING`.
- **F8.6:** Pano ve raporlar: dürüst göstergeler (ödenmemiş ara toplam, ciro değil).
- **F8.7:** Rol yönetimi (hesap kimliğiyle), son-Admin koruması, oturum iptali; ilk Admin yalnız yerel `--bootstrap-admin` komutuyla.
- **F8.8:** `/admin` arayüzü (11 rota), vitrinin `(shop)` route group'una taşınması, Playwright + axe.
- **Faz 8 dışı:** Warehouse'a teslimat verisi (D-071, sahip kararı), gerçek ödeme/iade, `/admin` için sıkı CSP, ERP dış entegrasyonları (Faz 9+).

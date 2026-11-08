# SSCommerce sürüm notları ve yayın hazırlık durumu

**Durum:** yayına HAZIR DEĞİL; yalnızca yerel üretim-eşdeğeri yığında doğrulandı (`LOCAL_PRODUCTION_EQUIVALENT_PASS` kapsamı için bkz. `docs/PHASE_9_VERIFICATION.md`). Kod `feat/phase-3-4-complete` dalına push edildi (2026-11-08, `919c7a9` + docs commit'i); herkese açık bir yayın, sürüm etiketi, PR ya da `main` birleştirmesi **yok**; hepsi sahip onayı bekler. Önerilen sürüm adı yayın anında sahip tarafından seçilir (`SSC_VERSION`).

## Uygulama NE YAPAR

Tek satıcılı, fiziksel ürünlü, TRY para birimli bir mağaza için **ödenmemiş sipariş kayıtları** ve operasyon yönetimi:

- **Vitrin:** kategori ağacı, ürün/varyant listeleme ve arama, ürün detayı, sepet, teslimat adresleri, checkout önizleme, hesap (kayıt/giriş/çıkış).
- **Sipariş (ödenmemiş operasyon kaydı):** idempotent sipariş verme (`Idempotency-Key`), fiyat/stok anlık görüntüsü, stok sipariş anında düşer, müşteri ve Admin iptali stoğu geri verir, durum akışı (`placed → processing → shipped → completed` / `cancelled`), hesap başına açık sipariş ve günlük sipariş sınırı.
- **Yönetim (`/admin`, 11 rota):** katalog (kategori/ürün/varyant, sürüm denetimli), stok hareketleri, siparişler, personel rolleri (Admin / Warehouse), depo, raporlar ("ödenmemiş sipariş ara toplamı"), denetim günlüğü.
- **İşletim:** HTTPS kenarı, güvenlik başlıkları + CSP, korelasyon kimliği, iç metrikler, sağlık uçları, yedek/geri yükleme ve yükseltme betikleri, en az yetkili veritabanı rolleri (bkz. `docs/DEPLOYMENT.md`, `docs/OPERATIONS.md`).

## Uygulama NE YAPMAZ (tasarım gereği)

- **Ödeme yok:** ödeme sağlayıcısı, tahsilat, iade, `paid` alanı, ciro/gelir göstergesi, genel toplam yok. Siparişler **ödenmemiş operasyon kayıtlarıdır**; arayüz bunu söyler. (`shippingMinor` ve `grandTotalMinor` null.)
- **Kargo ücreti ve vergi hesaplanmaz.**
- **Stok rezervasyonu otomatik sona ermez:** ödenmemiş sipariş stoğu tutar; serbest bırakma yalnız elle iptalle olur (D-061). Hesap başına sınırlar (D-072) riski azaltır, ortadan kaldırmaz.
- **E-posta doğrulama, parola sıfırlama, ERP dış entegrasyonu, çok satıcı, çok para birimi yok.**
- **Warehouse rolü müşteri teslimat verisini göremez** (D-071). Kargoya hazırlık için gerekirse bu ayrı bir sahip kararıdır.

## Yayın öncesi sahip kararları ve riskleri (açık)

| Konu | Durum |
|---|---|
| Herkese açık sipariş açılsın mı? (D-061 stok tutma, ödeme yokluğu, otomatik süre dolumu yokluğu) | **`PENDING_OWNER_APPROVAL`.** Dağıtım varsayılanı `ORDERING_ENABLED=false` (sunucuda 503). |
| Üretim sipariş eşikleri (açık 3 / günlük 10) | `PRODUCTION_ORDERING_POLICY_PENDING` (D-072). Sessizce onaylanmadı. |
| Warehouse'a teslimat verisi | D-071: **verilmedi**; ayrı karar. |
| Barındırma, alan adı, DNS, herkese açık CA sertifikası | `BLOCKED_EXTERNAL` / `PENDING_OWNER_ACTION`. |
| Uzak CI (GitHub Actions) | `BLOCKED_EXTERNAL` (hesap faturalandırma kilidi; yerelde `actionlint` + yerel eşdeğer koşular). |
| Data Protection anahtarları şifresiz XML | Volume şifrelemesi/erişim kısıtı sahip eylemi. |
| `style-src-attr 'unsafe-inline'` | Düşük risk; bileşenlerde `style=""` öznitelikleri sınıfa çevrilirse kalkar. |

## Faz 9 değişiklik özeti

Eklenenler: üretim imajları ve compose yığını (`deploy/`), Caddy TLS kenarı, API ve web güvenlik başlıkları + CSP (nonce/hash), korelasyon kimliği ve iç metrikler, sunucu taraflı sipariş anahtarı (`Ordering__Enabled`), Production/Staging için fail-fast yapılandırma denetimi, web `/healthz` `/readyz` ve zarif kapanış, `API_BASE_URL` derleme zorunluluğu, veritabanı rolleri + göç işi + yedek/geri yükleme/yükseltme provası betikleri, CI imaj derleme işi. Kayıtlı kararlar: D-077 sonrası (`docs/DECISIONS.md`).

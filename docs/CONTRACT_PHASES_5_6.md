# Faz 5–6 sözleşmesi (Inventory + Cart/Checkout)

Ana ajanın sabitlediği şema ve API sözleşmesi. Alt ajanlar buna uyar; değişiklik gerekiyorsa uygulamadan önce ana ajana bildirir. Kararların gerekçesi `docs/DECISIONS.md` D-045–D-054.

## Ortak kurallar

- Para API'de kuruş (`long`, `...Minor`), domain'de `decimal`, DB'de `numeric(18,2)`. Zaman UTC, `timestamptz`.
- Tüm uçlar `/api/v1` grubundan geçer (Origin + CSRF otomatik). GET ile durum değişmez. MVC controller yok.
- Kullanıcı kimliği yalnız kimlik doğrulanmış principal'den gelir; gövde ya da rotadan alınmaz.
- Hatalar `ApiResults.Problem` + `ApiErrorCodes` ile RFC 9457. İstisna metni yanıta ya da loga yazılmaz. E-posta, adres, telefon ve sepet içeriği loglanmaz, URL'ye konmaz.
- JSON gövdesi bozuk/yanlış tipli ise 400 `validation_failed`. Gövde üst sınırı 16 KB (413 değil, `AuthEndpoints` deseni).
- Bilinmeyen gövde alanları (`userId`, `priceMinor`, `subtotalMinor`, `discount`) yok sayılır; hiçbir etkisi olamaz. Bunu test kanıtlar.
- Yetkisiz: 401 `unauthenticated`, yetkisi yok: 403 `forbidden`. Başka kullanıcının kaynağı 404 (403 değil, var olduğunu sızdırmaz).
- `Cache-Control: no-store`: `/api/v1/cart`, `/api/v1/addresses`, `/api/v1/checkout` eklenir (`/auth` ve `/admin` zaten var).
- Yeni rate limit policy: `mutation` (IP başına, varsayılan 120/dk, `Security:RateLimit:MutationPermitLimit`). Stok, sepet, adres yazmaları ve preview bunu kullanır. Limiter kimlik doğrulamadan önce çalıştığı için kullanıcı başına değil IP başınadır (D-053).

## Faz 5 şeması

`warehouses` (`AuditedEntity`): `id`, `code` varchar(32) unique, biçim `^[A-Z0-9][A-Z0-9-]{1,31}$`; `name` varchar(100) boş olamaz; `is_active`; `created_at`, `updated_at`.

`inventory_balances`: `id` uuid PK; `warehouse_id` FK RESTRICT; `product_variant_id` FK RESTRICT; `on_hand_quantity` int CHECK `0..1000000`; `version` bigint CHECK `>= 1`; `created_at`, `updated_at`. Unique: (`warehouse_id`, `product_variant_id`). Ek index: `product_variant_id` (public toplam için).

`stock_movements` (değişmez): `id` uuid PK; `warehouse_id` FK RESTRICT; `product_variant_id` FK RESTRICT; `sku` varchar (o anki SKU kopyası); `quantity_delta` int CHECK `<> 0` ve `-1000000..1000000`; `operation_type` varchar(32) CHECK `IN ('receiving','adjustment','correction')`; `actor_user_id` uuid FK `AspNetUsers` RESTRICT; `reason` varchar(500) boş olamaz; `reference` varchar(128) null; `operation_key` varchar(128) unique; `request_fingerprint` char(64) (kanonik isteğin SHA-256 hex'i); `on_hand_after` int CHECK `>= 0`; `occurred_at` timestamptz.
Index: (`warehouse_id`, `occurred_at`), (`product_variant_id`, `occurred_at`). `UPDATE` ve `DELETE` DB trigger'ıyla reddedilir (`ck` değil trigger; migration `Down` trigger'ı da kaldırır).

Domain kuralları `SSCommerce.Domain.Inventory.InventoryRules`: `MaxOnHandQuantity = 1_000_000`, `MaxAbsoluteDelta = 1_000_000`, `PublicAvailabilityCap = 20`. Sabitler CHECK ifadelerinde de kullanılır.

## Faz 5 API (hepsi `/api/v1/admin` altında, `AdminOnly`)

| Uç | Davranış |
|---|---|
| `GET /admin/warehouses` | Aktif/pasif tüm depolar |
| `POST /admin/warehouses` `{code,name}` | 201; kod tekrar 409 `warehouse_code_taken` |
| `PATCH /admin/warehouses/{id}` `{name?,isActive?}` | 200; yok 404 `warehouse_not_found` |
| `GET /admin/inventory/balances?warehouseId=&sku=&page=&pageSize=` | Sayfalı bakiye: warehouse kodu, variantId, sku, onHandQuantity, version, updatedAt |
| `GET /admin/inventory/movements?warehouseId=&variantId=&page=&pageSize=` | Sayfalı, en yeni önce |
| `POST /admin/inventory/receive` `{warehouseId,variantId,quantity,reason,reference?}` | `quantity` 1..MaxAbsoluteDelta, tür `receiving` |
| `POST /admin/inventory/adjust` `{warehouseId,variantId,quantityDelta,type,reason,reference?}` | `quantityDelta` ≠ 0, `type` = `adjustment` ya da `correction` |

Receive/adjust için `Idempotency-Key` başlığı zorunlu: `^[A-Za-z0-9_-]{16,128}$`; eksik ya da bozuksa 400 `validation_failed`.

Yanıt (`StockMovementDto`): `id, warehouseId, variantId, sku, quantityDelta, operationType, actorUserId, reason, reference, onHandAfter, occurredAt, replayed`. Yalnız Admin görür. İlk uygulama 201 (`replayed:false`); aynı anahtar + aynı içerik 200 (`replayed:true`, ilk sonuç, bakiye değişmez); aynı anahtar + farklı içerik 409 `idempotency_key_reused`.

Metin alanlarında (`name`, `reason`, `reference`) kontrol karakteri 400 `validation_failed`. Idempotency anahtarı küreseldir, aktöre bağlı değildir (D-055).

Durum hataları: yok 404 `warehouse_not_found` / `variant_not_found`; pasif 409 `warehouse_inactive` / `variant_inactive`; negatife düşürme ya da üst sınırı aşma 409 `insufficient_stock` / `stock_limit_exceeded`. Reddedilen istek anahtarı tüketmez ve hareket bırakmaz.

Atomik akış (tek transaction, READ COMMITTED): (1) `pg_advisory_xact_lock(hashtextextended(key,0))`, aynı anahtarlı istekleri sıraya sokar; (2) anahtar varsa parmak izi karşılaştır, replay ya da 409; (3) depo ve varyantı oku, aktiflik; (4) koşullu tek SQL: `delta > 0` için `INSERT … ON CONFLICT DO UPDATE … WHERE on_hand + delta <= max RETURNING`, `delta < 0` için `UPDATE … WHERE on_hand + delta >= 0 RETURNING`; 0 satır → rollback + 409; (5) hareketi ekle; (6) commit. CHECK ve unique index son savunma hattıdır. Okuma-değiştirme-yazma yok. `SET LOCAL lock_timeout = '5s'`; zaman aşımı 503 `stock_busy`.

## Public stok görünürlüğü (F5.4)

Tanım: `available(variant)` = `SUM(on_hand)` yalnız **aktif** depolarda; `availableQuantity` = `min(available, PublicAvailabilityCap)`. Bakiye satırı yoksa 0. Depo kırılımı, kodu ya da adı public yanıtlarda asla yer almaz; cap üstü gerçek miktar açıklanmaz.

- `ProductVariantDto` eklenen alanlar: `id`, `inStock`, `availableQuantity`.
- `ProductDetailDto` ve `ProductListItemDto` eklenen alan: `inStock` (herhangi bir aktif varyant stokta).
- Stok filtresi yok; stoksuz ürün listede kalır ve "Stokta yok" yazar. Demo katalog için stok seed edilmez (D-047).
- Liste uçlarında stok tek sorguda hesaplanır (N+1 yok).

## Faz 6 şeması

`carts` (`AuditedEntity`): `id`, `user_id` FK `AspNetUsers` CASCADE, unique (kullanıcı başına tek aktif sepet).

`cart_items` (`AuditedEntity`): `id`, `cart_id` FK CASCADE, `product_variant_id` FK RESTRICT, `quantity` int CHECK `1..20`, `unit_price_at_add` numeric(18,2) CHECK `>= 0`. Unique (`cart_id`, `product_variant_id`).
`unit_price_at_add` sunucunun o anki katalog fiyatıdır; **tutar hesabına girmez**, yalnız "fiyat değişti" bilgisini vermek içindir. Satır adedi her değiştiğinde güncel fiyatla yenilenir.

`customer_addresses` (`AuditedEntity`): `id`, `user_id` FK CASCADE, `label` varchar(50), `recipient_name` varchar(100), `phone` char(10) CHECK `^5[0-9]{9}$`, `province` varchar(32), `district` varchar(64), `street_address` varchar(300), `postal_code` char(5) null CHECK `^(0[1-9]|[1-7][0-9]|8[01])[0-9]{3}$`, `is_default` bool. Partial unique (`user_id`) `WHERE is_default`. Index (`user_id`).

Domain kuralları: `CartRules.MaxLineQuantity = 20`, `CartRules.MaxDistinctLines = 50`, `AddressRules.MaxAddressesPerUser = 10`. İl, 81 ilin sabit listesinden olmalı (`AddressRules.Provinces`, DB CHECK yok). Telefon `5XXXXXXXXX`; API `0` ya da `+90` önekini ve boşlukları normalize eder. Metin alanlarında kontrol karakteri reddedilir, baş/son boşluk kırpılır.

## Faz 6 API

Sepet: grup `RequireAuthorization()` (D-051: Customer/Warehouse/Admin kendi sepetini kullanabilir).

| Uç | Davranış |
|---|---|
| `GET /cart` | 200 `CartDto`. Sepet yoksa boş sepet (satır oluşturmaz) |
| `POST /cart/items` `{variantId,quantity}` | Sepet yoksa oluşturur; varyant varsa adedi **artırır**. 200 `CartDto` |
| `PATCH /cart/items/{itemId}` `{quantity}` | Mutlak adet 1..20. `0` ya da negatif 400 (silme için DELETE). 200 `CartDto` |
| `DELETE /cart/items/{itemId}` | 200 `CartDto`. Başkasının ya da olmayan satır 404 `cart_item_not_found` |

`CartDto`: `items[]`, `itemCount` (adetler toplamı), `subtotalMinor`, `currency`, `hasIssues`.
`CartItemDto`: `id, variantId, sku, productSlug, productName, variantName, imageUrl?, imageAlt?, quantity, unitPriceMinor?, lineSubtotalMinor?, previousUnitPriceMinor?, priceChanged, availableQuantity, status`.
`status`: `available` | `insufficient_stock` (stok>0 ama adet fazla) | `out_of_stock` | `unavailable` (varyant/ürün/kategori pasif). `unavailable` satırda fiyat alanları `null` ve ara toplama girmez. Diğerleri güncel fiyatla girer. `previousUnitPriceMinor` yalnız fiyat farklıysa dolu.

Hatalar: varyant yok 404 `variant_not_found`; varyant, ürün ya da kategori pasif 409 `variant_inactive`; adet artışı stoktan fazlaysa 409 `insufficient_stock` (`availableQuantity` extension'lı, cap'li); stok 0 ise de aynı kod. Adet azaltmak stok az olsa da serbest. Satır adedi sınırı 409 `cart_quantity_limit`; satır sayısı sınırı 409 `cart_line_limit`.

Eşzamanlılık: her sepet yazması transaction içinde `SELECT … FROM carts WHERE user_id = @u FOR UPDATE` ile kullanıcı başına sıralanır; ilk oluşturma `INSERT … ON CONFLICT (user_id) DO NOTHING`. Unique index son savunma hattıdır. Sepet stok ayırmaz, stok düşmez.

Adresler: `/addresses` grubu `RequireAuthorization()`.

| Uç | Davranış |
|---|---|
| `GET /addresses` | Kullanıcının adresleri, varsayılan önce |
| `POST /addresses` | 201. İlk adres otomatik varsayılan. Sınır 409 `address_limit` |
| `PUT /addresses/{id}` | 200, tam güncelleme (`isDefault` hariç) |
| `POST /addresses/{id}/default` | 200 |
| `DELETE /addresses/{id}` | 204. Varsayılan silinirse en son güncellenen adres varsayılan olur |

`AddressDto`: `id, label, recipientName, phone, province, district, streetAddress, postalCode, isDefault`. Başkasının adresi 404 `address_not_found`. Adres yazmaları iki argümanlı `pg_advisory_xact_lock(int4, int4)` ile kullanıcı başına sıralanır; bu biçim stok anahtarlarının tek argümanlı kilit alanıyla çakışmaz. Partial unique index son savunma hattıdır.

Checkout preview:

`POST /checkout/preview` `{addressId}` (auth + CSRF). Sipariş, ödeme, rezervasyon yaratmaz; stok ve sepet değişmez.

Yanıt: `lines[]` (`CartItemDto` ile aynı), `address` (`AddressDto`), `itemCount`, `subtotalMinor`, `shippingMinor: null`, `shippingStatus: "shippingNotCalculated"`, `grandTotalMinor: null`, `currency`, `canProceed` (tüm satırlar `available` ise true), `issues[]` (`{code, itemId?}`), `orderingAvailable: false`, `calculatedAt`.
Hatalar: boş sepet 409 `cart_empty`; adres yok/başkasının 404 `address_not_found`. Satır sorunları hata değildir: 200 + `canProceed:false`.

Tutar taşması: ara toplam `Money` üst sınırını aşarsa 409 `cart_total_too_large`, 500 değil (D-056).

Sepet ve adres yazmalarında kilit zaman aşımı (5 sn) 503 `busy`, hiçbir şey yazılmaz. Metin alanlarında Unicode Cc, Cf, Zl ve Zp karakterleri ham girdi üzerinde 400 `validation_failed`.

## Ortak hata davranışı (F6 kapısı)

Veritabanına bağlantı düzeyinde ulaşılamazsa bütün API yolları 503 `service_unavailable` + `Retry-After: 5` döner (`DatabaseUnavailableMiddleware`); gövdede host, port ya da ileti yoktur. Sıradan SQL ve uygulama hataları 500 kalır. `POST /cart/items` idempotency anahtarı taşımaz; istemci ağ/5xx hatasında sepeti yeniden okur (D-057).

## E2E fixture (yalnız `backend/tools/SSCommerce.E2eHost`)

Üretim kodunda test ucu yoktur. Host, test DB'sinde `E2E-DEPO` deposunu ve SKU sırasına göre belirlenimli stok kurar (sıra numarası mod 4: 0 → stok yok, 1 → 3 adet, 2 → 12 adet, 3 → 50 adet; cap üstü dahil her durum görünür olur). Bu test fixture'ıdır, gerçek stok diye sunulmaz.

Fiyat, stok ve aktiflik değiştiren senaryolar için host, kendi sürecinde yalnız loopback'e bağlı küçük bir kontrol ucu açar (`127.0.0.1:5082`): `POST /fixture/stock {sku, quantity}`, `POST /fixture/price {sku, priceMinor}`, `POST /fixture/active {sku, isActive}`. API sürecinde böyle bir uç yoktur. Bu değişiklikleri yapan spec'ler diğer testlerin okuduğu veriyi bozmamak için Playwright'ta ayrı bir projede, diğerleri bittikten sonra tek worker ile koşar.

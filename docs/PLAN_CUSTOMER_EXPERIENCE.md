# Müşteri deneyimi paketi (MD) — plan ve sözleşme

Tarih: 2026-11-08. Kullanıcı isteği: "Favorilerim yok, profil eksik, detay sayfaları geliştirilmeli; sırayla hepsine başlayalım."
Kurallar değişmez: para API'de kuruş (`long`), zaman UTC, iş mantığı yalnız backend'de, kişisel veri HTML/RSC'ye girmez (tarayıcıda `no-store` okunur), yazmalar CSRF + `mutation` hız sınırı, sahiplik yalnız cookie'deki kullanıcıdan. Frontend yapısı D-087 (`features/<özellik>/`, ESLint sınırları).

| Adım | Kapsam | Ajanlar | Durum |
|---|---|---|---|
| MD-1 | Favoriler (uçtan uca) | database-dev → backend-dev ∥ frontend-dev → qa-reviewer | **LOCAL_PASS** (2026-11-08) |
| MD-2 | Profil: hesap özeti, ortak hesap menüsü, parola değiştirme | backend-dev ∥ frontend-dev → qa-reviewer | **LOCAL_PASS** (2026-11-08) |
| MD-3 | Detay sayfaları: ürün galerisi, benzer ürünler, teslimat/iade bloğu, mobil sabit sepete ekle; sipariş detayında durum zaman çizelgesi | frontend-dev | **LOCAL_PASS** (2026-11-08) |
| MD-4 | Yasal sayfalar (Hakkımızda, İletişim, KVKK, Mesafeli satış, çerez) | frontend-dev | **içerik sahibi bekliyor** |

Her adım sonunda kullanıcıya rapor; adımlar sırayla, bir önceki doğrulanmadan sonraki başlamaz.

## Varsayılan kararlar (kullanıcı aksini söyleyene kadar)

- **D-088 Favoriler yalnız giriş yapmış kullanıcıda, sunucuda.** Misafir favorisi (tarayıcıda tutup girişte birleştirme) sonraki iş. Misafir kalbe basarsa `/giris?next=<şu anki sayfa>`.
- **D-089 Ad soyad alanı eklenmez** (D-032 korunur: gereksiz kişisel veri yok). Profil e-posta + üyelik tarihi + özetlerle zenginleşir.
- **D-090 Parola değiştirilince diğer oturumlar kapanır, mevcut oturum açık kalır** (security stamp yenilenir, mevcut cookie yeniden imzalanır).
- **Kapsam dışı / ayrı karar:** hesap silme ve KVKK veri talebi (sipariş saklama yükümlülüğüyle birlikte karar ister), e-posta değiştirme ve parola sıfırlama (e-posta sağlayıcısı yok, D-036), yorum/puan, ürün JSON-LD (`react/no-danger` sözleşmesine istisna ister), misafir favorisi.

## MD-1 Favoriler

### Veri (database-dev)
- Tablo `customer_favorites`: `user_id uuid` (kullanıcı FK, kullanıcı silinince CASCADE), `product_id uuid` (ürün FK; davranış `cart_items`'ın ürün FK'sıyla aynı), `created_at timestamptz` (UTC). PK `(user_id, product_id)`; indeks `(user_id, created_at DESC)`.
- Migration yalnız ekleyicidir (yeni tablo). Yıkıcı işlem yok.

### API (backend-dev) — `/api/v1/favorites`, `RequireAuthorization()`
| Uç | Sonuç |
|---|---|
| `GET /favorites` | 200 `{ items: FavoriteItemDto[] }`, `addedAt` azalan. `FavoriteItemDto` = ürün liste öğesi alanları (`ProductListItemDto` ile aynı adlar/birimler: `startingPriceMinor` kuruş) + `addedAt` (UTC) + `available` (ürün ve kategorisi aktif mi). Satıştan kalkan ürün listede kalır, `available:false` (kullanıcı kaldırabilsin) |
| `GET /favorites/ids` | 200 `{ productIds: string[] }` (kalp durumu ve sayaç için hafif uç) |
| `PUT /favorites/{productId}` | 204. **İdempotent**: zaten favorideyse yine 204, ikinci satır yazılmaz. Ürün yok / aktif değil / geçersiz kimlik → 404 `product_not_found`. Kullanıcı başına en çok **100** → 409 `favorite_limit`. CSRF + `mutation` hız sınırı |
| `DELETE /favorites/{productId}` | 204. İdempotent: favoride değilse de 204. CSRF + `mutation` hız sınırı |
- Kullanıcı kimliği yalnız cookie'den; URL/gövdede kullanıcı kimliği yok. Başkasının favorisi hiçbir uçtan görünmez.
- Eşzamanlılık: aynı ürüne iki eşzamanlı PUT → tek satır, ikisi de 204 (benzersizlik ihlali başarı sayılır). 100 sınırı eşzamanlı eklemelerde aşılmamalı (adres sınırıyla aynı yöntem; o yöntem yarışa açıksa raporla).

### Arayüz (frontend-dev)
- `features/favorites/`: `favorites.ts` istemci deposu (kimlik kümesi; iyimser değiştirme + hata olursa geri alma; eski yanıtı yok sayan nesil sayacı; `resetFavorites()`), `FavoriteButton` (istemci adacığı, `aria-pressed`, erişilebilir ad), `FavoritesLink` (header/alt gezinti sayacı), `FavoritesView` (`/favorilerim` sayfası: kendi satır kartı, kaldır, sepete git, `available:false` için "Satışta değil", boş durum).
- Pasif "Yakında" kalpleri (Header, BottomNav, ProductCard) gerçek olur (D-023 favori için kapanır).
- **Logout güvenliği:** `resetCart()` çağrılan her yerde `resetFavorites()` de çağrılır; başka kullanıcıya önceki kullanıcının kalpleri görünmez.
- Bağımlılık tablosuna (`eslint.config.mjs`) eklenir: favorites → ortak · catalog → favorites (kart üstü kalp) · shell → favorites · auth/account/admin → favorites (logout temizliği, özet). Favoriler sayfası `ProductCard` kullanmaz (döngü olmasın).

### Test planı (öğretici mod)
| Test türü | Ne | Neden bu tür |
|---|---|---|
| Unit (backend) | Sınır 100, idempotent ekleme kararı, `available` hesabı | Saf kurallar: DB'siz, hızlı |
| Integration/API (gerçek test DB) | Happy path; 401 anonim; 404 ürün yok/pasif; 409 sınır; **izolasyon: A'nın favorisi B'de yok**; aynı PUT iki kez → tek satır; **eşzamanlı** iki PUT ve sınırda eşzamanlı eklemeler; CSRF'siz yazma reddi; şema/birim (`startingPriceMinor` kuruş) katalogla aynı | Race ve benzersizlik yalnız gerçek DB'de görülür; mock ile test edilemez |
| E2E (Playwright) | Kalbe bas → sayaç artar → `/favorilerim`'de görünür → kaldır; misafir kalbe basınca girişe yönlenir; **logout sonrası kalpler boş** | Kritik kullanıcı akışı ve logout güvenliği tarayıcıda doğrulanır |
| Statik | typecheck, lint (yeni sınırlar), build | İlk savunma hattı |
Regression: bulunan her hata için önce onu yakalayan test.

## MD-2 Profil
- API: `GET /auth/me` yanıtına `createdAt` (UTC) eklenir. `POST /auth/password` `{ currentPassword, newPassword }`: CSRF + `auth-credentials` hız sınırı; yanlış mevcut parola → 400 doğrulama hatası (`currentPassword`), yeni parola kayıt politikasıyla aynı; başarı 204, diğer oturumlar kapanır, mevcut oturum sürer (D-090); istemci yeni CSRF alır. Parola hiçbir loga/yanıta girmez.
- Arayüz: tüm hesap sayfalarında ortak yatay hesap menüsü (Özet · Siparişlerim · Favorilerim · Adreslerim · Güvenlik; split-screen yok). `/hesabim` özeti: e-posta, üyelik tarihi, son 3 sipariş, adres ve favori sayısı (mevcut uçlardan tarayıcıda). `/hesabim/guvenlik`: parola değiştirme formu. Mevcut URL'ler değişmez.
- Testler: API — happy, yanlış mevcut parola, zayıf yeni parola, anonim 401, CSRF'siz red, **eski oturum cookie'si değişiklikten sonra 401**, mevcut oturum açık; E2E — parola değiştir → çıkış → yeni parolayla giriş.

### MD-2 sözleşme (donduruldu)
- `GET /api/v1/auth/me` → mevcut `AccountDto` alanları + sonda `createdAt` (ISO-8601 UTC, ör. `2026-11-08T10:13:20Z`). Başka alan değişmez.
- `POST /api/v1/auth/password` gövde `{ "currentPassword": string, "newPassword": string }` (JSON, mevcut gövde okuyucu ve boyut sınırı):
  - 204: parola değişti; security stamp yenilendi → diğer oturum cookie'leri bir sonraki istekte 401; bu istekteki oturum yeniden imzalanmış yeni cookie ile sürer.
  - 400 `validation_failed` (`errors` sözlüğü, kayıttaki biçimle aynı): `currentPassword` boş/yanlış → `errors.currentPassword = ["Mevcut parola hatalı."]` (yanlışlığın nedeni ayrıntılanmaz); `newPassword` boş / politikaya uymaz (kayıttaki Identity politikası ve en çok uzunluk) / mevcutla aynı → `errors.newPassword = [...]` Türkçe.
  - 401 anonim · CSRF yoksa/yanlışsa mevcut CSRF reddi · `auth-credentials` hız sınırı (429 `Retry-After`) · 503 mevcut DB kalıbı.
  - Parolalar loga, yanıta, metriğe girmez. Başarı/başarısızlık olayı kişisel veri olmadan loglanabilir (kullanıcı kimliği uuid kabul).
- Web: her yazmadan önce taze CSRF alındığı için ek iş yok; başarıdan sonra `fetchAccount()` yenilenir.
- Arayüz ayrıntıları: `features/account/components/AccountNav.tsx` (yatay, `aria-current="page"`, mobilde yatay kaydırma, 44px hedef) `/hesabim`, `/hesabim/adresler`, `/hesabim/guvenlik`, `/siparisler`, `/favorilerim` sayfalarında görünür. Personel rolleri için özet sayfasında "Yönetim" bağlantısı korunur. Hesap kimliği (kopyala) özet sayfasında kalır. Tarihler `Europe/Istanbul` saat diliminde Türkçe gösterilir (saklama UTC). Son 3 sipariş `GET /orders?page=1&pageSize=3`, adres sayısı `GET /addresses`, favori sayısı favori deposundan.

## MD-3 Detay sayfaları
- Ürün: görsel galerisi (küçük resimler, klavye ile gezinme, mevcut `images` verisi), favori düğmesi, aynı kategoriden en çok 4 benzer ürün (mevcut `GET /products?category=`; mevcut ürün hariç), teslimat/iade bilgi bloğu (yalnız doğru bilgi: ödeme alınmaz, kargo hesaplanmaz, iptal kuralları), mobilde sabit "sepete ekle" çubuğu.
- Sipariş detayı: durum geçmişinden görsel zaman çizelgesi, kalemlerden ürün sayfasına bağlantı.
- Testler: E2E — galeri ve klavye, benzer ürünlerde mevcut ürün yok, mobil 390'da sabit çubuk, a11y (axe); mevcut 213 e2e regresyonu.

## MD-4 Yasal sayfalar
Metinler ürün sahibinden/hukukçudan gelir; uydurma yasal metin yayımlanmaz. Altyapı (rotalar, footer bağlantıları) içerik geldiğinde yapılır.

## Vitrin paketi (VT) — 2026-11-08, kullanıcı kararları

Proje canlıya alınmayacak bir portföy vitrini. Kararlar: logo **A · Etiket** (pano: https://claude.ai/artifact/FkSadgbdaERoukGLr1dsma), demo için **sabit vitrin verisi + demo giriş düğmeleri**, koyu tema **şimdilik yok**. Ziyaretçiye özel 24 saatlik demo hesabı (demo-account kalıbı) kullanılmaz: siparişler kullanıcıya RESTRICT, tahsisler değişmez stok hareketlerine bağlı; demo kullanıcı stok defteri bozulmadan silinemez (D-092).

| Adım | Kapsam | Ajan |
|---|---|---|
| VT-1 | MD-4 örnek yasal/kurumsal sayfalar | frontend-dev |
| VT-2 | Vitrin verisi + demo giriş ucu (`Showcase:Enabled`) | backend-dev |
| VT-3 | Logo A (SVG bileşen, favicon/app icon), giriş sayfasında demo düğmeleri, `dev.ps1 -Showcase` | frontend-dev + ana ajan |
| VT-4 | Vitrin README'si + GIF/ekran görüntüleri (SS Filo düzeni) | ana ajan (readme-media) |

### VT-2 sözleşme
- `Showcase:Enabled` (varsayılan `false`). `ProductionConfigurationValidator` Production'da `true`'yu reddeder; dağıtım dosyaları bunu hiç açmaz.
- Komut `--seed-showcase` (yalnız Development, `--seed-demo-catalog` ile aynı koruma; idempotent): demo kataloğunu garanti eder; depo yoksa "Ana Depo"; her aktif varyanta stok girişi (envanter servisi üzerinden, hareketli); üç vitrin hesabı (sabit e-postalar `demo-musteri@sscommerce.example`, `demo-yonetici@sscommerce.example`, `demo-depo@sscommerce.example`; rastgele, hiçbir yerde saklanmayan parola; roller Customer/Admin/Warehouse); demo müşteriye 2 adres, ~6 favori, sepette 2 ürün ve GERÇEK sipariş servisinden geçen 5 sipariş (teslim edildi, kargoda, hazırlanıyor, alındı, iptal) — durumlar personel durum servisiyle ilerletilir; tarihler gerçekçi olsun diye geçmişe yayılabilir (yalnız zaman alanları; tutar/stok değişmezleri bozulmaz).
- `GET /api/v1/auth/demo` → kapalıyken 404; açıkken 200 `{ "accounts": ["customer","admin","warehouse"] }`.
- `POST /api/v1/auth/demo` `{ "account": "customer"|"admin"|"warehouse" }` → kapalıyken 404; açıkken ilgili SABİT vitrin hesabıyla normal giriş cookie'si (parolasız `SignInAsync`, yalnız bu üç e-posta), 200 `AccountDto`; hesap yoksa 409 `showcase_not_seeded`; CSRF + `auth-credentials` hız sınırı; başka kullanıcı hiçbir yolla hedeflenemez.
- Bilinen sınır: vitrin hesapları ortaktır (canlı yok); demo yönetici kataloğu değiştirebilir — README "Bilinen sınırlar".

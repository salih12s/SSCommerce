# SSCommerce — Autonomous Build Goal: Faz 0 → Faz 1 → Faz 2

**Belge sürümü:** 1.0  
**Çalışma ortamı:** Windows, VS Code, Claude Code  
**Repository:** `https://github.com/salih12s/SSCommerce.git`  
**Kapsam:** Yalnızca Faz 0, Faz 1 ve Faz 2. Faz 3'e geçme.  
**Başlangıç varsayımı:** `.claude/agents/` ve `CLAUDE.md` mevcut olabilir; önce incele ve koru. Git, Node.js ve Docker kurulumlarını doğrulamadan var sayma.

## 1. Ana hedef ve bitiş koşulu

Faz 0, 1 ve 2'yi **sırayla**, birbirinden bağımsız küçük görevlerle uygula. Her görev için gerçek test çıktısı üret; başarısızlığı gizleme. Bir fazın teknik kapıları geçilmeden diğerine geçme. Görevler arasında yeni kullanıcı prompt'u bekleme. Bu belge, yalnızca Faz 0–2 kapsamındaki normal ve geri alınabilir geliştirme adımlarını önceden onaylar; riskli işlemleri veya Git yetkilerini onaylamaz.

**Başarı ancak şu kanıtlarla mümkündür:**

1. Faz 0, 1, 2 kabul kriterlerinin tümü `docs/PROGRESS.md` içinde `PASS` olarak, çalıştırılan komut ve sonuçlarıyla kayıtlı.
2. `README.md`, `.gitignore`, `.editorconfig`, `docs/ROADMAP.md`, `docs/ARCHITECTURE.md`, `docs/DECISIONS.md` ve `docs/PROGRESS.md` mevcut ve güncel.
3. ASP.NET Core 10 çözümü ve testleri derleniyor/çalışıyor; en az bir gerçek Web API smoke/integration testi var.
4. PostgreSQL bağlantısı gerçekten doğrulanmış; `docker compose up -d` başarıyla çalışmış ve kalıcı volume kullanılmış. Docker yoksa bu kriter `PASS` olamaz.
5. Next.js + TypeScript uygulaması kurulmuş; `npm run lint`, TypeScript typecheck ve `npm run build` başarılı.
6. Onaylı SSCommerce mağaza görsel dilini yansıtan responsive ana sayfa gerçek tarayıcıda/uygun UI testleriyle incelenmiş; mobil ve masaüstünde yatay taşma, kırık navigasyon ve bariz erişilebilirlik sorunları giderilmiş.
7. `git diff --check` başarılı; gerçek sırlar veya ortam dosyaları stage edilmemiş; yapılan değişiklikler ve riskler son raporda yer alıyor.
8. GitHub push yalnızca kullanıcı izni ve incelemesiyle yapılır. Push henüz onaylanmadıysa **yerel teknik tamamlanma** ile **uzak depoya yayınlanma** durumlarını ayrı bildir; uzaktaki durum tamamlandı deme.

**Asla yanlış başarı ilan etme.** Çalıştırılamayan kontrol = `NOT_VERIFIED`; başarısız kontrol = `FAIL`; dış etken = `BLOCKED_EXTERNAL`; bekleyen kullanıcı izni = `PENDING_APPROVAL`. Bunlar `PASS` yerine geçmez.

## 2. Çalışma biçimi: Opus koordinatör, Sonnet uygulayıcı

- Ana ajan: **Opus 5.5, high**. Mimari kararları ve küçük görevleri yönetir, test kanıtlarını inceler, mevcut repo durumuna göre sıradaki görevi seçer. Büyük kod işlerini doğrudan yazmak yerine alt ajana delege eder.
- Alt ajanlar: **Sonnet 5.5, high**, mevcut `.claude/agents/` tanımlarına uyar. Uygun ajanlar `backend-dev`, `frontend-dev`, `database-dev`, `qa-reviewer`.
- Gereken **en az sayıda** alt ajanı kullan. Dokümantasyon veya dar kapsamlı kurulum için tek Sonnet; bağımsız inceleme için QA. Aynı dosyayı iki alt ajana aynı anda yazdırma.
- Önce dosyayı araştır, sonra değiştir. Büyük logları ana ajana taşıma; dosya yollarını, değişiklik özetini, komut+exit code'larını, riskleri ver.
- Her alt ajan teslimi: `Görev ID | değişen dosyalar | neden | gerçekten koşulan komutlar + exit code | kanıt | riskler | önerilen sonraki adım`.
- Ana ajan her teslimi kontrol eder. İddiaları gerektiğinde terminalle doğrular. Test kanıtı yoksa görevi bitmiş kabul etmez.
- Yanıt ve öğretici açıklamalar **Türkçe**, kaynak kod ve standart dosya/identifer adları İngilizce.
- Token ekonomisi: Gereksiz full-repo yeniden tarama yapma; her seferinde 4 ajanı açma; aynı başarısız komutu sebepsiz tekrar tekrar deneme. Basit görevlerde uzun muhakeme raporları üretme.

## 3. Emniyet, izin ve değişiklik sınırları

- `.claude/settings.json`, `CLAUDE.md` ve mevcut ajan tanımlarını **bozma**. Gerçek bir çakışma tespit edersen açıkça raporla.
- `.env`, `.env.*`, anahtar, token, parola ve sertifikaları Git'e ekleme. `.env.example` sır içermeyen şablondur, commit edilebilir. `.claude/settings.local.json` Git dışında kalmalı.
- `git push`, `git commit`, branch silme, force push, reset --hard, dış servis satın alımı/kurulumu, Windows yönetici yetkisi, WSL/Docker sistem kurulumları ve yıkıcı DB komutları **açık kullanıcı onayı** gerektirir. Yetkiyi atlatmak için izin modunu gevşetme.
- Normal dosya üretimi, test, build, lint, yerel geri alınabilir geliştirme değişiklikleri için sürekli yeni görev prompt'u isteme.
- Kullanıcı onayı gereken Git işlemlerini biriktirip açıkça sun; mümkünse diğer güvenli işleri tamamlayarak ilerle. Onay gelmeden GitHub'a gönderildiğini söyleme.
- Docker/Node/npm/SDK eksikse sahte araç, mock veya yanlış `PASS` oluşturma. Kurulum için kısa, kesin yönerge ver ve `BLOCKED_EXTERNAL` kaydet.
- Onaylı tasarım mockup'ları **görsel referans**; kod, lisanslı görsel veya gerçek ürün fiyat verisi değildir. Sahte ödeme, gerçek indirim veya doğrulanmamış vaat yayınlama.
- Faz 3+ özelliklerini (gerçek katalog CRUD, Identity auth, stok/sipariş motoru, ödeme, gerçek ERP verisi, B2B) bu hedefte geliştirme.
- Belirsizliklerde geri döndürülebilir, sade bir varsayım yap; kararları `docs/DECISIONS.md` içine gerekçesiyle yaz. Kimlik, ödeme veya güvenlik gibi yüksek etkili kararları varsayarak uygulama.

## 4. Teknik sözleşme (değiştirme)

- Repo/solution/namespace adı: `SSCommerce`; daha önce vitrinde `SSCommerse` yazımı kullanılmıştı. Marka ismi farklılığını `docs/DECISIONS.md` içinde **açık karar bekliyor** olarak not et. Repo adını değiştirme.
- Backend: **ASP.NET Core 10**, C#, **EF Core 10**, **PostgreSQL**, REST, OpenAPI, Modular Monolith / sade Clean Architecture katmanları.
- Frontend: **Next.js App Router + React + TypeScript + Tailwind CSS**, tek web uygulaması; `/admin` ayrı frontend olmayacak.
- Tek satıcı, fiziksel ürünler, TRY; ilk sürümde **gerçek ödeme alınmaz**.
- Domain fiyatı C# `decimal` / PostgreSQL `numeric(18,2)`, API tasarımı kuruş cinsinden tam sayı. Saatler kalıcı veride UTC.
- Gelecek mimari: Product → ProductVariant/SKU → Warehouse/InventoryBalance; sipariş fiyat snapshot'ı ve idempotency. **Bu varlıkların tamamını Faz 1'de erken oluşturma.**
- Public domain iş mantığının tek yetkili sahibi .NET backend. Next.js tarafı domain mantığını kopyalamaz. Geliştirmede same-origin API yönlendirmesi tercih et; gerçek auth/CSRF Faz 4 kapsamındadır.

## 5. Faz 0 — Repository ve geliştirici hazırlığı

**F0.1 Envanter ve emniyet**
- Çalışılan klasörü, `git status`, `git remote -v`, `.claude` ajan ayarlarını incele.
- Depo Git ile yönetilmiyorsa `git init -b main` uygula; `origin` yoksa yalnızca doğru URL'yi ekle. Varsa doğru mu kontrol et. Mevcut başka remote'u sessizce değiştirme.
- Terminalde `dotnet --version`, `dotnet --list-sdks`, `node --version`, `npm --version`, `git --version`, `docker --version`, `docker compose version`, `docker info` çalıştır. Kurulu olmayan araçları raporla, kuruluymuş gibi davranma.

**F0.2 Temel dosyalar**
- `.gitignore`: .NET `bin/`, `obj/`, Node `node_modules/`, `.next/`, `.env`, `.env.*` (istisna `!.env.example`), `.claude/settings.local.json`, IDE geçici dosyaları. **`package-lock.json` ve diğer kullanılan lockfile'ları ignore etme.**
- `.editorconfig`: C#/TS uyumlu sade, anlaşılır standart.
- `README.md`: Ne yapıyoruz, neden, gereksinimler, çalıştırma komutları (gerçekten mevcut olanları), henüz yapılmayanlar.
- `docs/ROADMAP.md`: Faz 0–9 ana yol haritası ve Faz 0–2 görevleri.
- `docs/ARCHITECTURE.md`: katmanlar, modüller, API ve güvenlik sınırları; gelecek aşamalar ayrıca işaretli.
- `docs/DECISIONS.md`: sürümlü teknik karar ve gerekçeler.
- `docs/PROGRESS.md`: her görev için durum, tarih, komut/test kanıtı, değişen dosyalar, Git durumu, blocker ve sonraki görev. **Tek kalıcı ilerleme kaynağı.**
- `git check-ignore -v .env` `.env`'in dışlandığını; `.env.example`'ın dışlanmadığını doğrula. Gerçek sır içeren test dosyası oluşturma.

**Faz 0 çıkış kapısı:** Yukarıdaki dosyalar hazır, Git'in neye bağlı olduğu belli, kullanılabilir araçlar doğrulanmış, eksikler görünür. Docker/Node eksikliği kayda geçmiş olabilir ama Faz 1'de o gereksinime geçildiğinde düzeltmeden başarı ilan etme.

## 6. Faz 1 — Çalışan full-stack temel

Görevleri sırayla küçük parçalarda geliştir:

**F1.1 Backend solution**
- `backend/SSCommerce.slnx` (araç desteği izin veriyorsa) ve `backend/src/SSCommerce.Api`, `SSCommerce.Application`, `SSCommerce.Domain`, `SSCommerce.Infrastructure` projelerini kur. `backend/tests/` içinde en az bir gerçek test projesi oluştur.
- Bağımlılık yönü: Domain dış katmanlara bağımlı değil; Application → Domain; Infrastructure → Application/Domain; Api → Application ve composition root üzerinden Infrastructure.
- Gereksiz CQRS/MediatR/repository abstraction/event bus ekleme. Basit health, OpenAPI, merkezi hata modeli ve temel API yapılandırmasını oluştur.
- `GET /health/live`: veritabanı olmasa da uygulamanın yaşadığını gösterir. `GET /health/ready`: PostgreSQL erişimini gerçekten test eder, başarısızsa başarısız döner. Health endpoint'inde hata gizleyip 200 döndürme.
- `dotnet restore`, `dotnet build`, `dotnet test` çalıştır; gerçek API smoke testi ekle. Uygulamayı gerçekten başlat ve HTTP yanıtını doğrula.

**F1.2 PostgreSQL ve Docker Compose**
- Kök `docker-compose.yml` içinde PostgreSQL servisi, healthcheck ve **named volume**; sırları repo içine gömme. `.env.example` içinde sahte/yerel örnek değerleri açıklayabilirsin; gerçek `.env` Git dışı.
- `docker compose config`, `docker compose up -d`, `docker compose ps` ve gerçek bağlantı kontrolünü uygula. Docker engine çalışmıyorsa `BLOCKED_EXTERNAL` ve kurulum yönergesi ver; geçiyormuş gibi yapma.
- `Infrastructure` için Npgsql/EF Core temel kurulumunu yap ve bağlantı doğrulamasını gerçekleştir. Henüz business entity yoksa sahte/boş migration yaratmaya zorlama; migration kararını belgele.

**F1.3 Next.js iskeleti ve entegrasyon**
- `apps/web` içinde Next.js App Router, TypeScript, Tailwind, ESLint ve npm lockfile kur.
- Backend domain iş mantığını Next.js'e kopyalama; geliştirme için uygun API proxy/rewrite veya eşdeğer açıkça belgelenmiş yapı kullan.
- Frontend'in backend health yanıtını doğrulayabildiği kontrollü bir bağlantı örneği oluştur. Backend ulaşılamıyorsa sahte başarı üretme.
- `npm run lint`, `npx tsc --noEmit`, `npm run build` başarılı olsun. `package.json` script'leri yeterli ve tutarlı olmalı.

**F1.4 CI ve kalite**
- `.github/workflows/ci.yml` içinde .NET restore/build/test ve Next.js install/lint/typecheck/build adımları. Lockfile için `npm ci` kullan.
- CI dosyasını syntax açısından doğrula; GitHub Actions gerçekten çalıştırılmadıysa `PENDING_REMOTE_CI` olarak raporla.
- Güncel README'de Windows PowerShell geliştirme adımlarını yaz; komutları gerçekten çalıştırmadan çalışıyor deme.

**Faz 1 çıkış kapısı:** API ve web uygulaması gerçek çalışıyor, PostgreSQL bağlantısı doğrulandı, test/build/lint temiz, CI tanımlı. Gerçek uzak CI ancak push sonrası doğrulanabilir.

## 7. Faz 2 — Onaylı SSCommerce storefront tasarımının kodlanması

**F2.1 Tasarım token'ları ve bileşenler**
- Tasarım yönü: koyu kömür/siyah üst navigasyon, sıcak kırık beyaz sayfa zeminleri, **turuncu vurgu**, premium teknoloji/lifestyle ürün görselleri, okunabilir tipografi, rafine ürün kartları, boşluk ve hiyerarşi.
- Reusable: `Header`, search, kategori menüsü, hero, kategori kartı, ürün kartı, kampanya/koleksiyon bölümü, trust/service section, `Footer`, mobil gezinme.
- Masaüstü ve mobil aynı marka dili. Ekran görüntüsüne benzetmek için anlamlı layout üret; görsel mockup dosyalarının kod veya asset olarak mevcut olduğunu varsayma.

**F2.2 Ana sayfa**
- Ana sayfa: çok kategorili teknoloji yaşam tarzı hero (yalnızca gaming laptop vitrinine dönme), dört ana kategori, öne çıkan koleksiyonlar, ürün kartları ve footer.
- Resimler için telif ve lisans açısından güvenli lokal placeholder/izinli kaynak kullan; kırık dış link ve izinsiz kopya görsel kullanma. Fiyat/indirim etiketleri mock ise UI veya belgede örnek olduğunu açık et.
- Bu fazda ürün fixture'ları **tasarım/demo** verisi; gerçek backend katalog verisi olduğunu iddia etme. Gerçek katalog Faz 3.
- Temel sayfa etkileşimleri ya çalışan erişilebilir bağlantılardır ya da bilinçli biçimde `coming soon` olarak işaretlidir. İşlevsiz 'Sepete ekle' ya da ödeme olmuş izlenimi verme.

**F2.3 Responsive ve kalite**
- 1440px masaüstü, 768px tablet, 390px mobil görünümü kontrol et. Mobilde navigasyon ve kart grid'i uygun kırılmalı; yatay scroll olmamalı.
- Keyboard focus, semantic landmark, resimler için alt text, kontrast, temel Core Web Vitals performansını gözet.
- Playwright/UI smoke test erişilebilirse gerçek browser doğrulaması ekle; erişilemiyorsa `NOT_VERIFIED` kaydet, yapılmış gibi davranma.
- `npm run lint`, `npx tsc --noEmit`, `npm run build`, `dotnet build`, `dotnet test` yeniden çalıştır. Faz 1 özellikleri kırılmamış olmalı.
- `/admin` dashboard'unu Faz 8'e bırak. Bu aşamada güvenliği varmış gibi gösteren sahte admin CRUD veya sahte siparişler sunma.

**Faz 2 çıkış kapısı:** Ürün kodu değil, **gerçek çalışan responsive storefront tasarım temeli** var. Kullanıcı onaylı ana sayfa görsel diline sadık, test/build kanıtı mevcut.

## 8. Her görevin zorunlu döngüsü

1. `docs/PROGRESS.md` içinden ilk eksik görevi seç; sadece o görevin kapsamını belirle.
2. İlgili tek uzman alt ajana aktar; dosya sınırlarını ve kabul kriterini ver.
3. Alt ajanın değişikliklerini `git diff` ile incele, ilgili komutları/testleri çalıştır.
4. Sonuç geçmezse **nedenini tespit ederek** düzelt; aynı başarısız hareketi körlemesine döndürme.
5. Kanıt ile birlikte `docs/PROGRESS.md` ve gerekiyorsa `docs/DECISIONS.md` güncelle.
6. Faz kapısını değerlendir. Geçtiyse sıradaki faza otomatik devam et. Kullanıcıdan yeni prompt isteme.
7. Git commit/push için ayrıca kullanıcıdan izin iste; bu izin fazlar arasında kod işini sürdürmene engel olmasın (uzak CI doğrulaması ayrı `PENDING_REMOTE_CI`).

## 9. Takılma, bütçe ve bitiş raporu

- Her turda amaçlı, ölçülebilir ilerleme göster. Art arda iki denemede yeni kanıt yoksa durumu açıkça analiz et ve farklı, güvenli çözüm dene.
- Dış kurulum/yetki/credential için kendi başına yönetici yetkisi veya secret üretme. Uygun noktada `BLOCKED_EXTERNAL` veya `PENDING_APPROVAL` yazıp kullanıcıdan **yalnızca gerekli eylemi** iste.
- Yaklaşık 25–30 turda üç faz bitmiyorsa mevcut durumu `INCOMPLETE` olarak raporla; amaçsız token yakma. Bu bir maliyet sınırı olup kalite kriterlerini gevşetmez.
- Her faz sonunda kısa Türkçe özet üret: **ne değişti, neden böyle, hangi komut gerçekten geçti, hangi kod yolunu inceleyebiliriz, hangi Git checkpoint'i hazır?**
- Son durumda tek bir kompakt sonuç tablosu ver: Faz | PASS/FAIL/BLOCKED | kanıt | commit/push durumu | sonraki adım.
- `COMPLETED` sözcüğünü yalnızca bütün **yerel teknik** kapılar gerçekten geçtiğinde kullan. Remote push/CI bekliyorsa ayrı açıkça not et. Eksikse `INCOMPLETE` veya `BLOCKED` yaz; eksik işleri görünmez yapma.

## 10. `/goal` değerlendirmesi için son kanıt

Ana ajan, tamamlandığını iddia etmeden hemen önce konuşmada en az şunları gösterir:

- `dotnet --version`, `node --version`, `docker compose version` çıktılarından kısa özet;
- `dotnet build`, `dotnet test`, web lint/typecheck/build **gerçek exit code'ları**;
- PostgreSQL container ve readiness sonuçları;
- masaüstü/mobil UI doğrulama sonucu ve varsa browser test çıktısı;
- `git status --short`, `git diff --check`, `docs/PROGRESS.md` görev kapıları;
- GitHub commit/push ve CI gerçekleştiyse kanıt, gerçekleşmediyse `PENDING_APPROVAL`/`PENDING_REMOTE_CI`.

**Evaluator için kural:** Ana ajanın 'yaptım' demesi kanıt sayılmaz. Dosya varlığı, gerçek çalışma komutları ve çıkış kodlarıyla doğrulanmayan hiçbir kabul kriteri tamamlanmış değildir.

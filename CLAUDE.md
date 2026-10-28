# SSCommerce

E-ticaret uygulaması (tek satıcı, fiziksel ürün, TRY, ilk sürümde gerçek ödeme yok).
Yığın: **ASP.NET Core 10 + EF Core 10 + PostgreSQL** (`backend/`), **Next.js App Router + TypeScript + Tailwind** (`apps/web/`).
Hedef ve faz kapsamı: `GOAL_PHASES_0_2.md`. İlerleme: `docs/PROGRESS.md` (tek kaynak). Kararlar: `docs/DECISIONS.md`. Mimari: `docs/ARCHITECTURE.md`.
Kurallar: para API'de kuruş (`long`), domain'de `decimal`, DB'de `numeric(18,2)`; zaman UTC. İş mantığı yalnızca backend'de.

## Roller

| Rol | Model / effort | Görev |
|---|---|---|
| Ana ajan (oturum) | Opus 5.5 / high | Mimari karar, görevi bölme, devretme, sonucu inceleme, kullanıcıya Türkçe rapor |
| `backend-dev` | Sonnet 5.5 / high | API, iş mantığı, backend testleri |
| `frontend-dev` | Sonnet 5.5 / high | Arayüz, istemci durumu, frontend testleri |
| `database-dev` | Sonnet 5.5 / high | Şema, migration, seed, sorgu performansı |
| `qa-reviewer` | Sonnet 5.5 / high | Salt okunur kod incelemesi, test/lint/typecheck çalıştırma |

Alt ajan modeli `.claude/settings.json` içindeki `CLAUDE_CODE_SUBAGENT_MODEL` + `_FORCE` ile zorlanır. Bu açıkken Agent çağrısında `model` parametresi ve frontmatter'daki `model` yok sayılır. İstisna: `fork` her zaman ana modelde (Opus) çalışır.

## Dizin sahipliği

Aynı dosyaya iki ajan aynı anda dokunmaz.

| Dizin | Sahibi |
|---|---|
| `apps/web/` | frontend-dev |
| `backend/` (migration ve `DbContext` model yapılandırması hariç) | backend-dev |
| `backend/src/SSCommerce.Infrastructure/Persistence/` (migration, entity config, seed) | database-dev |
| `docker-compose.yml`, `.github/`, kök config, `docs/`, `CLAUDE.md`, `.claude/` | Ana ajan. Görev brifinde tek bir ajana açıkça devredilebilir |

## Orkestrasyon kuralları (ana ajan)

- Küçük ya da tek dosyalık işi kendin yap; alt ajan açma.
- Büyük işi en fazla 1–2 alt ajanla başlat. Paralel çalışacaklarsa dosya kümeleri kesişmesin.
- Her devirde brif şunları içerir: **amaç, kapsam (dokunulacak dosya/dizinler), kapsam dışı, tamamlanma kriteri (hangi komut neyi göstermeli), bilinen kararlar.** Alt ajan bu sohbeti görmez; gereken bağlamı brife yaz.
- Alt ajandan tam dosya, uzun log ya da diff isteme. Özet + dosya yolu + test kanıtı iste.
- Aynı analizi iki kez yaptırma. Bir alt ajanın bulduğunu tekrar okutma; gerekiyorsa `SendMessage` ile aynı ajana devam et.
- Kod değişikliğinden sonra risk varsa `qa-reviewer` ile doğrula. Para, yetki ve eşzamanlılık her zaman risklidir.
- Faz bitince kullanıcıya rapor ver ve **onay almadan yeni faza geçme.**

## Alt ajan rapor biçimi (hepsi için ortak)

```
Sonuç: TAMAM | KISMİ | ENGELLENDİ
Değişen dosyalar: yol — tek satır açıklama
Doğrulama: çalıştırılan komut → sonuç (ör. 12 geçti / 0 kaldı). Çalıştırılmadıysa: "çalıştırılmadı — neden"
Açık konular: karar gerektirenler, riskler, kapsam dışı bırakılanlar
```
En fazla ~25 satır. Kod bloğu yalnızca kritik bir satırı göstermek için kullanılır.

## Güvenlik ve kontrol

- Var olan dosyayı silmeden ya da değiştirmeden önce oku.
- Sır (parola, API anahtarı, bağlantı dizesi) dosyaya, loga ya da commit'e yazılmaz; yalnızca `.env` + `.env.example` (sahte değerlerle). `.env` okuma izinleri `settings.json` ile kapalı.
- Commit ve push yalnızca kullanıcı isteyince yapılır; ayarlarda her seferinde onay ister.
- Çalıştırılmamış test ya da build "başarılı" diye raporlanmaz.
- Üretim veritabanına bağlanılmaz; yıkıcı migration (drop/reset) kullanıcı onayı olmadan çalışmaz.

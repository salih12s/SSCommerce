# SSCommerce

Teknoloji ve yaşam tarzı ürünleri satan, tek satıcılı bir e-ticaret mağazası. Fiziksel ürün satılır, para birimi TRY'dir. **İlk sürümde gerçek ödeme alınmaz.**

## Neden bu yapı?

- **ASP.NET Core 10 + EF Core 10 + PostgreSQL** (`backend/`): fiyat, stok ve sipariş kuralları gibi iş mantığının tek sahibi.
- **Next.js + TypeScript + Tailwind** (`apps/web/`): vitrin ve ileride `/admin`. İş mantığı içermez, API'yi same-origin proxy üzerinden çağırır.
- Ayrıntılar için: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/DECISIONS.md](docs/DECISIONS.md), [docs/ROADMAP.md](docs/ROADMAP.md). İlerleme kaydı: [docs/PROGRESS.md](docs/PROGRESS.md).

## Gereksinimler

| Araç | Sürüm | Not |
|---|---|---|
| .NET SDK | 10.0.x | `dotnet --version` |
| Node.js / npm | 24.x / 11.x | `node --version` |
| Docker Desktop | 29.x (Compose v5) | PostgreSQL konteyneri için. `docker info` çalışmalı |
| Git | 2.x | |

## Çalıştırma (Windows PowerShell)

> Yalnızca bu makinede gerçekten çalıştırılmış komutlar yazılıdır (bkz. [PROGRESS](docs/PROGRESS.md)).

### 1. Veritabanı (Docker Desktop)

```powershell
Copy-Item .env.example .env      # sonra .env içindeki parolayı değiştir
docker compose up -d             # PostgreSQL 17, 127.0.0.1:5433, volume: sscommerce-pgdata
docker compose ps                # postgres "(healthy)" olmalı
```

Parola, volume **ilk oluşturulduğunda** veritabanına yazılır. Sonradan `.env`'de değiştirmek mevcut volume'u etkilemez. Sıfırdan başlamak için `docker compose down -v` gerekir; bu komut **tüm yerel veriyi siler**.

### 2. Backend API (http://localhost:5080)

```powershell
dotnet build backend/SSCommerce.slnx -c Release
dotnet test  backend/SSCommerce.slnx -c Release   # gerçek DB testi, SSCOMMERCE_TEST_POSTGRES yoksa atlanır

# Bağlantı dizesi yalnızca ortam değişkeniyle verilir; repoya yazılmaz. Parola .env'deki değerdir.
# Host olarak localhost değil 127.0.0.1 kullan: konteyner yalnızca IPv4'e bağlı. Windows'ta "localhost" önce ::1'i
# dener ve reddedilmesi ~2,6 sn sürer; bu da /health/ready'nin 3 sn sınırını aşmasına yol açabilir (DECISIONS D-019).
$env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=5433;Database=sscommerce;Username=sscommerce;Password=<.env'deki parola>"
$env:SSCOMMERCE_TEST_POSTGRES   = $env:ConnectionStrings__Postgres   # gerçek DB testini de koşturmak için
dotnet run --project backend/src/SSCommerce.Api --launch-profile http
```

| Uç | Ne gösterir |
|---|---|
| `GET /health/live` | Süreç ayakta mı (DB'ye bakmaz) → 200 |
| `GET /health/ready` | PostgreSQL'e gerçekten bağlanır; bağlanamazsa **503** |
| `GET /openapi/v1.json` | OpenAPI belgesi (yalnızca Development) |

Bağlantı dizesi tanımlı değilse API yine açılır; `/health/ready` 503 döner. Bu doğrulandı.

### 3. Web (http://localhost:3000)

```powershell
cd apps/web
npm ci
npm run dev            # geliştirme
npm run lint
npm run typecheck      # tsc --noEmit
npm run build
npx playwright install chromium   # ilk seferde
npm run test:e2e       # build + 1440/768/390 UI smoke ve axe erişilebilirlik testleri
```

Web, `/api/backend/*` isteklerini `API_BASE_URL` adresine (varsayılan `http://localhost:5080`) iletir (`apps/web/.env.example`). `/status` sayfası backend health durumunu dürüstçe gösterir.

## Henüz yapılmayanlar

- Gerçek katalog, kimlik doğrulama, sepet/stok, sipariş, ödeme, yönetim paneli (Faz 3–8). Vitrindeki ürünler ve fiyatlar **demo verisidir**; satış ve ödeme yoktur.
- Uzak CI çalıştırması (push sonrası doğrulanacak).

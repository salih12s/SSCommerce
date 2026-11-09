# Yerel geliştirme: dev veritabanı + backend API (http://localhost:5080) + web (http://localhost:3000) tek komutla.
# Kullanım (proje kökünde):  .\dev          (dev.cmd bu betiği çalıştırma ilkesine takılmadan açar)
#                            .\dev -Seed    (önce demo kataloğu yükler; tekrar çalıştırmak güvenli)
#                            .\dev -Showcase (vitrin modu: katalog + stok + demo hesaplar/siparişler kurulur, giriş sayfasında demo düğmeleri açılır; tekrar çalıştırmak güvenli)
# Ctrl+C ikisini birlikte durdurur. Parola yalnız çalışma anında kök .env'den okunur; ekrana ve dosyaya yazılmaz.
param([switch]$Seed, [switch]$Showcase)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# 1) Kök .env'den yalnız gereken POSTGRES_* değerlerini oku
$envFile = Join-Path $root '.env'
if (-not (Test-Path $envFile)) { throw ".env bulunamadı. Önce: Copy-Item .env.example .env (sonra parolayı değiştir)" }
$cfg = @{}
foreach ($line in Get-Content $envFile) {
  if ($line -match '^\s*(POSTGRES_[A-Z_]+)\s*=\s*(.*?)\s*$') { $cfg[$Matches[1]] = $Matches[2].Trim('"', "'") }
}
foreach ($k in 'POSTGRES_DB', 'POSTGRES_USER', 'POSTGRES_PASSWORD') {
  if (-not $cfg[$k]) { throw ".env içinde $k eksik" }
}
$port = if ($cfg['POSTGRES_PORT']) { $cfg['POSTGRES_PORT'] } else { '5433' }

# 2) Dev veritabanı (zaten çalışıyorsa hiçbir şey değiştirmez)
Write-Host '> Veritabanı (docker compose up -d)...' -ForegroundColor Cyan
docker compose -f (Join-Path $root 'docker-compose.yml') up -d --wait
if ($LASTEXITCODE -ne 0) { throw 'docker compose başarısız. Docker Desktop açık mı?' }

# 3) API ortamı (yalnız bu süreç ve çocukları için)
$env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=$port;Database=$($cfg['POSTGRES_DB']);Username=$($cfg['POSTGRES_USER']);Password=$($cfg['POSTGRES_PASSWORD'])"
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$apiProject = Join-Path $root 'backend/src/SSCommerce.Api'

if ($Seed -and -not $Showcase) {
  Write-Host '> Demo katalog yükleniyor...' -ForegroundColor Cyan
  dotnet run --project $apiProject -- --seed-demo-catalog
  if ($LASTEXITCODE -ne 0) { throw 'Demo katalog yüklenemedi' }
}

# Vitrin modu (D-092): --seed-showcase demo kataloğunu da kurar; Showcase__Enabled yalnız bu süreçte açılır (Development dışında API açılmayı reddeder).
if ($Showcase) {
  Write-Host '> Vitrin verisi kuruluyor (katalog, stok, demo hesaplar, siparişler)...' -ForegroundColor Cyan
  dotnet run --project $apiProject -- --seed-showcase
  if ($LASTEXITCODE -ne 0) { throw 'Vitrin verisi kurulamadı (yukarıdaki hataya bak)' }
  $env:Showcase__Enabled = 'true'
} else {
  $env:Showcase__Enabled = 'false'
}

# 4) API'yi aynı pencerede arka planda başlat, hazır olmasını bekle
Write-Host '> API başlatılıyor (http://localhost:5080)...' -ForegroundColor Cyan
$api = Start-Process dotnet -ArgumentList @('run', '--project', "`"$apiProject`"", '--launch-profile', 'http') -NoNewWindow -PassThru
try {
  $ready = $false
  for ($i = 0; $i -lt 90 -and -not $api.HasExited; $i++) {
    try {
      $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 'http://127.0.0.1:5080/health/ready'
      if ($r.StatusCode -eq 200) { $ready = $true; break }
    } catch { }
    Start-Sleep -Seconds 1
  }
  if ($api.HasExited) { throw 'API başlamadan kapandı (yukarıdaki hataya bak)' }
  if ($ready) { Write-Host '> API hazır.' -ForegroundColor Green } else { Write-Host '> API 90 sn içinde hazır olmadı; web yine de açılıyor.' -ForegroundColor Yellow }

  # 5) Web (ön planda). Ctrl+C ikisini de durdurur.
  Write-Host '> Web başlatılıyor: http://localhost:3000  (durdurmak için Ctrl+C)' -ForegroundColor Cyan
  Push-Location (Join-Path $root 'apps/web')
  try { npm run dev } finally { Pop-Location }
}
finally {
  # Web kapanınca API ve çocuk süreçlerini de kapat
  if (-not $api.HasExited) { taskkill /PID $api.Id /T /F | Out-Null }
  Write-Host '> Durduruldu.' -ForegroundColor Cyan
}

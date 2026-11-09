#!/bin/sh
# Üretim-eşdeğeri yığında HTTPS, çerez bayrakları, CSRF/Origin, iç uç engelleri ve rate limit/XFF bütünlüğü denetimi.
# Gerçek HTTPS yolu: curl -> Caddy (TLS, yerel CA doğrulanır) -> Next server.mjs -> ASP.NET -> PostgreSQL.
# Çerez/parola DEĞERLERİ yazdırılmaz. Yığındaki test verisi tek kullanımlıktır (e-posta example.test).
#   sh deploy/scripts/https-check.sh [CA-dosyası]        Çıkış 0 = tüm denetimler geçti.
set -u
BASE="${BASE:-https://localhost:8443}"
CA="${1:-${CA_FILE:-}}"
[ -z "$CA" ] && { echo "CA dosyası gerekli (Caddy kökü)"; exit 2; }
API="$BASE/api/backend/api/v1"
tmp="$(mktemp -d)"; jar="$tmp/jar"; pass=0; failn=0
C() { curl -s --ssl-no-revoke --cacert "$CA" "$@"; }
ok() { pass=$((pass+1)); printf '  PASS  %s\n' "$1"; }
no() { failn=$((failn+1)); printf '  FAIL  %s  (%s)\n' "$1" "${2:-}"; }
chk() { if [ "$2" = "$3" ]; then ok "$1"; else no "$1" "beklenen=$3 gelen=$2"; fi; }
has() { if echo "$2" | grep -qiE "$3"; then ok "$1"; else no "$1" "bulunamadı: $3"; fi; }
hasnot() { if echo "$2" | grep -qiE "$3"; then no "$1" "olmamalıydı: $3"; else ok "$1"; fi; }

echo "== 1. TLS ve yönlendirme"
chk "yerel CA ile zincir doğrulandı (ssl_verify_result=0)" "$(C -o /dev/null -w '%{ssl_verify_result}' "$BASE/")" 0
loc=$(curl -s -o /dev/null -D - "http://localhost:${HTTP_PORT:-8080}/x?a=1" | tr -d '\r' | grep -i '^location' | cut -d' ' -f2)
chk "HTTP -> HTTPS 308 yönlendirmesi (yol+sorgu korunur)" "$loc" "https://localhost:8443/x?a=1"
chk "CA olmadan bağlantı reddedilir (curl 60)" "$(curl -s -o /dev/null --ssl-no-revoke "$BASE/"; echo $?)" 60

echo "== 2. İç uçlar internete kapalı (kenar 404)"
for p in /api/backend/health/ready /api/backend/health/live /api/backend/metrics /api/backend/openapi/v1.json /metrics /openapi/v1.json; do
  chk "GET $p -> 404" "$(C -o /dev/null -w '%{http_code}' "$BASE$p")" 404
done
up=0; for p in 3000 5432 9100 5080; do curl -s -m 3 -o /dev/null "http://127.0.0.1:$p/" && up=$((up+1)); done
chk "yayımlanmamış portlara (3000/5432/9100/5080) bağlanılamıyor" "$up" 0

echo "== 2b. Yol varyantları kenarı atlatamaz (--path-as-is; güvenlik denetimi B4)"
for p in '/api/backend\health\ready' '/api/backend%5chealth%5cready' '/api/backend/%68ealth/ready' '/api/backend/./health/ready' '/api/backend/x/../health/ready' '//api/backend/health/ready' '/API/backend/health/ready' '/api/Backend/Health/Ready' '/api/backend/health%2fready' '/api/backend/health;x=1/ready' '/api/backend/METRICS' '/api/backend/%6detrics' '/api/backend//health/ready' '/metrics/'; do
  body=$(C --path-as-is -o - -w '
%{http_code}' "$BASE$p" | tail -c 400)
  code=$(printf '%s' "$body" | tail -n 1)
  leak=$(printf '%s' "$body" | grep -ciE '"status":"(Healthy|Unhealthy)"|http_server_requests_total|"checks"')
  if [ "$code" != "200" ] && [ "$leak" = "0" ]; then ok "varyant $p -> $code, iç içerik sızmadı"; else no "varyant $p" "kod=$code sızıntı=$leak"; fi
done

echo "== 2c. Kişisel sayfalar önbelleğe alınmaz (güvenlik denetimi B8)"
for p in /giris /kayit /sepet /hesabim /hesabim/adresler /siparisler /checkout /admin /admin/siparisler; do
  has "Cache-Control no-store: $p" "$(C -D - -o /dev/null "$BASE$p" | tr -d '')" '^cache-control: .*no-store'
done

echo "== 3. Güvenlik başlıkları (HTML)"
h=$(C -D - -o /dev/null "$BASE/" | tr -d '\r')
has "HSTS" "$h" '^strict-transport-security: max-age='
has "CSP script-src nonce/hash" "$h" "^content-security-policy:.*script-src 'self' 'nonce-"
hasnot "CSP script-src unsafe-inline YOK" "$(echo "$h" | grep -i '^content-security-policy' | sed 's/.*script-src//; s/;.*//')" "unsafe-inline|unsafe-eval"
has "frame-ancestors none" "$h" "frame-ancestors 'none'"
has "nosniff" "$h" '^x-content-type-options: nosniff'
has "Referrer-Policy" "$h" '^referrer-policy:'
has "X-Frame-Options DENY" "$h" '^x-frame-options: deny'
hasnot "Server/X-Powered-By sızmıyor" "$h" '^(server|x-powered-by):'
ha=$(C -D - -o /dev/null "$API/products" | tr -d '\r')
has "API yanıtı: nosniff + CSP none" "$ha" "^content-security-policy: default-src 'none'"
hp=$(C -D - -o /dev/null "$API/auth/me" | tr -d '')
has "kişisel API yanıtı (/auth/me): Cache-Control no-store" "$hp" '^cache-control: no-store'
rid=$(C -D - -o /dev/null -H 'X-Request-Id: ../../etc/passwd' "$API/products" | tr -d '\r' | grep -i '^x-request-id' | cut -d' ' -f2)
chk "istemci X-Request-Id kenarda ezilir (UUID biçimi)" "$(echo "$rid" | grep -cE '^[0-9a-f-]{36}$')" 1

echo "== 4. CSRF, Origin, çerezler"
email="drill-$(date +%s)-$$@example.test"; pw="Dr1ll-$(openssl rand -hex 6)!"
body=$(printf '{"email":"%s","password":"%s"}' "$email" "$pw")
C -c "$jar" -D "$tmp/h1" "$API/auth/csrf" -o "$tmp/csrf.json"
tok=$(sed 's/.*"token":"\([^"]*\)".*/\1/' "$tmp/csrf.json")
sc=$(tr -d '\r' < "$tmp/h1" | grep -i '^set-cookie: __Host-ssc.csrf')
has "csrf çerezi __Host- öneki" "$sc" '__Host-ssc\.csrf='
has "csrf çerezi Secure" "$sc" '; secure'
has "csrf çerezi HttpOnly" "$sc" '; httponly'
has "csrf çerezi Path=/" "$sc" '; path=/'
has "csrf çerezi SameSite=Strict" "$sc" 'samesite=strict'
hasnot "csrf çerezi Domain YOK" "$sc" '; domain='
chk "CSRF başlığı olmadan kayıt -> 400/403" "$(C -o /dev/null -w '%{http_code}' -b "$jar" -H 'Content-Type: application/json' -H "Origin: $BASE" -d "$body" "$API/auth/register" | sed 's/^400$/403/')" 403
chk "yabancı Origin ile kayıt -> 403" "$(C -o /dev/null -w '%{http_code}' -b "$jar" -H 'Content-Type: application/json' -H 'Origin: https://evil.example' -H "X-CSRF-TOKEN: $tok" -d "$body" "$API/auth/register")" 403
chk "yanlış CSRF jetonu -> 400/403" "$(C -o /dev/null -w '%{http_code}' -b "$jar" -H 'Content-Type: application/json' -H "Origin: $BASE" -H 'X-CSRF-TOKEN: forged' -d "$body" "$API/auth/register" | sed 's/^400$/403/')" 403
chk "doğru Origin + CSRF ile kayıt -> 201/200" "$(C -o /dev/null -w '%{http_code}' -b "$jar" -c "$jar" -H 'Content-Type: application/json' -H "Origin: $BASE" -H "X-CSRF-TOKEN: $tok" -d "$body" "$API/auth/register" | sed 's/^200$/201/')" 201
C -c "$jar" -b "$jar" -D "$tmp/h2" "$API/auth/csrf" -o "$tmp/csrf.json"; tok=$(sed 's/.*"token":"\([^"]*\)".*/\1/' "$tmp/csrf.json")
code=$(C -o /dev/null -w '%{http_code}' -b "$jar" -c "$jar" -D "$tmp/h3" -H 'Content-Type: application/json' -H "Origin: $BASE" -H "X-CSRF-TOKEN: $tok" -d "$body" "$API/auth/login")
chk "giriş -> 200" "$code" 200
ac=$(tr -d '\r' < "$tmp/h3" | grep -i '^set-cookie: __Host-ssc.auth')
has "auth çerezi __Host- öneki" "$ac" '__Host-ssc\.auth='
has "auth çerezi Secure" "$ac" '; secure'
has "auth çerezi HttpOnly" "$ac" '; httponly'
has "auth çerezi Path=/" "$ac" '; path=/'
has "auth çerezi SameSite=Lax" "$ac" 'samesite=lax'
hasnot "auth çerezi Domain YOK" "$ac" '; domain='
hasnot "auth çerezi kalıcı değil (Expires/Max-Age YOK = oturum çerezi)" "$ac" '; (expires|max-age)='
chk "oturumla /auth/me -> 200" "$(C -o /dev/null -w '%{http_code}' -b "$jar" "$API/auth/me")" 200
chk "çerezsiz /auth/me -> 401" "$(C -o /dev/null -w '%{http_code}' "$API/auth/me")" 401
chk "oturumlu istek Admin ucu -> 403 (rol yok)" "$(C -o /dev/null -w '%{http_code}' -b "$jar" "$API/admin/orders")" 403
cp "$jar" "$tmp/jar-before-logout"
C -c "$jar" -b "$jar" "$API/auth/csrf" -o "$tmp/csrf.json"; tok=$(sed 's/.*"token":"\([^"]*\)".*/\1/' "$tmp/csrf.json")
chk "çıkış -> 204/200" "$(C -o /dev/null -w '%{http_code}' -b "$jar" -c "$jar" -X POST -H "Origin: $BASE" -H "X-CSRF-TOKEN: $tok" "$API/auth/logout" | sed 's/^200$/204/')" 204
chk "çıkıştan sonra ESKİ çerez -> 401 (oturum iptali)" "$(C -o /dev/null -w '%{http_code}' -b "$tmp/jar-before-logout" "$API/auth/me")" 401

echo "== 5. Hız sınırı bütünlüğü (sahte XFF kenarda ezilir)"
tries=0; got429=0
while [ $tries -lt 14 ]; do
  tries=$((tries+1))
  c=$(C -o /dev/null -w '%{http_code}' -H "X-Forwarded-For: 10.$tries.$tries.$tries" -H 'Content-Type: application/json' -H "Origin: $BASE" -d '{"email":"x@example.test","password":"yanlis"}' "$API/auth/login")
  [ "$c" = "429" ] && got429=$((got429+1))
done
[ $got429 -ge 1 ] && ok "farklı sahte XFF ile 14 giriş denemesi -> $got429 kez 429 (kova atlatılamadı)" || no "sahte XFF rate limit'i atlattı" "429 görülmedi"
ra=$(C -D - -o /dev/null -H 'Content-Type: application/json' -H "Origin: $BASE" -d '{"email":"x@example.test","password":"y"}' "$API/auth/login" | tr -d '\r' | grep -i '^retry-after' | cut -d' ' -f2)
[ -n "$ra" ] && ok "429 yanıtında Retry-After=$ra" || no "429 Retry-After yok"

rm -rf "$tmp"
echo "== ÖZET: $pass geçti, $failn kaldı"
[ $failn -eq 0 ]

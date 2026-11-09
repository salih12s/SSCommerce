#!/bin/sh
# Yeniden başlatma / yeniden dağıtım dayanıklılığı + Data Protection anahtar izolasyonu + güvenilir-proxy sınırı.
# Yalnız bu yığının tek kullanımlık test verisiyle çalışır. Çerez/parola DEĞERLERİ yazdırılmaz.
#   sh deploy/scripts/resilience-check.sh <CA-dosyası>        Çıkış 0 = hepsi geçti.
set -u
CA="${1:-}"; [ -z "$CA" ] && { echo "CA dosyası gerekli"; exit 2; }
BASE="${BASE:-https://localhost:8443}"; API="$BASE/api/backend/api/v1"
D="docker compose -f deploy/compose.yml --env-file deploy/.env.stack"
tmp="$(mktemp -d)"; jar="$tmp/jar"; pass=0; failn=0
dk() { MSYS_NO_PATHCONV=1 docker "$@"; }
C() { curl -s --ssl-no-revoke --cacert "$CA" "$@"; }
ok() { pass=$((pass+1)); printf '  PASS  %s\n' "$1"; }
no() { failn=$((failn+1)); printf '  FAIL  %s  (%s)\n' "$1" "${2:-}"; }
chk() { if [ "$2" = "$3" ]; then ok "$1"; else no "$1" "beklenen=$3 gelen=$2"; fi; }
waitready() {
  i=0
  while [ $i -lt 60 ]; do
    s=$($D ps --format '{{.Service}}={{.Health}}' | grep -cE '^(api|web)=healthy$')
    [ "$s" = "2" ] && return 0
    i=$((i+1)); sleep 3
  done
  return 1
}
tok() { C -c "$jar" -b "$jar" "$API/auth/csrf" | sed 's/.*"token":"\([^"]*\)".*/\1/'; }
counts() { $D exec -T db sh -c 'PGPASSWORD="$SSC_APP_PASSWORD" psql -h 127.0.0.1 -U ssc_app -d "$POSTGRES_DB" -X -At -c "select (select count(*) from \"AspNetUsers\")||chr(47)||(select count(*) from products)"'; }

email="resil-$(date +%s)-$$@example.test"; pw="Rs1l-$(openssl rand -hex 6)!"
body=$(printf '{"email":"%s","password":"%s"}' "$email" "$pw")
t=$(tok); C -o /dev/null -b "$jar" -c "$jar" -H 'Content-Type: application/json' -H "Origin: $BASE" -H "X-CSRF-TOKEN: $t" -d "$body" "$API/auth/register"
t=$(tok); chk "giriş" "$(C -o /dev/null -w '%{http_code}' -b "$jar" -c "$jar" -H 'Content-Type: application/json' -H "Origin: $BASE" -H "X-CSRF-TOKEN: $t" -d "$body" "$API/auth/login")" 200
chk "yeniden başlatma ÖNCESİ /auth/me" "$(C -o /dev/null -w '%{http_code}' -b "$jar" "$API/auth/me")" 200
before=$(counts)

echo "== 1. api + web yeniden başlatma (docker restart)"
$D restart api web >/dev/null 2>&1
if waitready; then ok "api+web yeniden sağlıklı"; else no "yeniden başlatma sonrası sağlıklı olunamadı"; fi
chk "yeniden başlatma SONRASI aynı çerez -> /auth/me 200 (Data Protection anahtarları kalıcı)" "$(C -o /dev/null -w '%{http_code}' -b "$jar" "$API/auth/me")" 200

echo "== 2. yeniden dağıtım (konteynerler SİLİNİP yeniden yaratılır; volume'ler kalır)"
$D up -d --force-recreate api web >/dev/null 2>&1
if waitready; then ok "yeniden yaratılan api+web sağlıklı"; else no "yeniden yaratma sonrası sağlıksız"; fi
chk "yeniden yaratma SONRASI aynı çerez -> 200" "$(C -o /dev/null -w '%{http_code}' -b "$jar" "$API/auth/me")" 200

echo "== 3. veritabanı yeniden başlatma (veri korunur)"
$D restart db >/dev/null 2>&1
i=0; until [ "$($D ps --format '{{.Service}}={{.Health}}' | grep -c '^db=healthy$')" = 1 ] || [ $i -ge 30 ]; do i=$((i+1)); sleep 2; done
sleep 12
after=$(counts)
chk "kullanıcı/ürün sayısı değişmedi ($before)" "$after" "$before"
chk "DB yeniden başladıktan sonra oturum hâlâ geçerli" "$(C -o /dev/null -w '%{http_code}' -b "$jar" "$API/auth/me")" 200

echo "== 4. anahtar halkası izolasyonu (BAŞKA ortam bu çerezi çözemez)"
ck=$(grep -F '__Host-ssc.auth' "$jar" | awk '{print $6"="$7}' | head -1)
cname=ssc-drill-otherenv
docker rm -f $cname >/dev/null 2>&1
docker volume create ssc-drill-emptykeys >/dev/null
dk run -d --name $cname --network sscommerce-stack_backend --ip 172.28.77.60 --env-file deploy/.env.stack \
  -e ASPNETCORE_ENVIRONMENT=Production -e ASPNETCORE_URLS=http://172.28.77.60:8080 -e AllowedHosts=172.28.77.60 \
  -e Security__AllowedOrigins__0=https://localhost:8443 -e DataProtection__KeysPath=/keys -e Security__TrustedProxies__0=172.28.77.11 -e Ordering__Enabled=false \
  -v ssc-drill-emptykeys:/keys --entrypoint sh sscommerce-api:local \
  -c 'export ConnectionStrings__Postgres="Host=db;Database=$POSTGRES_DB;Username=ssc_app;Password=$SSC_APP_PASSWORD"; exec dotnet SSCommerce.Api.dll' >/dev/null 2>&1
sleep 20
st=$(dk run --rm --network sscommerce-stack_backend -e CK="$ck" --entrypoint node sscommerce-web:local -e '
fetch("http://172.28.77.60:8080/api/v1/auth/me",{headers:{cookie:process.env.CK}}).then(r=>console.log(r.status)).catch(()=>console.log("ERR"))')
chk "boş anahtar halkalı ikinci örnek: aynı çerez ÇÖZÜLEMEZ -> 401" "$st" 401
docker rm -f $cname >/dev/null 2>&1; docker volume rm ssc-drill-emptykeys >/dev/null 2>&1

echo "== 5. güvenilir-proxy sınırı (yanlış eş + sahte XFF)"
res=$(dk run --rm --network sscommerce-stack_backend --ip 172.28.77.50 --entrypoint node sscommerce-web:local -e '
(async()=>{let n429=0,first;for(let i=1;i<=14;i++){
 const r=await fetch("http://172.28.77.11:3000/api/backend/api/v1/auth/login",{method:"POST",headers:{"content-type":"application/json","origin":"https://localhost:8443","x-forwarded-for":"10."+i+"."+i+"."+i,"x-forwarded-proto":"https"},body:JSON.stringify({email:"x@example.test",password:"y"})});
 if(i===1)first=r.status; if(r.status===429)n429++; }
 console.log(first+" "+n429)})()')
set -- $res
if [ "${2:-0}" -ge 1 ]; then ok "yanlış eşten 14 istek + farklı sahte XFF -> $2 kez 429 (sahte XFF yok sayıldı); ilk=$1"; else no "güvenilmeyen eşin XFF'si kovayı bölebildi" "$res"; fi
if [ "${1:-}" != "429" ]; then ok "kova bölümlemesi: bu eşin ilk isteği başka istemcinin kovasından BAĞIMSIZ"; else no "ilk istek 429" "$res"; fi

rm -rf "$tmp"
echo "== ÖZET: $pass geçti, $failn kaldı"
[ $failn -eq 0 ]

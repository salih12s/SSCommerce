#!/bin/sh
# DRILL yığını için test hesapları: bir Admin (gerçek --bootstrap-admin komutuyla) ve bir Depo hesabı (kayıt; rolü Admin API'siyle verilir).
# Kimlik bilgileri <çıktı-env-dosyası>'na yazılır (depo DIŞINDA olmalı), EKRANA YAZILMAZ.
#   sh deploy/scripts/stack-fixtures.sh <CA-dosyası> <çıktı-env-dosyası>
set -eu
CA="${1:?CA dosyası}"; OUT="${2:?çıktı env dosyası (depo dışı)}"
case "$OUT" in "$(cd "$(dirname "$0")/../.." && pwd)"/*) echo "çıktı dosyası depo içinde olamaz"; exit 2 ;; esac
BASE="${BASE:-https://localhost:8443}"; API="$BASE/api/backend/api/v1"
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT INT TERM
C() { curl -s --ssl-no-revoke --cacert "$CA" "$@"; }
reg() { # e-posta parola -> kayıt (CSRF'li)
  jar="$tmp/j"; : > "$jar"
  t=$(C -c "$jar" -b "$jar" "$API/auth/csrf" | sed 's/.*"token":"\([^"]*\)".*/\1/')
  C -o /dev/null -w '%{http_code}' -b "$jar" -c "$jar" -H 'Content-Type: application/json' -H "Origin: $BASE" -H "X-CSRF-TOKEN: $t" \
    -d "$(printf '{"email":"%s","password":"%s"}' "$1" "$2")" "$API/auth/register"
}
sfx="$(date +%s)"
admin_email="admin-$sfx@example.test"; admin_pw="Ad-$(openssl rand -hex 8)!A1"
wh_email="depo-$sfx@example.test";   wh_pw="Wh-$(openssl rand -hex 8)!A1"
[ "$(reg "$admin_email" "$admin_pw")" = "201" ] || { echo "Admin hesabı kaydı başarısız"; exit 1; }
[ "$(reg "$wh_email" "$wh_pw")" = "201" ] || { echo "Depo hesabı kaydı başarısız"; exit 1; }
out=$(sh "$here/api-command.sh" bootstrap-admin "$admin_email" 2>&1 | tail -1)
case "$out" in *"ilk Admin atandı"*) ;; *) echo "Admin ataması başarısız: ${out}"; exit 1 ;; esac
umask 077
{
  echo "PROD_BASE_URL=$BASE"
  echo "PROD_ADMIN_EMAIL=$admin_email"; echo "PROD_ADMIN_PASSWORD=$admin_pw"
  echo "PROD_WAREHOUSE_EMAIL=$wh_email"; echo "PROD_WAREHOUSE_PASSWORD=$wh_pw"
} > "$OUT"
rm -rf "$tmp"
echo "stack-fixtures: Admin (bootstrap) ve Depo hesabı hazır; kimlik bilgileri $OUT dosyasında (gösterilmedi)."

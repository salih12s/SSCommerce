#!/bin/sh
# API imajındaki TEK SEFERLİK komutları (--bootstrap-admin, --seed-demo-catalog) çalıştırır. HTTP ucu değildir (D-030, D-070).
# Bu komutlar yalnız Development ortamında ve (ilk Admin için) LOOPBACK veritabanına karşı çalışır. Üretim yığınında bunu sağlamak için
# tek kullanımlık konteyner, veritabanı konteynerinin AĞ AD ALANINA katılır (docker --network container:...): Host=127.0.0.1 orada gerçekten
# PostgreSQL'dir; ağ dışından erişim açılmaz. Uygulama çalışma rolü (ssc_app) kullanılır, sahip/süper kullanıcı DEĞİL.
#   sh deploy/scripts/api-command.sh bootstrap-admin <kayıtlı-hesap-e-postası>
#   sh deploy/scripts/api-command.sh seed-demo-catalog        (YALNIZ drill/demo yığını; gerçek mağaza kataloğu Admin panelinden girilir)
set -eu
cmd="${1:-}"; email="${2:-}"
db="${DB_CONTAINER:-sscommerce-stack-db-1}"
img="${API_IMAGE:-sscommerce-api:local}"
case "$cmd" in
  bootstrap-admin) [ -n "$email" ] || { echo "e-posta gerekli"; exit 2; }; flag="--bootstrap-admin"; interactive="-i" ;;
  seed-demo-catalog) flag="--seed-demo-catalog"; interactive="" ;;
  *) echo "kullanım: api-command.sh bootstrap-admin <e-posta> | seed-demo-catalog"; exit 2 ;;
esac
# Güvenlik denetimi B3: konteynere süper kullanıcı/owner/backup parolaları GEÇİRİLMEZ; yalnız çalışma rolünün parolası ve veritabanı adı.
stackenv="$(dirname "$0")/../.env.stack"
envf="$(mktemp)"; trap 'rm -f "$envf"' EXIT INT TERM
umask 077
grep -E '^(POSTGRES_DB|SSC_APP_PASSWORD)=' "$stackenv" > "$envf"
hp() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi; }
run() {
  MSYS_NO_PATHCONV=1 docker run --rm $interactive --network "container:$db" --env-file "$(hp "$envf")" \
    -e ASPNETCORE_ENVIRONMENT=Development -e Logging__LogLevel__Default=Warning \
    --entrypoint sh "$img" \
    -c 'export ConnectionStrings__Postgres="Host=127.0.0.1;Port=5432;Database=$POSTGRES_DB;Username=ssc_app;Password=$SSC_APP_PASSWORD"; exec dotnet SSCommerce.Api.dll '"$flag"
}
if [ "$cmd" = "bootstrap-admin" ]; then printf '%s\n%s\n' "$email" "$email" | run; else run; fi

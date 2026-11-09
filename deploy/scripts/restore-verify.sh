#!/bin/sh
# Yedek geri yükleme provası. KAYNAĞA/ÜRETİME DOKUNMAZ: tek kullanımlık, ağsız, tmpfs'li bir PostgreSQL konteynerinde geri yükler,
# manifest sayılarını ve iş değişmezlerini doğrular, sonra YALNIZ kendi açtığı konteyneri kaldırır.
#   sh deploy/scripts/restore-verify.sh <yedek-adı-uzantısız>      (yedek: sscommerce-stack-backups volume'ünde)
#   sh deploy/scripts/restore-verify.sh --file /yol/x.dump [/yol/x.manifest]   (ana makinedeki bir döküm; ör. geliştirme yedeği)
# Çıkış 0 = tüm kontroller geçti.
set -eu
export MSYS_NO_PATHCONV=1   # Git Bash (Windows) /tmp gibi yolları bozmasın
here="$(cd "$(dirname "$0")/.." && pwd)"
img="postgres:17-alpine@sha256:266d3c257594e195b309ae9d1805e54c788f1d7b375f56bf6c367aac6c917e7b"
work="$(mktemp -d)"
cname="ssc-restore-drill-$$"
pw="$(openssl rand -hex 16)"
cleanup() { docker rm -f "$cname" >/dev/null 2>&1 || true; rm -rf "$work" 2>/dev/null || true; }
trap cleanup EXIT INT TERM

if [ "${1:-}" = "--file" ]; then
  cp "$2" "$work/x.dump"; [ -n "${3:-}" ] && cp "$3" "$work/x.manifest" || : > "$work/x.manifest"
else
  name="$1"
  case "$name" in *[!A-Za-z0-9_-]*|"") echo "geçersiz yedek adı (yalnız harf, rakam, _ ve -)" >&2; exit 2 ;; esac
  docker run --rm -v sscommerce-stack-backups:/b:ro "$img" sh -c "cd /b && sha256sum -c ${name}.dump.sha256 >&2 && cat ${name}.dump" > "$work/x.dump"
  docker run --rm -v sscommerce-stack-backups:/b:ro "$img" cat "/b/${name}.manifest" > "$work/x.manifest"
fi
echo "restore: döküm $(wc -c < "$work/x.dump") bayt"

docker run -d --name "$cname" --network none --tmpfs /var/lib/postgresql/data:rw -e POSTGRES_PASSWORD="$pw" -e POSTGRES_DB=restoretest "$img" >/dev/null
i=0; until docker exec "$cname" pg_isready -q -U postgres -d restoretest 2>/dev/null; do i=$((i+1)); [ $i -ge 40 ] && { echo "restore: geçici sunucu açılmadı" >&2; exit 1; }; sleep 1; done
sleep 2
hp() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi; }   # docker.exe için yerel yol
docker cp "$(hp "$work/x.dump")" "$cname:/tmp/x.dump"
docker cp "$(hp "$here/db/invariants.sql")" "$cname:/tmp/invariants.sql"
docker exec "$cname" pg_restore -U postgres -d restoretest --no-owner --no-acl --exit-on-error /tmp/x.dump
echo "restore: pg_restore tamam"

fail=0
# 1) Manifest sayıları
if [ -s "$work/x.manifest" ]; then
  while IFS='|' read -r kind a b; do
    case "$kind" in
      count) if [ "$a" = "__triggers" ]; then q='select count(*) from pg_trigger where not tgisinternal'; else q="select count(*) from public.\"$a\""; fi; got=$(docker exec "$cname" psql -U postgres -d restoretest -At -c "$q"); [ "$got" = "$b" ] && echo "  ok  sayı $a=$got" || { echo "  HATA sayı $a beklenen=$b gelen=$got"; fail=1; } ;;
      migration) got=$(docker exec "$cname" psql -U postgres -d restoretest -At -c "select count(*) from \"__EFMigrationsHistory\" where \"MigrationId\"='$a'"); [ "$got" = "1" ] && echo "  ok  migration $a" || { echo "  HATA migration yok: $a"; fail=1; } ;;
    esac
  done < "$work/x.manifest"
else
  echo "  (manifest yok: yalnız değişmezler doğrulanır)"
fi
# 2) İş değişmezleri (0 beklenir)
docker exec "$cname" psql -U postgres -d restoretest -At -f /tmp/invariants.sql > "$work/inv.txt"
while IFS='|' read -r rule n; do
  [ -z "$rule" ] && continue
  [ "$n" = "0" ] && echo "  ok  değişmez $rule" || { echo "  HATA değişmez $rule ihlal=$n"; fail=1; }
done < "$work/inv.txt"
[ $fail -eq 0 ] && echo "restore: TÜM KONTROLLER GEÇTİ" || echo "restore: BAŞARISIZ"
exit $fail

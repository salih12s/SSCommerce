#!/bin/sh
# Göç işi: idempotent şema betiği + yetki ince ayarı. Sahip rolüyle çalışır. Yeniden çalıştırmak güvenlidir (idempotent).
# Ortam: PGHOST PGPORT PGDATABASE PGUSER(=ssc_owner) PGPASSWORD. Parola hiçbir yere yazılmaz.
set -eu
echo "migrate: veritabanı bekleniyor"
i=0
until pg_isready -q -t 3; do i=$((i+1)); [ "$i" -ge 40 ] && { echo "migrate: veritabanı hazır değil" >&2; exit 1; }; sleep 2; done
echo "migrate: şema uygulanıyor (idempotent)"
psql -X -q -v ON_ERROR_STOP=1 -f /migrations/schema.sql >/dev/null
echo "migrate: yetkiler uygulanıyor"
psql -X -q -v ON_ERROR_STOP=1 -f /migrations/post-migrate.sql
echo "migrate: tamam; son kayıt: $(psql -X -At -c 'select "MigrationId" from "__EFMigrationsHistory" order by 1 desc limit 1')"

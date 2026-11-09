#!/bin/sh
# Konteyner içinde çalışır (compose "backup" servisi). Tutarlı pg_dump -Fc + sağlama toplamı + kayıt sayıları bildirimi.
# Çıktı /backups altında: <ad>.dump, <ad>.dump.sha256, <ad>.manifest. Parola ve veri satırı yazmaz.
set -eu
ts=$(date -u +%Y%m%dT%H%M%SZ)
name="sscommerce-${ts}"
umask 077
pg_dump -Fc --no-owner --no-acl -f "/backups/${name}.dump"
pg_restore --list "/backups/${name}.dump" >/dev/null
( cd /backups && sha256sum "${name}.dump" > "${name}.dump.sha256" )
{
  echo "# manifest ${name} (UTC ${ts}); satır sayıları dökümden hemen SONRA alınır (canlı yazma sürerken küçük sapma olabilir; sessiz pencerede alın)"
  psql -X -At -c "select 'migration|' || \"MigrationId\" from \"__EFMigrationsHistory\" order by 1"
  trg=$(psql -X -At -c "select count(*) from pg_trigger where not tgisinternal") || exit 1
  echo "count|__triggers|${trg}"
  tables=$(psql -X -At -c "select tablename from pg_tables where schemaname='public' order by 1") || exit 1
  for t in $tables; do
    cnt=$(psql -X -At -c "select count(*) from public.\"$t\"") || exit 1
    echo "count|$t|${cnt}"
  done
} > "/backups/${name}.manifest"
echo "backup: ${name}.dump ($(wc -c < "/backups/${name}.dump") bayt) tamam"

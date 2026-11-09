#!/bin/sh
# YÜKSELTME PROVASI (yıkıcı değil, kaynağa dokunmaz): eski şemalı bir dökümü TEK KULLANIMLIK sunucuya geri yükler, üretim göç imajıyla
# (sscommerce-migrate) en yeni migration'a yükseltir, ikinci kez çalıştırıp idempotentliği, sonra veri/iş değişmezlerini ve rol yetkilerini doğrular.
#   sh deploy/scripts/upgrade-drill.sh <eski-şemalı-döküm.dump>        (ör. geliştirme yedeğinin KOPYASI; dosyaya yazılmaz)
# Çıkış 0 = hepsi geçti. Sunucu: ağsız, tmpfs, rastgele parolalar (yazdırılmaz). Yalnız kendi açtığı konteyneri kaldırır.
set -u
export MSYS_NO_PATHCONV=1
dump="${1:?döküm dosyası}"
here="$(cd "$(dirname "$0")/.." && pwd)"
img="postgres:17-alpine@sha256:266d3c257594e195b309ae9d1805e54c788f1d7b375f56bf6c367aac6c917e7b"
mig="${MIGRATE_IMAGE:-sscommerce-migrate:local}"
cname="ssc-upgrade-drill-$$"; db=sscommerce
hp() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi; }
rnd() { openssl rand -hex 12; }
OW=$(rnd); AP=$(rnd); BK=$(rnd); SU=$(rnd)
pass=0; failn=0
ok() { pass=$((pass+1)); printf '  PASS  %s\n' "$1"; }
no() { failn=$((failn+1)); printf '  FAIL  %s  (%s)\n' "$1" "${2:-}"; }
chk() { if [ "$2" = "$3" ]; then ok "$1"; else no "$1" "beklenen=$3 gelen=$2"; fi; }
cleanup() { docker rm -f "$cname" >/dev/null 2>&1 || true; }
trap cleanup EXIT INT TERM
q() { docker exec -e PGPASSWORD="$OW" "$cname" psql -h 127.0.0.1 -U ssc_owner -d $db -X -At -c "$1"; }

docker run -d --name "$cname" --network none --tmpfs /var/lib/postgresql/data:rw \
  -e POSTGRES_DB=$db -e POSTGRES_USER=ssc_pgadmin -e POSTGRES_PASSWORD="$SU" \
  -e SSC_OWNER_PASSWORD="$OW" -e SSC_APP_PASSWORD="$AP" -e SSC_BACKUP_PASSWORD="$BK" \
  -v "$(hp "$here/db/init"):/docker-entrypoint-initdb.d:ro" "$img" >/dev/null
i=0; until q 'select 1' >/dev/null 2>&1; do i=$((i+1)); [ $i -ge 60 ] && { echo "sunucu açılmadı"; docker logs "$cname" 2>&1 | tail -5; exit 1; }; sleep 1; done
echo "== eski şemalı döküm geri yükleniyor ($(wc -c < "$dump") bayt)"
docker cp "$(hp "$dump")" "$cname:/tmp/old.dump"
docker exec -e PGPASSWORD="$OW" "$cname" pg_restore -h 127.0.0.1 -U ssc_owner -d $db --no-owner --no-acl --exit-on-error /tmp/old.dump
before_mig=$(q 'select count(*) from "__EFMigrationsHistory"')
before_prod=$(q 'select count(*) from products'); before_var=$(q 'select count(*) from product_variants'); before_img=$(q 'select count(*) from product_images'); before_cat=$(q 'select count(*) from categories')
echo "  eski şema: migration=$before_mig ürün=$before_prod varyant=$before_var kategori=$before_cat"
chk "yükseltme ÖNCESİ admin_audit_log yok (eski şema)" "$(q "select coalesce(to_regclass('public.admin_audit_log')::text,'yok')")" yok

# Beklenen migration sayısı sabit yazılmaz: göç imajındaki idempotent betiğin kaydettiği migration sayısından türetilir (yeni migration eklenince betik bozulmaz).
expected=$(docker run --rm --entrypoint sh "$mig" -c 'grep -c "INSERT INTO \"__EFMigrationsHistory\"" /migrations/schema.sql')
runmig() { docker run --rm --network "container:$cname" -e PGHOST=127.0.0.1 -e PGDATABASE=$db -e PGUSER=ssc_owner -e PGPASSWORD="$OW" "$mig" 2>&1; }
echo "== göç imajı (1. çalıştırma)"; out=$(runmig); rc=$?; echo "$out" | sed 's/^/    /'
chk "göç imajı çıkış kodu" "$rc" 0
chk "migration sayısı = göç imajındaki betiğin migration sayısı ($expected; son: $(q 'select "MigrationId" from "__EFMigrationsHistory" order by 1 desc limit 1'))" "$(q 'select count(*) from "__EFMigrationsHistory"')" "$expected"
chk "admin_audit_log oluştu" "$(q "select coalesce(to_regclass('public.admin_audit_log')::text,'yok')")" admin_audit_log
chk "ürün sayısı değişmedi" "$(q 'select count(*) from products')" "$before_prod"
chk "varyant sayısı değişmedi" "$(q 'select count(*) from product_variants')" "$before_var"
chk "görsel sayısı değişmedi" "$(q 'select count(*) from product_images')" "$before_img"
chk "kategori sayısı değişmedi" "$(q 'select count(*) from categories')" "$before_cat"
chk "ürünlerde version sütunu eklendi ve hepsi 1" "$(q 'select count(*) from products where version = 1')" "$before_prod"
echo "== göç imajı (2. çalıştırma: idempotent olmalı)"; out2=$(runmig); rc2=$?
chk "ikinci çalıştırma çıkış kodu" "$rc2" 0
chk "ikinci çalıştırma sonrası migration sayısı hâlâ $expected" "$(q 'select count(*) from "__EFMigrationsHistory"')" "$expected"

echo "== iş değişmezleri"
docker cp "$(hp "$here/db/invariants.sql")" "$cname:/tmp/inv.sql"
docker exec -e PGPASSWORD="$OW" "$cname" psql -h 127.0.0.1 -U ssc_owner -d $db -X -At -f /tmp/inv.sql > /tmp/ssc-inv.$$ 2>&1
while IFS='|' read -r rule n; do [ -z "$rule" ] && continue; chk "değişmez $rule" "$n" 0; done < /tmp/ssc-inv.$$; rm -f /tmp/ssc-inv.$$

echo "== çalışma rolü yükseltilmiş şemada asgari yetkili"
ta() { docker exec -e PGPASSWORD="$AP" "$cname" psql -h 127.0.0.1 -U ssc_app -d $db -X -At -c "$1" >/dev/null 2>&1; echo $?; }
chk "ssc_app SELECT products" "$(ta 'select 1 from products limit 1')" 0
chk "ssc_app DELETE admin_audit_log reddedilir" "$(ta 'delete from admin_audit_log')" 1
chk "ssc_app DISABLE TRIGGER reddedilir" "$(ta 'alter table orders disable trigger all')" 1
chk "ssc_app DROP TABLE reddedilir" "$(ta 'drop table carts')" 1
chk "ssc_app tablo sahibi değil (orders sahibi ssc_owner)" "$(q "select tableowner from pg_tables where tablename='orders'")" ssc_owner

echo "== ÖZET: $pass geçti, $failn kaldı"
[ $failn -eq 0 ]

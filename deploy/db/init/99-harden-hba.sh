#!/bin/sh
# SON init betiği (önceki betikler yerel soket "trust"ına bağlıdır). Güvenlik denetimi B3: resmi postgres imajı TCP loopback (127.0.0.1, ::1) için "trust" yazar;
# bu, veritabanı konteynerinin ağ ad alanına katılan her sürecin PAROLASIZ (süper kullanıcı dahil) bağlanması demektir.
# TCP satırları scram-sha-256'ya çevrilir; yerel Unix soketi (yalnız konteyner içi) olduğu gibi kalır. Yalnız YENİ kümede (boş veri dizini) çalışır;
# var olan kümede aynı dönüşümü elle uygulayın: docs/OPERATIONS.md "pg_hba sertleştirme".
set -eu
hba="$PGDATA/pg_hba.conf"
sed -i -E 's#^(host[[:space:]]+(all|replication)[[:space:]]+all[[:space:]]+(127\.0\.0\.1/32|::1/128)[[:space:]]+)trust#\1scram-sha-256#' "$hba"
grep -qE '^host[[:space:]].*(127\.0\.0\.1/32|::1/128)[[:space:]]+trust' "$hba" && { echo "pg_hba sertleştirme başarısız" >&2; exit 1; }
echo "pg_hba: TCP loopback kuralları scram-sha-256"

#!/bin/sh
# deploy/.env.stack üretir (rastgele sırlar). Sırları EKRANA YAZMAZ. Var olan dosyanın üzerine YAZMAZ.
set -eu
cd "$(dirname "$0")/.."
if [ -e .env.stack ]; then
  echo "deploy/.env.stack zaten var; üzerine yazılmadı."
  exit 0
fi
rnd() { openssl rand -base64 36 | tr -d '/+=\n' | cut -c1-40; }
umask 077
sed -e "s/^PG_SUPERUSER_PASSWORD=.*/PG_SUPERUSER_PASSWORD=$(rnd)/" \
    -e "s/^SSC_OWNER_PASSWORD=.*/SSC_OWNER_PASSWORD=$(rnd)/" \
    -e "s/^SSC_APP_PASSWORD=.*/SSC_APP_PASSWORD=$(rnd)/" \
    -e "s/^SSC_BACKUP_PASSWORD=.*/SSC_BACKUP_PASSWORD=$(rnd)/" stack.env.example > .env.stack
echo "deploy/.env.stack üretildi (sırlar gösterilmedi)."

#!/bin/sh
# İlk kurulumda (boş veri dizini) BİR KEZ çalışır; var olan veriye dokunmaz. Süper kullanıcı (POSTGRES_USER) yalnız bakım içindir:
# uygulama ve göç ona BAĞLANMAZ. Parolalar ortamdan gelir, çıktıya yazılmaz.
#   ssc_owner : şema sahibi + göç (DDL). Uygulama çalışırken kullanılmaz.
#   ssc_app   : çalışma zamanı. DDL yok, tablo sahibi değil (tetikleyicileri devre dışı bırakamaz), TRUNCATE yok.
#   ssc_backup: yalnız okuma (pg_read_all_data), pg_dump için.
set -eu
: "${SSC_OWNER_PASSWORD:?}" "${SSC_APP_PASSWORD:?}" "${SSC_BACKUP_PASSWORD:?}"
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v owner_pw="$SSC_OWNER_PASSWORD" -v app_pw="$SSC_APP_PASSWORD" -v backup_pw="$SSC_BACKUP_PASSWORD" -v dbname="$POSTGRES_DB" <<'SQL'
CREATE ROLE ssc_owner  LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'owner_pw';
CREATE ROLE ssc_app    LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'app_pw';
CREATE ROLE ssc_backup LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'backup_pw';
GRANT pg_read_all_data TO ssc_backup;

ALTER DATABASE :"dbname" OWNER TO ssc_owner;
REVOKE ALL ON DATABASE :"dbname" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"dbname" TO ssc_app, ssc_backup, ssc_owner;
ALTER SCHEMA public OWNER TO ssc_owner;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO ssc_app, ssc_backup;

-- Sahibin ileride oluşturacağı nesnelere uygulama rolü otomatik asgari yetki alır (TRUNCATE/TRIGGER/REFERENCES YOK).
ALTER DEFAULT PRIVILEGES FOR ROLE ssc_owner IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ssc_app;
ALTER DEFAULT PRIVILEGES FOR ROLE ssc_owner IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO ssc_app;
SQL

-- Her göçten sonra çalışır (idempotent). Amaç: çalışma zamanı rolü (ssc_app) asgari yetkiyle kalsın.
-- Sahibi başka rol olan nesneler (ör. yedekten geri yüklenmiş veritabanı) sahip rolüne devredilir.
DO $$
DECLARE r record;
BEGIN
  FOR r IN SELECT c.relname, c.relkind FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE n.nspname = 'public' AND c.relkind IN ('r','S') AND pg_get_userbyid(c.relowner) <> current_user LOOP
    EXECUTE format('ALTER %s public.%I OWNER TO %I', CASE r.relkind WHEN 'S' THEN 'SEQUENCE' ELSE 'TABLE' END, r.relname, current_user);
  END LOOP;
END $$;

GRANT USAGE ON SCHEMA public TO ssc_app, ssc_backup;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ssc_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO ssc_app;
REVOKE TRUNCATE, TRIGGER, REFERENCES ON ALL TABLES IN SCHEMA public FROM ssc_app;

-- Göç geçmişi: uygulama yalnız okur (sağlık kontrolü bekleyen göçü buradan görür).
REVOKE INSERT, UPDATE, DELETE ON TABLE public."__EFMigrationsHistory" FROM ssc_app;

-- Derinlemesine savunma: yalnız-ekleme (append-only) tablolarda tetikleyici zaten UPDATE/DELETE'i reddeder;
-- yetkiyi de kaldır ki ikinci bir engel olsun. Tetikleyici adı desenine göre bulunur (elle liste tutulmaz).
DO $$
DECLARE t text;
BEGIN
  FOR t IN SELECT DISTINCT c.relname FROM pg_trigger g JOIN pg_class c ON c.oid = g.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE NOT g.tgisinternal AND n.nspname = 'public' AND g.tgname LIKE 'trg\_%\_no\_update\_delete' LOOP
    EXECUTE format('REVOKE UPDATE, DELETE ON TABLE public.%I FROM ssc_app', t);
  END LOOP;
  IF to_regclass('public.orders') IS NOT NULL THEN
    REVOKE DELETE ON TABLE public.orders FROM ssc_app;
  END IF;
END $$;

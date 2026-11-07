using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SSCommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Faz 8 (docs/CONTRACT_PHASE_8.md). Tek ileri-yönlü EKLEMELİ migration: DROP yok, veri yazan deyim yok.
            // version: DEFAULT 1 sabit olduğundan PostgreSQL 11+ tabloyu yeniden yazmaz; mevcut satırlar 1 olur. CHECK'ler tabloyu bir kez tarar.
            // Dizinler CONCURRENTLY değildir (migration transaction içindedir); yerel/küçük veri için kabul edilir, büyük canlı tabloda Faz 9 notu.
            // Faz 8 (docs/CONTRACT_PHASE_8.md). Tek ileri-yönlü EKLEMELİ migration: DROP yok, veri yazan deyim yok.
            // version: DEFAULT 1 sabit olduğundan PostgreSQL 11+ tabloyu yeniden yazmaz; mevcut satırlar 1 olur. CHECK'ler tabloyu bir kez tarar.
            // Dizinler CONCURRENTLY değildir (migration transaction içindedir); yerel/küçük veri için kabul edilir, büyük canlı tabloda Faz 9 notu.
            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "products",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "product_variants",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "categories",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "admin_audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    changes = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_audit_log", x => x.id);
                    table.CheckConstraint("ck_admin_audit_log_action_format", "action ~ '^[a-z]+(\\.[a-z_]+)+$'");
                    table.CheckConstraint("ck_admin_audit_log_changes_object", "jsonb_typeof(changes) = 'object' AND octet_length(changes::text) <= 8192 AND NOT (changes ?| ARRAY['password', 'password_hash', 'security_stamp', 'token', 'email', 'phone', 'address', 'street_address'])");
                    table.CheckConstraint("ck_admin_audit_log_target_type", "target_type IN ('category', 'product', 'variant', 'warehouse', 'user', 'order')");
                    table.ForeignKey(
                        name: "fk_admin_audit_log_actor_user",
                        column: x => x.actor_user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_occurred_at_id",
                table: "stock_movements",
                columns: new[] { "occurred_at", "id" },
                descending: new bool[0]);

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_version_positive",
                table: "products",
                sql: "version >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_variants_version_positive",
                table: "product_variants",
                sql: "version >= 1");

            migrationBuilder.CreateIndex(
                name: "ix_orders_open_created_at",
                table: "orders",
                columns: new[] { "created_at", "id" },
                filter: "status IN ('placed', 'processing')");

            migrationBuilder.CreateIndex(
                name: "ix_orders_open_user",
                table: "orders",
                column: "user_id",
                filter: "status IN ('placed', 'processing')");

            migrationBuilder.CreateIndex(
                name: "ix_order_status_history_occurred_at_id",
                table: "order_status_history",
                columns: new[] { "occurred_at", "id" },
                descending: new bool[0]);

            migrationBuilder.AddCheckConstraint(
                name: "ck_categories_version_positive",
                table: "categories",
                sql: "version >= 1");

            migrationBuilder.CreateIndex(
                name: "ix_admin_audit_log_actor_user_id",
                table: "admin_audit_log",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_admin_audit_log_occurred_at_id",
                table: "admin_audit_log",
                columns: new[] { "occurred_at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_admin_audit_log_target_type_target_id_occurred_at_id",
                table: "admin_audit_log",
                columns: new[] { "target_type", "target_id", "occurred_at", "id" },
                descending: new[] { false, false, true, true });

            // ---- Faz 8 DB korumaları (docs/CONTRACT_PHASE_8.md "Persistence"). Hepsi EKLEMELİDİR: mevcut nesne düşürülmez, veri yazılmaz. ----

            // Yalnız-ekle denetim kaydı. SQLSTATE 23001 (restrict_violation): D-064 trigger'larıyla aynı kod. Genel işlev: TG_TABLE_NAME hangisi olduğunu söyler.
            // DROP TABLE trigger'ı yok sayar (Down için gerekli); korunan şey veri bütünlüğüdür, şema değil.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION append_only_reject_change() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                BEGIN
                    RAISE EXCEPTION '% is append-only: % is not allowed', TG_TABLE_NAME, TG_OP
                        USING ERRCODE = 'restrict_violation';
                END
                $fn$;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_admin_audit_log_no_update_delete
                BEFORE UPDATE OR DELETE ON admin_audit_log
                FOR EACH ROW EXECUTE FUNCTION append_only_reject_change();
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_admin_audit_log_no_truncate
                BEFORE TRUNCATE ON admin_audit_log
                FOR EACH STATEMENT EXECUTE FUNCTION append_only_reject_change();
                """);

            // Kategori üst değişikliği: döngü ve derinlik (en çok 5 düzey, kök = 1). Yalnız parent_id gerçekten değişince ve boş değilse çalışır;
            // ad/aktiflik/sıra değişikliği ve kök olarak ekleme kilit ALMAZ. Kilit, ağaç genelinde TEK advisory kilittir ve backend'in satır
            // FOR UPDATE'inden ÖNCE alınır (aksi halde bu kilit ile FK 'FOR KEY SHARE' arasında 40P01 olur). Atalar satır kilidi olmadan,
            // en çok 5 adım yürünür. VOLATILE: işlevin her sorgusu yeni anlık görüntü alır, bu yüzden kilidi bekleyen işlem öncekinin commit'ini görür
            // (yalnız READ COMMITTED altında doğru; tüm yönetici yazmaları öyledir). Alt ağaç yüksekliği özyinelemeli CTE ile, düzey sınırıyla ölçülür.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION categories_guard_parent() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                DECLARE
                    walker uuid;
                    ancestor_count integer := 0;
                    subtree_height integer := 0;
                BEGIN
                    IF NEW.parent_id IS NULL OR NEW.parent_id = NEW.id THEN
                        RETURN NEW; -- kök ya da kendine bağlama (ck_categories_parent_not_self reddeder)
                    END IF;
                    IF TG_OP = 'UPDATE' THEN
                        IF NEW.parent_id IS NOT DISTINCT FROM OLD.parent_id THEN
                            RETURN NEW;
                        END IF;
                    END IF;

                    PERFORM pg_advisory_xact_lock(hashtextextended('catalog:category-tree', 0));

                    walker := NEW.parent_id;
                    WHILE walker IS NOT NULL LOOP
                        IF walker = NEW.id THEN
                            RAISE EXCEPTION 'categories: parent change would create a cycle'
                                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_categories_no_cycle', TABLE = 'categories';
                        END IF;
                        ancestor_count := ancestor_count + 1;
                        IF ancestor_count >= 5 THEN
                            RAISE EXCEPTION 'categories: tree depth would exceed 5'
                                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_categories_max_depth', TABLE = 'categories';
                        END IF;
                        SELECT c.parent_id INTO walker FROM categories c WHERE c.id = walker;
                    END LOOP;

                    IF TG_OP = 'UPDATE' THEN
                        WITH RECURSIVE sub(id, lvl) AS (
                            SELECT c.id, 1 FROM categories c WHERE c.parent_id = NEW.id
                            UNION ALL
                            SELECT c.id, s.lvl + 1 FROM categories c JOIN sub s ON c.parent_id = s.id WHERE s.lvl < 6
                        )
                        SELECT coalesce(max(lvl), 0) INTO subtree_height FROM sub;
                    END IF;

                    IF ancestor_count + 1 + subtree_height > 5 THEN
                        RAISE EXCEPTION 'categories: tree depth would exceed 5'
                            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_categories_max_depth', TABLE = 'categories';
                    END IF;
                    RETURN NEW;
                END
                $fn$;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_categories_guard_parent
                BEFORE INSERT OR UPDATE OF parent_id ON categories
                FOR EACH ROW EXECUTE FUNCTION categories_guard_parent();
                """);

            // Sipariş durum geçişi (D-067 madde 2). Bilinmeyen hedef durumu burada reddedilmez: ck_orders_status reddeder (kısıt adı korunur).
            // Rol yetkisi OrderStateMachine'dedir; bu trigger yalnız geçiş tablosunu zorlar. trg_orders_restrict_update'ten SONRA çalışır (alfabetik).
            migrationBuilder.Sql(
                """
                CREATE FUNCTION orders_guard_status_transition() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                BEGIN
                    IF NEW.status IS NULL OR NEW.status NOT IN ('placed', 'processing', 'shipped', 'completed', 'cancelled') THEN
                        RETURN NEW; -- NULL için NOT NULL, bilinmeyen metin için ck_orders_status konuşur
                    END IF;
                    IF (OLD.status, NEW.status) IN (
                        ('placed', 'processing'), ('placed', 'cancelled'),
                        ('processing', 'shipped'), ('processing', 'cancelled'),
                        ('shipped', 'completed')) THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'orders: status transition % -> % is not allowed', OLD.status, NEW.status
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_orders_status_transition', TABLE = 'orders';
                END
                $fn$;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_orders_status_transition
                BEFORE UPDATE OF status ON orders
                FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
                EXECUTE FUNCTION orders_guard_status_transition();
                """);

            // Yeni sipariş yalnız 'placed' doğar. Bilinmeyen metinler yine ck_orders_status'a bırakılır.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION orders_guard_insert_status() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                BEGIN
                    IF NEW.status IN ('processing', 'shipped', 'completed', 'cancelled') THEN
                        RAISE EXCEPTION 'orders: a new order must start as placed, not %', NEW.status
                            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_orders_insert_status', TABLE = 'orders';
                    END IF;
                    RETURN NEW;
                END
                $fn$;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_orders_insert_placed
                BEFORE INSERT ON orders
                FOR EACH ROW WHEN (NEW.status IS DISTINCT FROM 'placed')
                EXECUTE FUNCTION orders_guard_insert_status();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Denetim kaydı varken geri dönüş YOKTUR: tablo düşerse yalnız-ekle kayıtlar kaybolurdu. Hata, migration'ın tek transaction'ını geri sarar.
            migrationBuilder.Sql(
                """
                DO $do$
                BEGIN
                    IF EXISTS (SELECT 1 FROM admin_audit_log) THEN
                        RAISE EXCEPTION 'AddAdminOperations cannot be rolled back: admin_audit_log has rows (append-only data would be lost)'
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                END
                $do$;
                """);

            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_orders_insert_placed ON orders;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_orders_status_transition ON orders;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_categories_guard_parent ON categories;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_admin_audit_log_no_truncate ON admin_audit_log;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_admin_audit_log_no_update_delete ON admin_audit_log;");

            migrationBuilder.DropTable(
                name: "admin_audit_log");

            migrationBuilder.DropIndex(
                name: "ix_stock_movements_occurred_at_id",
                table: "stock_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_version_positive",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_variants_version_positive",
                table: "product_variants");

            migrationBuilder.DropIndex(
                name: "ix_orders_open_created_at",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "ix_orders_open_user",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "ix_order_status_history_occurred_at_id",
                table: "order_status_history");

            migrationBuilder.DropCheckConstraint(
                name: "ck_categories_version_positive",
                table: "categories");

            migrationBuilder.DropColumn(
                name: "version",
                table: "products");

            migrationBuilder.DropColumn(
                name: "version",
                table: "product_variants");

            migrationBuilder.DropColumn(
                name: "version",
                table: "categories");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS orders_guard_insert_status();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS orders_guard_status_transition();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS categories_guard_parent();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS append_only_reject_change();");
        }
    }
}

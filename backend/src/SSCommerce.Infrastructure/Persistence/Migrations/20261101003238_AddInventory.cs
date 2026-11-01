using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SSCommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "warehouses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warehouses", x => x.id);
                    table.CheckConstraint("ck_warehouses_code_format", "code ~ '^[A-Z0-9][A-Z0-9-]{1,31}$'");
                    table.CheckConstraint("ck_warehouses_name_not_blank", "btrim(name) <> ''");
                });

            migrationBuilder.CreateTable(
                name: "inventory_balances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    on_hand_quantity = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inventory_balances", x => x.id);
                    table.CheckConstraint("ck_inventory_balances_on_hand_range", "on_hand_quantity BETWEEN 0 AND 1000000");
                    table.CheckConstraint("ck_inventory_balances_version_positive", "version >= 1");
                    table.ForeignKey(
                        name: "fk_inventory_balances_product_variant",
                        column: x => x.product_variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_inventory_balances_warehouse",
                        column: x => x.warehouse_id,
                        principalTable: "warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_movements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    quantity_delta = table.Column<int>(type: "integer", nullable: false),
                    operation_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    operation_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_fingerprint = table.Column<string>(type: "char(64)", nullable: false),
                    on_hand_after = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_movements", x => x.id);
                    table.CheckConstraint("ck_stock_movements_on_hand_after_range", "on_hand_after BETWEEN 0 AND 1000000");
                    table.CheckConstraint("ck_stock_movements_operation_key_format", "operation_key ~ '^[A-Za-z0-9_-]{16,128}$'");
                    table.CheckConstraint("ck_stock_movements_operation_type", "operation_type IN ('receiving', 'adjustment', 'correction')");
                    table.CheckConstraint("ck_stock_movements_quantity_delta_nonzero", "quantity_delta <> 0");
                    table.CheckConstraint("ck_stock_movements_quantity_delta_range", "quantity_delta BETWEEN -1000000 AND 1000000");
                    table.CheckConstraint("ck_stock_movements_reason_not_blank", "btrim(reason) <> ''");
                    table.CheckConstraint("ck_stock_movements_reference_not_blank", "reference IS NULL OR btrim(reference) <> ''");
                    table.CheckConstraint("ck_stock_movements_request_fingerprint_format", "request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_stock_movements_sku_format", "sku ~ '^[A-Z0-9]+(-[A-Z0-9]+)*$'");
                    table.ForeignKey(
                        name: "fk_stock_movements_actor_user",
                        column: x => x.actor_user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_movements_product_variant",
                        column: x => x.product_variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_movements_warehouse",
                        column: x => x.warehouse_id,
                        principalTable: "warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_balances_product_variant_id",
                table: "inventory_balances",
                column: "product_variant_id");

            migrationBuilder.CreateIndex(
                name: "ux_inventory_balances_warehouse_id_product_variant_id",
                table: "inventory_balances",
                columns: new[] { "warehouse_id", "product_variant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_actor_user_id",
                table: "stock_movements",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_product_variant_id_occurred_at",
                table: "stock_movements",
                columns: new[] { "product_variant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_warehouse_id_occurred_at",
                table: "stock_movements",
                columns: new[] { "warehouse_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ux_stock_movements_operation_key",
                table: "stock_movements",
                column: "operation_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_warehouses_code",
                table: "warehouses",
                column: "code",
                unique: true);

            // stock_movements DEĞİŞMEZ (D-047): satır düzeyinde UPDATE/DELETE ve deyim düzeyinde TRUNCATE reddedilir.
            // SQLSTATE 23001 (restrict_violation): FK RESTRICT'in 23503'ünden ve CHECK'in 23514'ünden ayırt edilir.
            // DROP TABLE trigger'ı yok sayar (migration Down için gerekli); korunan şey veri bütünlüğüdür, şema değil.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION stock_movements_reject_change() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                BEGIN
                    RAISE EXCEPTION 'stock_movements is append-only: % is not allowed', TG_OP
                        USING ERRCODE = 'restrict_violation';
                END
                $fn$;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_stock_movements_no_update_delete
                BEFORE UPDATE OR DELETE ON stock_movements
                FOR EACH ROW EXECUTE FUNCTION stock_movements_reject_change();
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_stock_movements_no_truncate
                BEFORE TRUNCATE ON stock_movements
                FOR EACH STATEMENT EXECUTE FUNCTION stock_movements_reject_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_stock_movements_no_truncate ON stock_movements;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_stock_movements_no_update_delete ON stock_movements;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS stock_movements_reject_change();");

            migrationBuilder.DropTable(
                name: "inventory_balances");

            migrationBuilder.DropTable(
                name: "stock_movements");

            migrationBuilder.DropTable(
                name: "warehouses");
        }
    }
}

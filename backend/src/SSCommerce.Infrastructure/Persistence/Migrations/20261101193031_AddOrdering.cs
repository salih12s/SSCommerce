using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SSCommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrdering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Tek izinli DROP: sipariş hareket türlerini kabul etmek için stock_movements CHECK'i yeniden kurulur (aşağıda AddCheckConstraint).
            // Satırlara dokunmaz; tablo üzerinde yalnız şema kilidi alınır, trigger'lar tetiklenmez.
            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_operation_type",
                table: "stock_movements");

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:IdentitySequenceOptions", "'100001', '1', '', '', 'False', '1'")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_fingerprint = table.Column<string>(type: "char(64)", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    delivery_label = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    delivery_recipient_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    delivery_phone = table.Column<string>(type: "char(10)", nullable: false),
                    delivery_province = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    delivery_district = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    delivery_street_address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    delivery_postal_code = table.Column<string>(type: "char(5)", nullable: true),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    item_subtotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    shipping_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    shipping_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    grand_total = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.CheckConstraint("ck_orders_currency", "currency = 'TRY'");
                    table.CheckConstraint("ck_orders_delivery_district_not_blank", "btrim(delivery_district) <> ''");
                    table.CheckConstraint("ck_orders_delivery_label_not_blank", "btrim(delivery_label) <> ''");
                    table.CheckConstraint("ck_orders_delivery_phone_format", "delivery_phone ~ '^5[0-9]{9}$'");
                    table.CheckConstraint("ck_orders_delivery_postal_code_format", "delivery_postal_code IS NULL OR delivery_postal_code ~ '^(0[1-9]|[1-7][0-9]|8[01])[0-9]{3}$'");
                    table.CheckConstraint("ck_orders_delivery_province_not_blank", "btrim(delivery_province) <> ''");
                    table.CheckConstraint("ck_orders_delivery_recipient_name_not_blank", "btrim(delivery_recipient_name) <> ''");
                    table.CheckConstraint("ck_orders_delivery_street_address_not_blank", "btrim(delivery_street_address) <> ''");
                    table.CheckConstraint("ck_orders_grand_total_null", "grand_total IS NULL");
                    table.CheckConstraint("ck_orders_idempotency_key_format", "idempotency_key ~ '^[A-Za-z0-9_-]{16,128}$'");
                    table.CheckConstraint("ck_orders_item_subtotal_non_negative", "item_subtotal >= 0");
                    table.CheckConstraint("ck_orders_request_fingerprint_format", "request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_orders_shipping_amount_null", "shipping_amount IS NULL");
                    table.CheckConstraint("ck_orders_shipping_status", "shipping_status = 'shippingNotCalculated'");
                    table.CheckConstraint("ck_orders_status", "status IN ('placed', 'processing', 'shipped', 'completed', 'cancelled')");
                    table.ForeignKey(
                        name: "fk_orders_user",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    product_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    variant_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    product_slug = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    image_url = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    image_alt = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    unit_price = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    line_subtotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_items", x => x.id);
                    table.CheckConstraint("ck_order_items_currency", "currency = 'TRY'");
                    table.CheckConstraint("ck_order_items_image_url_format", "image_url IS NULL OR image_url ~ '^/images/[a-z0-9_-]+(/[a-z0-9_-]+)*\\.[a-z0-9]{2,5}$'");
                    table.CheckConstraint("ck_order_items_line_subtotal", "line_subtotal = unit_price * quantity");
                    table.CheckConstraint("ck_order_items_product_name_not_blank", "btrim(product_name) <> ''");
                    table.CheckConstraint("ck_order_items_product_slug_format", "product_slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint("ck_order_items_quantity_range", "quantity BETWEEN 1 AND 20");
                    table.CheckConstraint("ck_order_items_sku_format", "sku ~ '^[A-Z0-9]+(-[A-Z0-9]+)*$'");
                    table.CheckConstraint("ck_order_items_unit_price_non_negative", "unit_price >= 0");
                    table.CheckConstraint("ck_order_items_variant_name_not_blank", "btrim(variant_name) <> ''");
                    table.ForeignKey(
                        name: "fk_order_items_order",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_items_product_variant",
                        column: x => x.product_variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_status_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    to_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_status_history", x => x.id);
                    table.CheckConstraint("ck_order_status_history_actor_kind", "actor_kind IN ('customer', 'staff')");
                    table.CheckConstraint("ck_order_status_history_first_row", "(from_status IS NULL) = (to_status = 'placed')");
                    table.CheckConstraint("ck_order_status_history_from_status", "from_status IS NULL OR from_status IN ('placed', 'processing', 'shipped', 'completed', 'cancelled')");
                    table.CheckConstraint("ck_order_status_history_reason_not_blank", "reason IS NULL OR btrim(reason) <> ''");
                    table.CheckConstraint("ck_order_status_history_status_changed", "from_status IS NULL OR from_status <> to_status");
                    table.CheckConstraint("ck_order_status_history_to_status", "to_status IN ('placed', 'processing', 'shipped', 'completed', 'cancelled')");
                    table.ForeignKey(
                        name: "fk_order_status_history_actor_user",
                        column: x => x.actor_user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_status_history_order",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_inventory_allocations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    placement_movement_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_inventory_allocations", x => x.id);
                    table.CheckConstraint("ck_order_inventory_allocations_quantity_positive", "quantity > 0");
                    table.ForeignKey(
                        name: "fk_order_inventory_allocations_order_item",
                        column: x => x.order_item_id,
                        principalTable: "order_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_inventory_allocations_placement_movement",
                        column: x => x.placement_movement_id,
                        principalTable: "stock_movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_inventory_allocations_product_variant",
                        column: x => x.product_variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_inventory_allocations_warehouse",
                        column: x => x.warehouse_id,
                        principalTable: "warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_operation_type",
                table: "stock_movements",
                sql: "operation_type IN ('receiving', 'adjustment', 'correction', 'order_placement', 'order_cancellation')");

            migrationBuilder.CreateIndex(
                name: "ix_order_inventory_allocations_product_variant_id",
                table: "order_inventory_allocations",
                column: "product_variant_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_inventory_allocations_warehouse_id",
                table: "order_inventory_allocations",
                column: "warehouse_id");

            migrationBuilder.CreateIndex(
                name: "ux_order_inventory_allocations_order_item_id_warehouse_id",
                table: "order_inventory_allocations",
                columns: new[] { "order_item_id", "warehouse_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_order_inventory_allocations_placement_movement_id",
                table: "order_inventory_allocations",
                column: "placement_movement_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_order_items_product_variant_id",
                table: "order_items",
                column: "product_variant_id");

            migrationBuilder.CreateIndex(
                name: "ux_order_items_order_id_product_variant_id",
                table: "order_items",
                columns: new[] { "order_id", "product_variant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_order_status_history_actor_user_id",
                table: "order_status_history",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_status_history_order_id_occurred_at_id",
                table: "order_status_history",
                columns: new[] { "order_id", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_status_created_at_id",
                table: "orders",
                columns: new[] { "status", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_orders_user_id_created_at_id",
                table: "orders",
                columns: new[] { "user_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ux_orders_order_number",
                table: "orders",
                column: "order_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_orders_user_id_idempotency_key",
                table: "orders",
                columns: new[] { "user_id", "idempotency_key" },
                unique: true);

            // Değişmezlik (D-064). SQLSTATE 23001 (restrict_violation): stock_movements trigger'ıyla aynı kod; FK RESTRICT'in 23503'ünden
            // ve CHECK'in 23514'ünden ayırt edilir. DROP TABLE trigger'ı yok sayar (Down için gerekli); korunan şey veri bütünlüğüdür, şema değil.
            // order_items, order_inventory_allocations, order_status_history yalnız eklenir: tek ortak işlev TG_TABLE_NAME ile hangisi olduğunu söyler.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION order_records_reject_change() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                BEGIN
                    RAISE EXCEPTION '% is append-only: % is not allowed', TG_TABLE_NAME, TG_OP
                        USING ERRCODE = 'restrict_violation';
                END
                $fn$;
                """);

            foreach (var table in new[] { "order_items", "order_inventory_allocations", "order_status_history" })
            {
                migrationBuilder.Sql(
                    $"""
                    CREATE TRIGGER trg_{table}_no_update_delete
                    BEFORE UPDATE OR DELETE ON {table}
                    FOR EACH ROW EXECUTE FUNCTION order_records_reject_change();
                    """);
                migrationBuilder.Sql(
                    $"""
                    CREATE TRIGGER trg_{table}_no_truncate
                    BEFORE TRUNCATE ON {table}
                    FOR EACH STATEMENT EXECUTE FUNCTION order_records_reject_change();
                    """);
            }

            // orders: silinemez, boşaltılamaz; UPDATE yalnız status ve updated_at'i değiştirebilir. Karşılaştırma satırın tamamının JSON
            // karşılığı üzerinden yapılır: ileride eklenecek her sütun (snapshot dahil) kendiliğinden korunur, listeyi güncellemek gerekmez.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION orders_restrict_change() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        IF (to_jsonb(NEW) - 'status' - 'updated_at') IS DISTINCT FROM (to_jsonb(OLD) - 'status' - 'updated_at') THEN
                            RAISE EXCEPTION 'orders is immutable except status and updated_at: UPDATE of other columns is not allowed'
                                USING ERRCODE = 'restrict_violation';
                        END IF;
                        RETURN NEW;
                    END IF;

                    RAISE EXCEPTION 'orders is immutable: % is not allowed', TG_OP
                        USING ERRCODE = 'restrict_violation';
                END
                $fn$;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_orders_restrict_update
                BEFORE UPDATE ON orders
                FOR EACH ROW EXECUTE FUNCTION orders_restrict_change();
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_orders_no_delete
                BEFORE DELETE ON orders
                FOR EACH ROW EXECUTE FUNCTION orders_restrict_change();
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_orders_no_truncate
                BEFORE TRUNCATE ON orders
                FOR EACH STATEMENT EXECUTE FUNCTION orders_restrict_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_orders_no_truncate ON orders;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_orders_no_delete ON orders;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_orders_restrict_update ON orders;");
            foreach (var table in new[] { "order_status_history", "order_inventory_allocations", "order_items" })
            {
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS trg_{table}_no_truncate ON {table};");
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS trg_{table}_no_update_delete ON {table};");
            }

            migrationBuilder.DropTable(
                name: "order_inventory_allocations");

            migrationBuilder.DropTable(
                name: "order_status_history");

            migrationBuilder.DropTable(
                name: "order_items");

            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS orders_restrict_change();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS order_records_reject_change();");

            // Sipariş hareketi (order_placement/order_cancellation) kalmışsa eski, dar CHECK eklenemez ve Down tümüyle geri alınır
            // (migration tek transaction'dır): stok denetim kaydı ve siparişler silinmez. Bilinçli: veri varken geri dönüş yoktur.
            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_operation_type",
                table: "stock_movements");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_operation_type",
                table: "stock_movements",
                sql: "operation_type IN ('receiving', 'adjustment', 'correction')");
        }
    }
}

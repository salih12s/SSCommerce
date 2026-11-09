using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SSCommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCartAndAddresses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "carts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_carts", x => x.id);
                    table.ForeignKey(
                        name: "fk_carts_user",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "customer_addresses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    recipient_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    phone = table.Column<string>(type: "char(10)", nullable: false),
                    province = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    district = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    street_address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    postal_code = table.Column<string>(type: "char(5)", nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_addresses", x => x.id);
                    table.CheckConstraint("ck_customer_addresses_district_not_blank", "btrim(district) <> ''");
                    table.CheckConstraint("ck_customer_addresses_label_not_blank", "btrim(label) <> ''");
                    table.CheckConstraint("ck_customer_addresses_phone_format", "phone ~ '^5[0-9]{9}$'");
                    table.CheckConstraint("ck_customer_addresses_postal_code_format", "postal_code IS NULL OR postal_code ~ '^(0[1-9]|[1-7][0-9]|8[01])[0-9]{3}$'");
                    table.CheckConstraint("ck_customer_addresses_province_not_blank", "btrim(province) <> ''");
                    table.CheckConstraint("ck_customer_addresses_recipient_name_not_blank", "btrim(recipient_name) <> ''");
                    table.CheckConstraint("ck_customer_addresses_street_address_not_blank", "btrim(street_address) <> ''");
                    table.ForeignKey(
                        name: "fk_customer_addresses_user",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cart_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cart_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price_at_add = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cart_items", x => x.id);
                    table.CheckConstraint("ck_cart_items_quantity_range", "quantity BETWEEN 1 AND 20");
                    table.CheckConstraint("ck_cart_items_unit_price_at_add_non_negative", "unit_price_at_add >= 0");
                    table.ForeignKey(
                        name: "fk_cart_items_cart",
                        column: x => x.cart_id,
                        principalTable: "carts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_cart_items_product_variant",
                        column: x => x.product_variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cart_items_product_variant_id",
                table: "cart_items",
                column: "product_variant_id");

            migrationBuilder.CreateIndex(
                name: "ux_cart_items_cart_id_product_variant_id",
                table: "cart_items",
                columns: new[] { "cart_id", "product_variant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_carts_user_id",
                table: "carts",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customer_addresses_user_id",
                table: "customer_addresses",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_customer_addresses_user_id_default",
                table: "customer_addresses",
                column: "user_id",
                unique: true,
                filter: "is_default");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cart_items");

            migrationBuilder.DropTable(
                name: "customer_addresses");

            migrationBuilder.DropTable(
                name: "carts");
        }
    }
}

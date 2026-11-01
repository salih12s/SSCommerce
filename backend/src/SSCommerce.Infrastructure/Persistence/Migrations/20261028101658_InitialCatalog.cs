using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SSCommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                    table.CheckConstraint("ck_categories_name_not_blank", "btrim(name) <> ''");
                    table.CheckConstraint("ck_categories_parent_not_self", "parent_id IS NULL OR parent_id <> id");
                    table.CheckConstraint("ck_categories_slug_format", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.ForeignKey(
                        name: "fk_categories_parent",
                        column: x => x.parent_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    slug = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    brand = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    specifications = table.Column<string>(type: "jsonb", nullable: true),
                    is_demo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.CheckConstraint("ck_products_brand_not_blank", "brand IS NULL OR btrim(brand) <> ''");
                    table.CheckConstraint("ck_products_description_length", "description IS NULL OR char_length(description) <= 4000");
                    table.CheckConstraint("ck_products_name_not_blank", "btrim(name) <> ''");
                    table.CheckConstraint("ck_products_slug_format", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint("ck_products_specifications_object", "specifications IS NULL OR (jsonb_typeof(specifications) = 'object' AND pg_column_size(specifications) <= 16384)");
                    table.ForeignKey(
                        name: "fk_products_category",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    alt_text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    credit_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_images", x => x.id);
                    table.CheckConstraint("ck_product_images_alt_text_not_blank", "btrim(alt_text) <> ''");
                    table.CheckConstraint("ck_product_images_credit_key_format", "credit_key IS NULL OR credit_key ~ '^/images/[a-z0-9_-]+(/[a-z0-9_-]+)*\\.[a-z0-9]{2,5}$'");
                    table.CheckConstraint("ck_product_images_sort_order_non_negative", "sort_order >= 0");
                    table.CheckConstraint("ck_product_images_storage_key_format", "storage_key ~ '^/images/[a-z0-9_-]+(/[a-z0-9_-]+)*\\.[a-z0-9]{2,5}$'");
                    table.ForeignKey(
                        name: "fk_product_images_product",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_variants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    display_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    options = table.Column<string>(type: "jsonb", nullable: false),
                    option_signature = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_variants", x => x.id);
                    table.CheckConstraint("ck_product_variants_currency_try", "currency = 'TRY'");
                    table.CheckConstraint("ck_product_variants_display_name_not_blank", "btrim(display_name) <> ''");
                    table.CheckConstraint("ck_product_variants_option_signature_not_blank", "btrim(option_signature) <> ''");
                    table.CheckConstraint("ck_product_variants_options_object", "jsonb_typeof(options) = 'object' AND pg_column_size(options) <= 2048");
                    table.CheckConstraint("ck_product_variants_price_non_negative", "price >= 0");
                    table.CheckConstraint("ck_product_variants_sku_format", "sku ~ '^[A-Z0-9]+(-[A-Z0-9]+)*$' AND char_length(sku) BETWEEN 3 AND 40");
                    table.ForeignKey(
                        name: "fk_product_variants_product",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_categories_parent_id",
                table: "categories",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ux_categories_slug",
                table: "categories",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_images_product_id_sort_order",
                table: "product_images",
                columns: new[] { "product_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ux_product_images_product_id_primary",
                table: "product_images",
                column: "product_id",
                unique: true,
                filter: "is_primary");

            migrationBuilder.CreateIndex(
                name: "ux_product_variants_product_id_option_signature",
                table: "product_variants",
                columns: new[] { "product_id", "option_signature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_product_variants_sku",
                table: "product_variants",
                column: "sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_products_category_id_is_active",
                table: "products",
                columns: new[] { "category_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ux_products_slug",
                table: "products",
                column: "slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_images");

            migrationBuilder.DropTable(
                name: "product_variants");

            migrationBuilder.DropTable(
                name: "products");

            migrationBuilder.DropTable(
                name: "categories");
        }
    }
}

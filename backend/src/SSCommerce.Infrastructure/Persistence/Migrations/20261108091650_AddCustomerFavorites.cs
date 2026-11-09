using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SSCommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerFavorites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_favorites",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_favorites", x => new { x.user_id, x.product_id });
                    table.ForeignKey(
                        name: "fk_customer_favorites_product",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_favorites_user",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customer_favorites_product_id",
                table: "customer_favorites",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_customer_favorites_user_id_created_at",
                table: "customer_favorites",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_favorites");
        }
    }
}

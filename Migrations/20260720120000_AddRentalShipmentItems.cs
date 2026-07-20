using AuditIt.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditIt.Api.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260720120000_AddRentalShipmentItems")]
    public partial class AddRentalShipmentItems : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RentalShipmentItems",
                columns: table => new
                {
                    RentalShipmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    RentalItemId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentalShipmentItems", x => new { x.RentalShipmentId, x.RentalItemId });
                    table.ForeignKey(
                        name: "FK_RentalShipmentItems_RentalItems_RentalItemId",
                        column: x => x.RentalItemId,
                        principalTable: "RentalItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RentalShipmentItems_RentalShipments_RentalShipmentId",
                        column: x => x.RentalShipmentId,
                        principalTable: "RentalShipments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RentalShipmentItems_RentalItemId",
                table: "RentalShipmentItems",
                column: "RentalItemId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RentalShipmentItems");
        }
    }
}

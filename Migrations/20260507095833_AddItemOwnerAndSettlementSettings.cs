using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditIt.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddItemOwnerAndSettlementSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SettlementNotifiedAt",
                table: "Rentals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettlementNotifiedStatus",
                table: "Rentals",
                type: "TEXT",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "Items",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SettlementSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TechnicianPercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    CreatorPercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    ItemOwnerPercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettlementSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Items_OwnerUserId",
                table: "Items",
                column: "OwnerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Items_Users_OwnerUserId",
                table: "Items",
                column: "OwnerUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Items_Users_OwnerUserId",
                table: "Items");

            migrationBuilder.DropTable(
                name: "SettlementSettings");

            migrationBuilder.DropIndex(
                name: "IX_Items_OwnerUserId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "SettlementNotifiedAt",
                table: "Rentals");

            migrationBuilder.DropColumn(
                name: "SettlementNotifiedStatus",
                table: "Rentals");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Items");
        }
    }
}

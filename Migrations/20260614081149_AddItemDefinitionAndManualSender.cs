using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditIt.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddItemDefinitionAndManualSender : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RentalItems_Items_ItemId",
                table: "RentalItems");

            migrationBuilder.AddColumn<string>(
                name: "SenderName",
                table: "Rentals",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ItemId",
                table: "RentalItems",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<int>(
                name: "ItemDefinitionId",
                table: "RentalItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RentalItems_ItemDefinitionId",
                table: "RentalItems",
                column: "ItemDefinitionId");

            migrationBuilder.AddForeignKey(
                name: "FK_RentalItems_ItemDefinitions_ItemDefinitionId",
                table: "RentalItems",
                column: "ItemDefinitionId",
                principalTable: "ItemDefinitions",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_RentalItems_Items_ItemId",
                table: "RentalItems",
                column: "ItemId",
                principalTable: "Items",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RentalItems_ItemDefinitions_ItemDefinitionId",
                table: "RentalItems");

            migrationBuilder.DropForeignKey(
                name: "FK_RentalItems_Items_ItemId",
                table: "RentalItems");

            migrationBuilder.DropIndex(
                name: "IX_RentalItems_ItemDefinitionId",
                table: "RentalItems");

            migrationBuilder.DropColumn(
                name: "SenderName",
                table: "Rentals");

            migrationBuilder.DropColumn(
                name: "ItemDefinitionId",
                table: "RentalItems");

            migrationBuilder.AlterColumn<Guid>(
                name: "ItemId",
                table: "RentalItems",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_RentalItems_Items_ItemId",
                table: "RentalItems",
                column: "ItemId",
                principalTable: "Items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

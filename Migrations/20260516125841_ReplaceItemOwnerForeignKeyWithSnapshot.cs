using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditIt.Api.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceItemOwnerForeignKeyWithSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Items_Users_OwnerUserId",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_Items_OwnerUserId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Items");

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserNamesSnapshot",
                table: "Items",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerUserNamesSnapshot",
                table: "Items");

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "Items",
                type: "TEXT",
                nullable: true);

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
    }
}

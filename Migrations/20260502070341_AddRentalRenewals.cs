using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditIt.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRentalRenewals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RenewalSequence",
                table: "Rentals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RenewedFromRentalId",
                table: "Rentals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RenewedFromRentalNumber",
                table: "Rentals",
                type: "TEXT",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RenewedToRentalId",
                table: "Rentals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RenewedToRentalNumber",
                table: "Rentals",
                type: "TEXT",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RenewalSequence",
                table: "Rentals");

            migrationBuilder.DropColumn(
                name: "RenewedFromRentalId",
                table: "Rentals");

            migrationBuilder.DropColumn(
                name: "RenewedFromRentalNumber",
                table: "Rentals");

            migrationBuilder.DropColumn(
                name: "RenewedToRentalId",
                table: "Rentals");

            migrationBuilder.DropColumn(
                name: "RenewedToRentalNumber",
                table: "Rentals");
        }
    }
}

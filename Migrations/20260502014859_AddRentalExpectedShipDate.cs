using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditIt.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRentalExpectedShipDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpectedShipDate",
                table: "Rentals",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.Sql("""
                UPDATE "Rentals"
                SET "ExpectedShipDate" = datetime("StartDate", '-1 day')
                WHERE "ExpectedShipDate" = '0001-01-01 00:00:00'
                   OR "ExpectedShipDate" = '0001-01-01T00:00:00.0000000'
                   OR "ExpectedShipDate" IS NULL
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpectedShipDate",
                table: "Rentals");
        }
    }
}

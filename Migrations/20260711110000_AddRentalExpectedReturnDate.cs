using System;
using AuditIt.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditIt.Api.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260711110000_AddRentalExpectedReturnDate")]
    public partial class AddRentalExpectedReturnDate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpectedReturnDate",
                table: "Rentals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql(@"
                UPDATE ""Rentals""
                SET ""ExpectedReturnDate"" = date(""ExpectedEndDate"", '+2 days')
                WHERE ""Status"" <> 5
                  AND ""RenewedToRentalId"" IS NULL;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpectedReturnDate",
                table: "Rentals");
        }
    }
}

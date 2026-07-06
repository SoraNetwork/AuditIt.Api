using System;
using AuditIt.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditIt.Api.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260706090000_AddRentalRenewalIntent")]
    public partial class AddRentalRenewalIntent : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasRenewalIntent",
                table: "Rentals",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "RenewalIntentEndDate",
                table: "Rentals",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasRenewalIntent",
                table: "Rentals");

            migrationBuilder.DropColumn(
                name: "RenewalIntentEndDate",
                table: "Rentals");
        }
    }
}

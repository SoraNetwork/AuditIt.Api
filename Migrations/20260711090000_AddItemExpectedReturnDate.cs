using System;
using AuditIt.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditIt.Api.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260711090000_AddItemExpectedReturnDate")]
    public partial class AddItemExpectedReturnDate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpectedReturnDate",
                table: "Items",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpectedReturnDate",
                table: "Items");
        }
    }
}

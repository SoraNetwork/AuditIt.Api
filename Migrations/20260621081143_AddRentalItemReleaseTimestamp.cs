using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditIt.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRentalItemReleaseTimestamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReleasedFromRentalAt",
                table: "RentalItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql(@"
                UPDATE ""RentalItems""
                SET ""ReleasedFromRentalAt"" = ""ReturnedAt""
                WHERE ""ReleasedFromRentalAt"" IS NULL
                  AND ""ReturnedAt"" IS NOT NULL
                  AND ""ReturnNotes"" = 'Removed from rental item list.';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReleasedFromRentalAt",
                table: "RentalItems");
        }
    }
}

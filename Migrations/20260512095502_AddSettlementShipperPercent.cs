using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditIt.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSettlementShipperPercent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ShipperPercent",
                table: "SettlementSettings",
                type: "decimal(5,2)",
                nullable: false,
                defaultValue: 10m);

            migrationBuilder.Sql("""
                UPDATE SettlementSettings
                SET ItemOwnerPercent = CASE
                    WHEN ItemOwnerPercent >= 10 THEN ItemOwnerPercent - 10
                    ELSE 0
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE SettlementSettings
                SET ItemOwnerPercent = CASE
                    WHEN ItemOwnerPercent <= 90 THEN ItemOwnerPercent + 10
                    ELSE 100
                END
                """);

            migrationBuilder.DropColumn(
                name: "ShipperPercent",
                table: "SettlementSettings");
        }
    }
}

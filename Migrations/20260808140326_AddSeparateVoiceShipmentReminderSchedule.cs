using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditIt.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSeparateVoiceShipmentReminderSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VoiceSendHour",
                table: "ShipmentReminderSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 12);

            migrationBuilder.AddColumn<int>(
                name: "VoiceSendMinute",
                table: "ShipmentReminderSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VoiceSendHour",
                table: "ShipmentReminderSettings");

            migrationBuilder.DropColumn(
                name: "VoiceSendMinute",
                table: "ShipmentReminderSettings");
        }
    }
}

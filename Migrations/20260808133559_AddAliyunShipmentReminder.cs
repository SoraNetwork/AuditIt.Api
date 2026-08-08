using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuditIt.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAliyunShipmentReminder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShipmentReminderDispatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RentalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipientMobile = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    RecipientName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Channel = table.Column<int>(type: "INTEGER", nullable: false),
                    BusinessDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProviderRequestId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    AttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShipmentReminderDispatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShipmentReminderSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SmsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    VoiceEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SendHour = table.Column<int>(type: "INTEGER", nullable: false),
                    SendMinute = table.Column<int>(type: "INTEGER", nullable: false),
                    TemplateVariablesJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    SmsSignName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SmsTemplateCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    VoiceTtsCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    VoiceCalledShowNumber = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    AdministratorUserIds = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShipmentReminderSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentReminderDispatches_BusinessDate_Status",
                table: "ShipmentReminderDispatches",
                columns: new[] { "BusinessDate", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentReminderDispatches_RentalId_RecipientMobile_Channel_BusinessDate",
                table: "ShipmentReminderDispatches",
                columns: new[] { "RentalId", "RecipientMobile", "Channel", "BusinessDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShipmentReminderDispatches");

            migrationBuilder.DropTable(
                name: "ShipmentReminderSettings");
        }
    }
}

using System.Text.Json;
using AuditIt.Api.Data;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AuditIt.Api.Tests;

public class ShipmentReminderServiceTests
{
    [Fact]
    public async Task DispatchScheduledAsync_mapsConfiguredVariables_and_deduplicatesRecipientsAndSweeps()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var creator = new User { Id = Guid.NewGuid(), Name = "Creator", Mobile = "13800138000", Status = UserStatus.Active };
        var administrator = new User { Id = Guid.NewGuid(), Name = "Administrator", Mobile = "13800138000", Status = UserStatus.Active };
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20260808-0001",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "张三", Phone = "13900139000" },
            Status = RentalStatus.Pending,
            ExpectedShipDate = new DateTime(2026, 8, 8),
            StartDate = new DateTime(2026, 8, 8),
            ExpectedEndDate = new DateTime(2026, 8, 10),
            CreatedBy = creator.Name,
            AssignedTo = creator.Name,
            TotalPrice = 100m
        };
        db.Users.AddRange(creator, administrator);
        db.Rentals.Add(rental);
        db.ShipmentReminderSettings.Add(new ShipmentReminderSettings
        {
            Enabled = true,
            SmsEnabled = true,
            SendHour = 12,
            SmsSignName = "AuditIt",
            SmsTemplateCode = "SMS_123",
            AdministratorUserIds = JsonSerializer.Serialize(new[] { administrator.Id }),
            TemplateVariablesJson = JsonSerializer.Serialize(new[]
            {
                new ShipmentReminderTemplateVariableDto { Name = "rental", Source = ShipmentReminderVariableSource.RentalNumber },
                new ShipmentReminderTemplateVariableDto { Name = "when", Source = ShipmentReminderVariableSource.RelativeExpectedShipDate },
                new ShipmentReminderTemplateVariableDto { Name = "note", Source = ShipmentReminderVariableSource.StaticText, StaticValue = "请加急" }
            })
        });
        await db.SaveChangesAsync();

        var sender = new RecordingSender();
        var service = new ShipmentReminderService(
            db,
            sender,
            Options.Create(new AliyunNotificationOptions { AccessKeyId = "key", AccessKeySecret = "secret" }),
            NullLogger<ShipmentReminderService>.Instance);
        var now = new DateTime(2026, 8, 8, 4, 30, 0, DateTimeKind.Utc); // 12:30 China time

        await service.DispatchScheduledAsync(now);
        await service.DispatchScheduledAsync(now);

        var send = Assert.Single(sender.SmsSends);
        Assert.Equal("13800138000", send.Mobile);
        Assert.Equal("R20260808-0001", send.Parameters["rental"]);
        Assert.Equal("今天", send.Parameters["when"]);
        Assert.Equal("请加急", send.Parameters["note"]);
        Assert.Equal(1, await db.ShipmentReminderDispatches.CountAsync());
        Assert.Equal(ShipmentReminderDispatchStatus.Succeeded, (await db.ShipmentReminderDispatches.SingleAsync()).Status);
    }

    private sealed class RecordingSender : IAliyunShipmentReminderSender
    {
        public List<(string Mobile, IReadOnlyDictionary<string, string> Parameters)> SmsSends { get; } = new();

        public Task<string> SendSmsAsync(string mobile, string signName, string templateCode, IReadOnlyDictionary<string, string> templateParameters, CancellationToken ct)
        {
            SmsSends.Add((mobile, new Dictionary<string, string>(templateParameters)));
            return Task.FromResult("sms-request-id");
        }

        public Task<string> SendVoiceAsync(string mobile, string? calledShowNumber, string ttsCode, IReadOnlyDictionary<string, string> templateParameters, CancellationToken ct) =>
            Task.FromResult("voice-request-id");

        public Task<IReadOnlyList<AliyunSmsTemplateDto>> ListSmsTemplatesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AliyunSmsTemplateDto>>(Array.Empty<AliyunSmsTemplateDto>());
    }
}


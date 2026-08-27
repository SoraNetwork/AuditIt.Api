using AuditIt.Api.Data;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AuditIt.Api.Tests;

public class ReminderSweeperTests
{
    [Fact]
    public async Task SweepOnceAsync_doesNotRecreateReturnedOrDismissedDeliveryReminders_andIsIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection));
        services.AddOptions<ReminderOptions>().Configure(options =>
        {
            options.SweepIntervalMinutes = 10;
            options.DueSoonLeadHours = 48;
        });
        services.AddScoped<IIdentityService, EmptyIdentityService>();
        services.AddScoped<IShipmentReminderService, NoopShipmentReminderService>();
        var channel = new CapturingNotificationChannel();
        services.AddSingleton<INotificationChannel>(channel);
        await using var provider = services.BuildServiceProvider();

        var today = RentalDateRules.Today(DateTime.UtcNow);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();
            var renter = new Renter { Id = Guid.NewGuid(), Name = "测试租客", Phone = "13800138000" };
            var warehouse = new Warehouse { Name = "主仓", Location = "A1", Description = "测试仓库" };

            var returned = BuildRental("R-RETURNED", RentalStatus.Returned, renter, warehouse, today, "操作员甲");
            var dismissed = BuildRental("R-DISMISSED", RentalStatus.Active, renter, warehouse, today, "操作员甲");
            var fresh = BuildRental("R-FRESH", RentalStatus.Active, renter, warehouse, today, "操作员甲");
            db.Rentals.AddRange(returned, dismissed, fresh);
            db.Reminders.AddRange(
                BuildDeliveryReminder(returned, today, dismissedAt: null),
                BuildDeliveryReminder(dismissed, today, dismissedAt: DateTime.UtcNow.AddMinutes(-5)));
            await db.SaveChangesAsync();
        }

        var sweeper = new ReminderSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptionsMonitor<ReminderOptions>>(),
            NullLogger<ReminderSweeper>.Instance);

        await sweeper.SweepOnceAsync(default);
        await sweeper.SweepOnceAsync(default);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var reminders = await db.Reminders.OrderBy(reminder => reminder.Title).ToListAsync();
            Assert.Equal(3, reminders.Count);
            Assert.Single(reminders, reminder => reminder.Title.Contains("R-RETURNED"));
            Assert.NotNull(reminders.Single(reminder => reminder.Title.Contains("R-RETURNED")).DismissedAt);
            Assert.Single(reminders, reminder => reminder.Title.Contains("R-DISMISSED"));
            Assert.Single(reminders, reminder => reminder.Title.Contains("R-FRESH"));
        }

        var delivered = Assert.Single(channel.Delivered);
        Assert.Contains("R-FRESH", delivered.Title);
    }

    private static Rental BuildRental(
        string rentalNumber,
        RentalStatus status,
        Renter renter,
        Warehouse warehouse,
        DateTime today,
        string target) => new()
    {
        Id = Guid.NewGuid(),
        RentalNumber = rentalNumber,
        Renter = renter,
        Status = status,
        StartDate = today.AddDays(-3),
        ExpectedShipDate = today.AddDays(-4),
        ExpectedEndDate = today.AddDays(5),
        CreatedBy = target,
        TotalPrice = 100m,
        Shipments = new List<RentalShipment>
        {
            new()
            {
                Direction = ShipmentDirection.Outbound,
                OriginWarehouse = warehouse,
                Carrier = "测试物流",
                TrackingNumber = Guid.NewGuid().ToString("N"),
                ShippedAt = today.AddDays(-3),
                CreatedBy = target
            }
        }
    };

    private static Reminder BuildDeliveryReminder(Rental rental, DateTime today, DateTime? dismissedAt) => new()
    {
        Type = ReminderType.RentalDeliveryUnsigned,
        Level = ReminderLevel.Warning,
        RelatedEntityType = "Rental",
        RelatedEntityId = rental.Id.ToString(),
        TargetUser = rental.CreatedBy,
        Title = $"租赁 {rental.RentalNumber} 发货物流未签收",
        DueAt = today.AddDays(-2),
        CreatedAt = DateTime.UtcNow.AddHours(-1),
        DismissedAt = dismissedAt,
        DismissedBy = dismissedAt.HasValue ? rental.CreatedBy : null
    };

    private sealed class CapturingNotificationChannel : INotificationChannel
    {
        public string Name => "capturing";
        public List<Reminder> Delivered { get; } = new();

        public Task DeliverAsync(Reminder reminder, CancellationToken ct)
        {
            Delivered.Add(reminder);
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyIdentityService : IIdentityService
    {
        public Task<(User user, IReadOnlyList<string> permissions)?> LoginUpsertAsync(string name, string? dingTalkId) =>
            Task.FromResult<(User user, IReadOnlyList<string> permissions)?>(null);
        public Task<IReadOnlyList<string>> GetPermissionsForUserAsync(Guid userId) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<IReadOnlyList<string>> GetUsersWithPermissionAsync(string permissionCode) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<IReadOnlyList<string>> GetUsersInRoleAsync(string roleName) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class NoopShipmentReminderService : IShipmentReminderService
    {
        public Task DispatchScheduledAsync(DateTime utcNow, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ShipmentReminderSettingsDto> GetSettingsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ShipmentReminderRecipientDto>> ListActiveRecipientsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ShipmentReminderSettingsDto> UpdateSettingsAsync(UpdateShipmentReminderSettingsDto dto, string? currentUser, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AliyunSmsTemplateDto>> ListSmsTemplatesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ShipmentReminderTestResultDto>> SendTestAsync(TestShipmentReminderDto dto, CancellationToken ct = default) => throw new NotImplementedException();
    }
}

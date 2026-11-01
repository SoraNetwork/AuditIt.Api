using AuditIt.Api.Data;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AuditIt.Api.Tests;

public partial class RentalOccupancyAndValueTests
{
    [Theory]
    [InlineData("active", null, "Creator A", "Global Shipper")]
    [InlineData("active", null, "Creator B", "Global Shipper")]
    [InlineData("active", "  Other Shipper  ", "Creator A", "Other Shipper")]
    [InlineData("active", "  ", "Creator A", "Global Shipper")]
    [InlineData("left", null, "Creator A", "Creator A")]
    [InlineData("missing", null, "Creator A", "Creator A")]
    [InlineData("unset", null, "Creator A", "Creator A")]
    public async Task CreateAsync_DefaultShipperUsesGlobalSettingAndAllowsOverride(
        string configuration, string? explicitShipper, string creator, string expected)
    {
        await using var f = await OccupancyFixture.Create(1);
        var shipper = new User { Name = "Global Shipper", Status = configuration == "left" ? UserStatus.Left : UserStatus.Active };
        if (configuration is "active" or "left") f.Db.Users.Add(shipper);
        if (configuration != "unset")
            f.Db.ShipmentReminderSettings.Add(new ShipmentReminderSettings { DefaultShipperUserId = shipper.Id });
        await f.Db.SaveChangesAsync();

        var service = CreateRentalService(f.Db);
        Assert.Equal(configuration == "active" ? shipper.Name : null, await service.GetDefaultShipperNameAsync());
        var request = f.Request(f.Today.AddDays(1), f.Today.AddDays(3));
        request.ExpectedShipperName = explicitShipper;
        var result = await service.CreateAsync(request, creator);

        Assert.Null(result.Error);
        Assert.NotNull(result.Rental);
        Assert.Equal(expected, result.Rental!.ExpectedShipperName);
        Assert.Equal(creator, result.Rental.CreatedBy);
        Assert.Equal(expected, (await f.Db.Rentals.SingleAsync()).ExpectedShipperName);
    }

    [Fact]
    public async Task DefaultShipperCanBeSavedAndClearedWithoutNotificationCredentials_AndOnlyAffectsNewRentals()
    {
        await using var f = await OccupancyFixture.Create(1);
        var shipper = new User { Name = "Global Shipper" };
        f.Db.Users.Add(shipper);
        await f.Db.SaveChangesAsync();
        var settingsService = new ShipmentReminderService(f.Db, null!,
            Options.Create(new AliyunNotificationOptions()), NullLogger<ShipmentReminderService>.Instance);
        var settings = await settingsService.UpdateDefaultShipperAsync(
            new UpdateDefaultShipperDto { DefaultShipperUserId = shipper.Id }, "Admin");
        Assert.Equal(shipper.Id, settings.DefaultShipperUserId);
        Assert.False(settings.Enabled);
        Assert.False(settings.AliyunCredentialsConfigured);
        Assert.Equal("Admin", settings.UpdatedBy);

        settings = await settingsService.UpdateSettingsAsync(new UpdateShipmentReminderSettingsDto
        {
            SmsEnabled = false,
            TemplateVariables = new()
            {
                new() { Name = "order_id", Source = ShipmentReminderVariableSource.RentalNumber },
                new() { Name = "time", Source = ShipmentReminderVariableSource.RelativeExpectedShipDate }
            }
        }, "Admin");
        Assert.Equal(shipper.Id, settings.DefaultShipperUserId);

        var rentals = CreateRentalService(f.Db);
        var result = await rentals.CreateAsync(f.Request(f.Today.AddDays(1), f.Today.AddDays(2)), "Creator");
        Assert.Null(result.Error);
        Assert.Equal(shipper.Name, result.Rental!.ExpectedShipperName);

        shipper.Name = "Renamed Shipper";
        await f.Db.SaveChangesAsync();
        Assert.Equal(shipper.Name, await rentals.GetDefaultShipperNameAsync());

        settings = await settingsService.UpdateDefaultShipperAsync(new UpdateDefaultShipperDto(), "Admin");
        Assert.Null(settings.DefaultShipperUserId);
        Assert.Null((await settingsService.GetSettingsAsync()).DefaultShipperUserId);
        Assert.Null(await rentals.GetDefaultShipperNameAsync());
        Assert.Equal("Global Shipper", (await f.Db.Rentals.SingleAsync()).ExpectedShipperName);

        var next = await rentals.CreateAsync(f.Request(f.Today.AddDays(4), f.Today.AddDays(5)), "Other Creator");
        Assert.Null(next.Error);
        Assert.Equal("Other Creator", next.Rental!.ExpectedShipperName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DefaultShipperRejectsDepartedOrMissingEmployees(bool exists)
    {
        await using var f = await OccupancyFixture.Create(1);
        var original = new User { Name = "Current Shipper" };
        var invalid = new User { Name = "Departed Shipper", Status = UserStatus.Left };
        f.Db.Users.Add(original);
        if (exists) f.Db.Users.Add(invalid);
        f.Db.ShipmentReminderSettings.Add(new ShipmentReminderSettings { DefaultShipperUserId = original.Id });
        await f.Db.SaveChangesAsync();
        var service = new ShipmentReminderService(f.Db, null!,
            Options.Create(new AliyunNotificationOptions()), NullLogger<ShipmentReminderService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateDefaultShipperAsync(
            new UpdateDefaultShipperDto { DefaultShipperUserId = invalid.Id }, "Admin"));
        Assert.Equal(original.Id, (await service.GetSettingsAsync()).DefaultShipperUserId);
    }

    [Fact]
    public async Task DefaultShipperMigrationPreservesExistingNotificationSettings()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261003121149_AddExpectedShipperName");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO ShipmentReminderSettings
                (Id, Enabled, SmsEnabled, VoiceEnabled, SendHour, SendMinute, VoiceSendHour, VoiceSendMinute,
                 TemplateVariablesJson, AdministratorUserIds, VoiceTtsCode, UpdatedAt)
            VALUES (1, 1, 0, 1, 12, 0, 14, 30, '[]', '[]', 'TTS_existing', '2026-10-01 00:00:00')
            """);

        await migrator.MigrateAsync();
        var settings = await db.ShipmentReminderSettings.SingleAsync();
        Assert.Null(settings.DefaultShipperUserId);
        Assert.True(settings.Enabled);
        Assert.True(settings.VoiceEnabled);
        Assert.Equal(14, settings.VoiceSendHour);
        Assert.Equal("TTS_existing", settings.VoiceTtsCode);
    }
}

using AuditIt.Api.Data;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AuditIt.Api.Tests;

public class SettlementServiceTests
{
    [Fact]
    public async Task GetPreviewAsync_keepsUnassignedShipperShareWhenNoOutboundShipmentExists()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var rentalId = Guid.NewGuid();
        context.SettlementSettings.Add(new SettlementSetting
        {
            Id = 1,
            TechnicianPercent = 0m,
            CreatorPercent = 0m,
            ShipperPercent = 10m,
            ItemOwnerPercent = 0m
        });
        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = "R20260501-0002",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" },
            Status = RentalStatus.Returned,
            StartDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            ExpectedShipDate = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
            ExpectedEndDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
            TotalPrice = 800m
        });
        await context.SaveChangesAsync();

        var service = new SettlementService(context, new StubDingTalkService(), NullLogger<SettlementService>.Instance);

        var preview = await service.GetPreviewAsync(rentalId);

        Assert.NotNull(preview);
        Assert.Equal(80m, preview!.ShipperAmount);
        var share = Assert.Single(preview.ShipperShares);
        Assert.Null(share.ShipperName);
        Assert.Equal(80m, share.Amount);
    }

    [Fact]
    public async Task GetPreviewAsync_usesSourceOutboundShippersForRenewalRental()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var sourceRentalId = Guid.NewGuid();
        var renewalRentalId = Guid.NewGuid();
        var startDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var category = new Category { Name = "Camera", Description = "Camera category" };
        var definition = new ItemDefinition { Name = "Camera Body", Category = category, Unit = "pcs", Description = "Body" };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            OwnerUserNamesSnapshot = "Owner"
        };

        var sourceRental = new Rental
        {
            Id = sourceRentalId,
            RentalNumber = "R20260501-0001",
            Renter = renter,
            Status = RentalStatus.Renewed,
            StartDate = startDate,
            ExpectedShipDate = startDate.AddDays(-1),
            ExpectedEndDate = startDate.AddDays(10),
            TotalPrice = 500m,
            RenewedToRentalId = renewalRentalId,
            RenewedToRentalNumber = "R20260501-0001-01"
        };
        sourceRental.Shipments.Add(new RentalShipment
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            TrackingNumber = "SF1",
            ShippedAt = startDate,
            CreatedBy = "Alice"
        });
        sourceRental.Shipments.Add(new RentalShipment
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            TrackingNumber = "SF2",
            ShippedAt = startDate,
            CreatedBy = "Bob"
        });

        var renewalRental = new Rental
        {
            Id = renewalRentalId,
            RentalNumber = "R20260501-0001-01",
            Renter = renter,
            Status = RentalStatus.Returned,
            StartDate = startDate.AddDays(11),
            ExpectedShipDate = startDate.AddDays(11),
            ExpectedEndDate = startDate.AddDays(20),
            TotalPrice = 1000m,
            RenewedFromRentalId = sourceRentalId,
            RenewedFromRentalNumber = sourceRental.RentalNumber
        };
        renewalRental.Items.Add(new RentalItem
        {
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name,
            PerItemPrice = 1000m,
            ReturnedAt = startDate.AddDays(20)
        });

        context.SettlementSettings.Add(new SettlementSetting
        {
            Id = 1,
            TechnicianPercent = 0m,
            CreatorPercent = 0m,
            ShipperPercent = 10m,
            ItemOwnerPercent = 0m
        });
        context.Rentals.AddRange(sourceRental, renewalRental);
        await context.SaveChangesAsync();

        var service = new SettlementService(context, new StubDingTalkService(), NullLogger<SettlementService>.Instance);

        var preview = await service.GetPreviewAsync(renewalRentalId);

        Assert.NotNull(preview);
        Assert.Equal(100m, preview!.ShipperAmount);
        Assert.Collection(
            preview.ShipperShares,
            share =>
            {
                Assert.Equal("Alice", share.ShipperName);
                Assert.Equal(50m, share.Amount);
            },
            share =>
            {
                Assert.Equal("Bob", share.ShipperName);
                Assert.Equal(50m, share.Amount);
            });
        Assert.Contains("Alice", preview.MarkdownText);
        Assert.Contains("Bob", preview.MarkdownText);
    }

    [Fact]
    public async Task GetPreviewAsync_keepsOwnerSharesSeparatePerRentalItem()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var rentalId = Guid.NewGuid();
        var startDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var category = new Category { Name = "Camera", Description = "Camera category" };
        var definition = new ItemDefinition { Name = "Camera Body", Category = category, Unit = "pcs", Description = "Body" };
        var itemA = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            OwnerUserNamesSnapshot = "Owner"
        };
        var itemB = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-002",
            Warehouse = warehouse,
            ItemDefinition = definition,
            OwnerUserNamesSnapshot = "Owner"
        };

        var rental = new Rental
        {
            Id = rentalId,
            RentalNumber = "R20260501-0004",
            Renter = renter,
            Status = RentalStatus.Returned,
            StartDate = startDate,
            ExpectedShipDate = startDate.AddDays(-1),
            ExpectedEndDate = startDate.AddDays(10),
            TotalPrice = 1000m
        };
        rental.Items.Add(new RentalItem
        {
            Item = itemA,
            ItemShortIdSnapshot = itemA.ShortId,
            ItemNameSnapshot = "Camera A",
            PerItemPrice = 100m,
            ReturnedAt = startDate.AddDays(10)
        });
        rental.Items.Add(new RentalItem
        {
            Item = itemB,
            ItemShortIdSnapshot = itemB.ShortId,
            ItemNameSnapshot = "Camera B",
            PerItemPrice = 300m,
            ReturnedAt = startDate.AddDays(10)
        });

        context.SettlementSettings.Add(new SettlementSetting
        {
            Id = 1,
            TechnicianPercent = 0m,
            CreatorPercent = 0m,
            ShipperPercent = 0m,
            ItemOwnerPercent = 50m
        });
        context.Rentals.Add(rental);
        await context.SaveChangesAsync();

        var service = new SettlementService(context, new StubDingTalkService(), NullLogger<SettlementService>.Instance);

        var preview = await service.GetPreviewAsync(rentalId);

        Assert.NotNull(preview);
        Assert.Equal(500m, preview!.ItemOwnerAmount);
        Assert.Collection(
            preview.OwnerShares,
            share =>
            {
                Assert.Equal("Owner", share.OwnerName);
                Assert.Equal("CAM-001", share.ItemShortId);
                Assert.Equal("Camera A", share.ItemName);
                Assert.Equal(125m, share.Amount);
            },
            share =>
            {
                Assert.Equal("Owner", share.OwnerName);
                Assert.Equal("CAM-002", share.ItemShortId);
                Assert.Equal("Camera B", share.ItemName);
                Assert.Equal(375m, share.Amount);
            });
        Assert.Contains("CAM-001", preview.MarkdownText);
        Assert.Contains("CAM-002", preview.MarkdownText);
    }

    [Fact]
    public async Task GetPreviewAsync_walksRenewalChainUntilOutboundShippersAreFound()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var firstRentalId = Guid.NewGuid();
        var secondRentalId = Guid.NewGuid();
        var thirdRentalId = Guid.NewGuid();
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
        var startDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        var firstRental = new Rental
        {
            Id = firstRentalId,
            RentalNumber = "R20260501-0003",
            Renter = renter,
            Status = RentalStatus.Renewed,
            StartDate = startDate,
            ExpectedShipDate = startDate.AddDays(-1),
            ExpectedEndDate = startDate.AddDays(10),
            TotalPrice = 300m,
            RenewedToRentalId = secondRentalId,
            RenewedToRentalNumber = "R20260501-0003-01"
        };
        firstRental.Shipments.Add(new RentalShipment
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            TrackingNumber = "SF3",
            ShippedAt = startDate,
            CreatedBy = "Alice"
        });

        var secondRental = new Rental
        {
            Id = secondRentalId,
            RentalNumber = "R20260501-0003-01",
            Renter = renter,
            Status = RentalStatus.Renewed,
            StartDate = startDate.AddDays(11),
            ExpectedShipDate = startDate.AddDays(11),
            ExpectedEndDate = startDate.AddDays(20),
            TotalPrice = 600m,
            RenewedFromRentalId = firstRentalId,
            RenewedFromRentalNumber = firstRental.RentalNumber,
            RenewedToRentalId = thirdRentalId,
            RenewedToRentalNumber = "R20260501-0003-02"
        };

        var thirdRental = new Rental
        {
            Id = thirdRentalId,
            RentalNumber = "R20260501-0003-02",
            Renter = renter,
            Status = RentalStatus.Returned,
            StartDate = startDate.AddDays(21),
            ExpectedShipDate = startDate.AddDays(21),
            ExpectedEndDate = startDate.AddDays(30),
            TotalPrice = 1200m,
            RenewedFromRentalId = secondRentalId,
            RenewedFromRentalNumber = secondRental.RentalNumber
        };

        context.SettlementSettings.Add(new SettlementSetting
        {
            Id = 1,
            TechnicianPercent = 0m,
            CreatorPercent = 0m,
            ShipperPercent = 10m,
            ItemOwnerPercent = 0m
        });
        context.Rentals.AddRange(firstRental, secondRental, thirdRental);
        await context.SaveChangesAsync();

        var service = new SettlementService(context, new StubDingTalkService(), NullLogger<SettlementService>.Instance);

        var preview = await service.GetPreviewAsync(thirdRentalId);

        Assert.NotNull(preview);
        Assert.Equal(120m, preview!.ShipperAmount);
        var share = Assert.Single(preview.ShipperShares);
        Assert.Equal("Alice", share.ShipperName);
        Assert.Equal(120m, share.Amount);
    }

    private sealed class StubDingTalkService : IDingTalkService
    {
        public Task<string> GetAccessTokenAsync() => Task.FromResult(string.Empty);

        public Task<DingTalkUserInfo> GetLegacyUserInfoByCodeAsync(string code) => throw new NotImplementedException();

        public Task<DingTalkContactUser> GetSsoUserInfoByCodeAsync(string ssoCode) => throw new NotImplementedException();

        public Task<IReadOnlyList<DingTalkUserDetail>> GetDirectoryUsersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DingTalkUserDetail>>(Array.Empty<DingTalkUserDetail>());

        public Task SendWorkNoticeAsync(IEnumerable<string> userIds, string content, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> SendRobotMarkdownAsync(string title, string markdownText, string? msgUuid = null, CancellationToken ct = default) =>
            Task.FromResult(true);
    }
}

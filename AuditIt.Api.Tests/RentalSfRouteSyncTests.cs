using AuditIt.Api.Data;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AuditIt.Api.Tests;

public class RentalSfRouteSyncTests
{
    [Fact]
    public async Task SyncSfRoutesAsync_autoSignsInboundShipmentWithoutReturningRentalItems()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var deliveredAt = new DateTime(2026, 5, 20, 6, 30, 0, DateTimeKind.Utc);
        var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
        var category = new Category { Name = "Lens", Description = "Lens category" };
        var definition = new ItemDefinition { Name = "Prime Lens", Category = category, Unit = "件", Description = "Prime lens" };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "LENS-001",
            Status = ItemStatus.LoanedOut,
            Warehouse = warehouse,
            ItemDefinition = definition
        };
        var renter = new Renter
        {
            Id = Guid.NewGuid(),
            Name = "Tenant",
            Phone = "13800138000"
        };
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20260520-0001",
            Renter = renter,
            Status = RentalStatus.Active,
            StartDate = deliveredAt.AddDays(-7),
            ExpectedShipDate = deliveredAt.AddDays(-8),
            ExpectedEndDate = deliveredAt.AddDays(1),
            TotalPrice = 100m
        };
        rental.Items.Add(new RentalItem
        {
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name
        });
        rental.Shipments.Add(new RentalShipment
        {
            Direction = ShipmentDirection.Inbound,
            OriginWarehouse = warehouse,
            Carrier = "顺丰",
            TrackingNumber = "SF1234567890",
            ShippedAt = deliveredAt.AddHours(-4)
        });

        context.Rentals.Add(rental);
        await context.SaveChangesAsync();

        var service = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(deliveredAt),
            new StubSettlementService());

        var (result, error) = await service.SyncSfRoutesAsync(rental.Id, forceRefresh: true, "tester");

        Assert.Null(error);
        Assert.NotNull(result);
        var route = Assert.Single(result!.Shipments);
        Assert.True(route.AutoDelivered);

        var savedShipment = await context.RentalShipments.SingleAsync();
        var savedRentalItem = await context.RentalItems.Include(ri => ri.Item).SingleAsync();
        var savedRental = await context.Rentals.SingleAsync();

        Assert.Equal(deliveredAt, savedShipment.DeliveredAt);
        Assert.Null(savedRentalItem.ReturnedAt);
        Assert.Equal(ItemStatus.LoanedOut, savedRentalItem.Item!.Status);
        Assert.Equal(RentalStatus.Active, savedRental.Status);
    }

    private sealed class StubSfExpressService : ISfExpressService
    {
        private readonly DateTime _deliveredAt;

        public StubSfExpressService(DateTime deliveredAt)
        {
            _deliveredAt = deliveredAt;
        }

        public Task<IReadOnlyList<SfRouteQueryResult>> QueryRoutesAsync(
            IReadOnlyList<SfRouteQueryItem> items,
            bool forceRefresh,
            CancellationToken ct = default)
        {
            IReadOnlyList<SfRouteQueryResult> results = items.Select(item => new SfRouteQueryResult
            {
                ShipmentId = item.ShipmentId,
                TrackingNumber = item.TrackingNumber,
                CheckPhoneNo = item.CheckPhoneNo,
                Queryable = true,
                QueriedAt = _deliveredAt,
                DeliveredAt = _deliveredAt,
                Routes =
                {
                    new SfRouteNodeDto
                    {
                        AcceptTime = "2026-05-20 14:30:00",
                        FirstStatusName = "签收",
                        SecondaryStatusName = "已签收",
                        Remark = "已签收"
                    }
                }
            }).ToList();

            return Task.FromResult(results);
        }
    }

    private sealed class StubRenterService : IRenterService
    {
        public Task<IEnumerable<RenterDto>> SearchAsync(string? keyword, int limit) => Task.FromResult(Enumerable.Empty<RenterDto>());
        public Task<RenterDto?> GetByIdAsync(Guid id) => Task.FromResult<RenterDto?>(null);
        public Task<RenterDto> CreateAsync(CreateRenterDto dto, string? currentUser) => throw new NotImplementedException();
        public Task<RenterDto?> UpdateAsync(Guid id, UpdateRenterDto dto) => Task.FromResult<RenterDto?>(null);
        public Task<bool> DeleteAsync(Guid id) => Task.FromResult(false);
        public Task<Renter> ResolveOrUpsertAsync(RenterInlineDto inline, string? currentUser) => throw new NotImplementedException();
    }

    private sealed class StubIdentityService : IIdentityService
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

    private sealed class StubSettlementService : ISettlementService
    {
        public Task<SettlementSettingDto> GetSettingsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(SettlementSettingDto? settings, string? error)> UpdateSettingsAsync(UpdateSettlementSettingDto dto, string? currentUser, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SettlementPreviewDto?> GetPreviewAsync(Guid rentalId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<SettlementPreviewDto>> GetPreviewsAsync(List<Rental> rentals, CancellationToken ct = default) => Task.FromResult(new List<SettlementPreviewDto>());
        public Task<(SettlementPreviewDto? preview, string? error)> SendForRentalAsync(Guid rentalId, string? currentUser, bool force = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task TrySendForRentalAsync(Guid rentalId, string? currentUser, CancellationToken ct = default) => Task.CompletedTask;
    }
}

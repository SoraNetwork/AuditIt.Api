using AuditIt.Api.Data;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AuditIt.Api.Tests;

public class RentalSfRouteSyncTests
{
    [Theory]
    [InlineData("Alice", "R20260601-0001")]
    [InlineData("001380", "R20260601-0001")]
    [InlineData("ALPHA-42", "R20260601-0001")]
    [InlineData("xy-alice", "R20260601-0001")]
    [InlineData("tb-alice", "R20260601-0001")]
    [InlineData("xhs-alice", "R20260601-0001")]
    [InlineData("Huangpu", "R20260601-0001")]
    [InlineData("photography", "R20260601-0001")]
    [InlineData("R20260601", "R20260601-0001")]
    [InlineData("Camera", "R20260601-0001")]
    public async Task ListAsync_searchesRentalNumberRenterAndItems(string search, string expectedRentalNumber)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
        var cameraRental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20260601-0001",
            Renter = new Renter
            {
                Id = Guid.NewGuid(),
                Name = "Alice Chen",
                Phone = "13800138000",
                IdCardNo = "ID-ALPHA-42",
                XianyuId = "xy-alice-store",
                TaobaoId = "tb-alice-shop",
                XiaohongshuId = "xhs-alice-red",
                DefaultAddress = "Shanghai Huangpu Road 1",
                Notes = "VIP photography client"
            },
            Status = RentalStatus.Active,
            StartDate = new DateTime(2026, 6, 1),
            ExpectedShipDate = new DateTime(2026, 5, 31),
            ExpectedEndDate = new DateTime(2026, 6, 5),
            TotalPrice = 100m
        };
        cameraRental.Items.Add(new RentalItem
        {
            ItemShortIdSnapshot = "CAM-001",
            ItemNameSnapshot = "Camera Body"
        });

        var lensRental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20260602-0002",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Bob Li", Phone = "13900139000" },
            Status = RentalStatus.Active,
            StartDate = new DateTime(2026, 6, 2),
            ExpectedShipDate = new DateTime(2026, 6, 1),
            ExpectedEndDate = new DateTime(2026, 6, 6),
            TotalPrice = 200m
        };
        lensRental.Items.Add(new RentalItem
        {
            ItemShortIdSnapshot = "LEN-001",
            ItemNameSnapshot = "Prime Lens"
        });

        context.Rentals.AddRange(cameraRental, lensRental);
        await context.SaveChangesAsync();

        var service = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(DateTime.UtcNow),
            new StubSettlementService());

        var (items, total) = await service.ListAsync(new RentalQueryParameters
        {
            Search = search,
            PageSize = 20
        });

        var item = Assert.Single(items);
        Assert.Equal(1, total);
        Assert.Equal(expectedRentalNumber, item.RentalNumber);
    }

    [Fact]
    public async Task ListAsync_appliesSortingBeforePagination()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        context.Rentals.AddRange(
            BuildRental("R20260601-0001", renter, new DateTime(2026, 6, 20), new DateTime(2026, 1, 1)),
            BuildRental("R20260601-0002", renter, new DateTime(2026, 6, 10), new DateTime(2026, 1, 2)),
            BuildRental("R20260601-0003", renter, new DateTime(2026, 6, 15), new DateTime(2026, 1, 3)));
        await context.SaveChangesAsync();

        var service = BuildRentalService(context);

        var (items, total) = await service.ListAsync(new RentalQueryParameters
        {
            SortField = "expectedEndDate",
            SortOrder = "ascend",
            Page = 1,
            PageSize = 2
        });

        Assert.Equal(3, total);
        Assert.Equal(
            new[] { "R20260601-0002", "R20260601-0003" },
            items.Select(item => item.RentalNumber).ToArray());
    }

    [Fact]
    public async Task ListAsync_sortsExpectedReturnDateWithNullsLast()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var missingReturnDate = BuildRental("R20260601-0001", renter, new DateTime(2026, 6, 12), new DateTime(2026, 1, 1));
        var latestReturnDate = BuildRental("R20260601-0002", renter, new DateTime(2026, 6, 12), new DateTime(2026, 1, 2));
        var earlierReturnDate = BuildRental("R20260601-0003", renter, new DateTime(2026, 6, 12), new DateTime(2026, 1, 3));
        latestReturnDate.ExpectedReturnDate = new DateTime(2026, 6, 20);
        earlierReturnDate.ExpectedReturnDate = new DateTime(2026, 6, 18);

        context.Rentals.AddRange(missingReturnDate, latestReturnDate, earlierReturnDate);
        await context.SaveChangesAsync();

        var service = BuildRentalService(context);

        var (items, total) = await service.ListAsync(new RentalQueryParameters
        {
            SortField = "expectedReturnDate",
            SortOrder = "descend",
            Page = 1,
            PageSize = 10
        });

        Assert.Equal(3, total);
        Assert.Equal(
            new[] { "R20260601-0002", "R20260601-0003", "R20260601-0001" },
            items.Select(item => item.RentalNumber).ToArray());
    }

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

    [Fact]
    public async Task SyncSfRoutesAsync_UsesAdminPhoneTailAfterRenterAndCreatorFallbacks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var adminRole = new Role { Name = BuiltInRoles.Admin, Description = "Administrator", IsBuiltIn = true };
        var admin = new User { Name = "Admin User", Mobile = "13900139000", Status = UserStatus.Active };
        var creator = new User { Name = "Creator", Mobile = "13700137000", Status = UserStatus.Active };
        var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20260602-0001",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" },
            CreatedBy = creator.Name,
            Status = RentalStatus.Active,
            StartDate = new DateTime(2026, 6, 2),
            ExpectedShipDate = new DateTime(2026, 6, 1),
            ExpectedEndDate = new DateTime(2026, 6, 5),
            TotalPrice = 100m
        };
        rental.Shipments.Add(new RentalShipment
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            TrackingNumber = "SF1234567890",
            ShippedAt = new DateTime(2026, 6, 1)
        });

        context.Roles.Add(adminRole);
        context.Users.AddRange(admin, creator);
        context.Rentals.Add(rental);
        await context.SaveChangesAsync();
        context.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = adminRole.Id, AssignedBy = "system" });
        await context.SaveChangesAsync();

        var sfExpress = new AdminFallbackSfExpressService("9000");
        var service = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            sfExpress,
            new StubSettlementService());

        var (result, error) = await service.SyncSfRoutesAsync(rental.Id, forceRefresh: true, "tester");

        Assert.Null(error);
        Assert.NotNull(result);
        var route = Assert.Single(result!.Shipments);
        Assert.Equal("9000", route.CheckPhoneNo);
        Assert.Single(route.Routes);
        Assert.Equal(new[] { "8000", "7000", "9000" }, sfExpress.RequestedPhoneTails);
    }

    private static Rental BuildRental(string rentalNumber, Renter renter, DateTime expectedEndDate, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        RentalNumber = rentalNumber,
        Renter = renter,
        Status = RentalStatus.Active,
        StartDate = expectedEndDate.AddDays(-3),
        ExpectedShipDate = expectedEndDate.AddDays(-4),
        ExpectedEndDate = expectedEndDate,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        TotalPrice = 100m
    };

    private static RentalService BuildRentalService(ApplicationDbContext context) => new(
        context,
        new StubRenterService(),
        new StubIdentityService(),
        Array.Empty<INotificationChannel>(),
        new StubSfExpressService(DateTime.UtcNow),
        new StubSettlementService());

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

    private sealed class AdminFallbackSfExpressService : ISfExpressService
    {
        private readonly string _adminPhoneTail;

        public AdminFallbackSfExpressService(string adminPhoneTail)
        {
            _adminPhoneTail = adminPhoneTail;
        }

        public List<string> RequestedPhoneTails { get; } = new();

        public Task<IReadOnlyList<SfRouteQueryResult>> QueryRoutesAsync(
            IReadOnlyList<SfRouteQueryItem> items,
            bool forceRefresh,
            CancellationToken ct = default)
        {
            RequestedPhoneTails.AddRange(items.Select(item => item.CheckPhoneNo));
            IReadOnlyList<SfRouteQueryResult> results = items.Select(item => new SfRouteQueryResult
            {
                ShipmentId = item.ShipmentId,
                TrackingNumber = item.TrackingNumber,
                CheckPhoneNo = item.CheckPhoneNo,
                Queryable = true,
                Routes = item.CheckPhoneNo == _adminPhoneTail
                    ? new List<SfRouteNodeDto> { new() { AcceptTime = "2026-06-02 10:00:00", Remark = "In transit" } }
                    : new List<SfRouteNodeDto>()
            }).ToList();

            return Task.FromResult(results);
        }
    }

    private sealed class StubRenterService : IRenterService
    {
        public Task<IEnumerable<RenterDto>> SearchAsync(string? keyword, int limit) => Task.FromResult(Enumerable.Empty<RenterDto>());
        public Task<RenterDto?> GetByIdAsync(Guid id) => Task.FromResult<RenterDto?>(null);
        public Task<RenterDto> CreateAsync(CreateRenterDto dto, string? currentUser) => throw new NotImplementedException();
        public Task<RenterDto?> UpdateAsync(Guid id, UpdateRenterDto dto, string? currentUser) => Task.FromResult<RenterDto?>(null);
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

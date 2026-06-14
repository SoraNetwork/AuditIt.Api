using AuditIt.Api.Data;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AuditIt.Api.Tests;

public class RentalOccupancyAndValueTests
{
    [Fact]
    public async Task ItemValue_CanBeSavedAndRetrieved()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
        var category = new Category { Name = "Camera", Description = "Camera category" };
        var definition = new ItemDefinition { Name = "Camera Body", Category = category, Unit = "pcs", Description = "Body" };
        
        var item1 = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.InStock,
            ItemValue = 12999.50m
        };

        var item2 = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-002",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.InStock,
            ItemValue = null
        };

        context.Items.AddRange(item1, item2);
        await context.SaveChangesAsync();

        // Retrieve from a new DbContext instance to verify persistence
        await using var readContext = new ApplicationDbContext(options);
        var retrieved1 = await readContext.Items.FindAsync(item1.Id);
        var retrieved2 = await readContext.Items.FindAsync(item2.Id);

        Assert.NotNull(retrieved1);
        Assert.Equal(12999.50m, retrieved1!.ItemValue);

        Assert.NotNull(retrieved2);
        Assert.Null(retrieved2!.ItemValue);
    }

    [Fact]
    public async Task UpdateRentalItemsAsync_PhysicallyRemovesItem_IfRentalHasNotStarted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
        var category = new Category { Name = "Camera", Description = "Camera category" };
        var definition = new ItemDefinition { Name = "Camera Body", Category = category, Unit = "pcs", Description = "Body" };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };

        var item1 = new Item { Id = Guid.NewGuid(), ShortId = "CAM-001", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.InStock };
        var item2 = new Item { Id = Guid.NewGuid(), ShortId = "CAM-002", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.InStock };
        context.Items.AddRange(item1, item2);

        var rentalId = Guid.NewGuid();
        var rental = new Rental
        {
            Id = rentalId,
            RentalNumber = "R20260610-0001",
            Renter = renter,
            Status = RentalStatus.Pending,
            StartDate = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            ExpectedShipDate = new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc),
            ExpectedEndDate = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc)
        };
        context.Rentals.Add(rental);

        // Associate item1 with the rental
        var rentalItem1 = new RentalItem
        {
            RentalId = rentalId,
            ItemId = item1.Id,
            ItemShortIdSnapshot = item1.ShortId,
            ItemNameSnapshot = definition.Name
        };
        context.RentalItems.Add(rentalItem1);
        await context.SaveChangesAsync();

        // Verify initially busy calendar check includes item1
        var busyRentalsBefore = await context.Rentals
            .Include(r => r.Items)
            .Where(r => r.Status != RentalStatus.Cancelled)
            .Where(r => r.Items.Any(ri => ri.ItemId == item1.Id && (ri.ReturnedAt == null || ri.ReturnedAt > r.StartDate)))
            .ToListAsync();
        Assert.Single(busyRentalsBefore);

        // Update items to remove item1 and add item2
        var rentalService = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(),
            new StubSettlementService());
            
        var updateDto = new UpdateRentalItemsDto
        {
            ItemIds = new List<string> { item2.Id.ToString() }
        };
        
        var result = await rentalService.UpdateRentalItemsAsync(rentalId, updateDto, "TestUser");
        Assert.Null(result.Error);
        Assert.NotNull(result.Rental);

        // Verify database state
        var rentalItems = await context.RentalItems.ToListAsync();
        // Item1 should be physically deleted, Item2 should be added
        var riItem1 = rentalItems.FirstOrDefault(ri => ri.ItemId == item1.Id);
        var riItem2 = rentalItems.FirstOrDefault(ri => ri.ItemId == item2.Id);

        Assert.Null(riItem1);
        Assert.NotNull(riItem2);

        // Verify busy calendar check for item1 is now empty
        var busyRentalsAfter1 = await context.Rentals
            .Include(r => r.Items)
            .Where(r => r.Status != RentalStatus.Cancelled)
            .Where(r => r.Items.Any(ri => ri.ItemId == item1.Id && (ri.ReturnedAt == null || ri.ReturnedAt > r.StartDate)))
            .ToListAsync();
        Assert.Empty(busyRentalsAfter1);

        // Verify busy calendar check for item2 now shows the rental
        var busyRentalsAfter2 = await context.Rentals
            .Include(r => r.Items)
            .Where(r => r.Status != RentalStatus.Cancelled)
            .Where(r => r.Items.Any(ri => ri.ItemId == item2.Id && (ri.ReturnedAt == null || ri.ReturnedAt > r.StartDate)))
            .ToListAsync();
        Assert.Single(busyRentalsAfter2);
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

    private sealed class StubSfExpressService : ISfExpressService
    {
        public Task<SfShipmentRouteDto?> AddOrUpdateShipmentAsync(Guid shipmentId, string carrier, string trackingNumber, string phoneSuffix, string? currentUser, CancellationToken ct = default) =>
            Task.FromResult<SfShipmentRouteDto?>(null);

        public Task<SfShipmentRouteDto?> QueryRouteAsync(Guid shipmentId, bool forceRefresh = false, CancellationToken ct = default) =>
            Task.FromResult<SfShipmentRouteDto?>(null);

        public Task<IReadOnlyList<SfRouteQueryResult>> QueryRoutesAsync(IReadOnlyList<SfRouteQueryItem> items, bool forceRefresh, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SfRouteQueryResult>>(Array.Empty<SfRouteQueryResult>());
    }
}

using AuditIt.Api.Controllers;
using AuditIt.Api.Data;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AuditIt.Api.Tests;

public partial class RentalOccupancyAndValueTests
{
    [Theory]
    [InlineData(ItemStatus.InStock, 0)]
    [InlineData(ItemStatus.InStock, 1)]
    [InlineData(ItemStatus.Disposed, 1)]
    [InlineData(ItemStatus.SuspectedMissing, 1)]
    public async Task DefinitionDemand_RejectsInsufficientStockEvenWithoutOtherRentals(ItemStatus status, int count)
    {
        await using var f = await OccupancyFixture.Create(count);
        foreach (var item in f.Items) item.Status = status;
        await f.Db.SaveChangesAsync();
        var request = f.Request(f.Today.AddDays(1), f.Today.AddDays(3));
        request.ItemDefinitionIds = new() { f.Definition.Id, f.Definition.Id };
        var result = await CreateRentalService(f.Db).CreateAsync(request, "tester");
        Assert.NotNull(result.Conflict);
        Assert.Null(result.Rental);
        Assert.Empty(await f.Db.Rentals.ToListAsync());
    }

    [Fact]
    public async Task ManualLoan_WithFutureReservation_IsStillCountedOnEarlierDays()
    {
        await using var f = await OccupancyFixture.Create(2);
        f.Loan(f.Items[0], f.Today, f.Today.AddDays(2));
        f.Rental(f.Today.AddDays(5), f.Today.AddDays(8), f.Items[0]);
        f.Rental(f.Today, f.Today.AddDays(2), f.Items[1]);
        await f.Db.SaveChangesAsync();
        var result = await CreateRentalService(f.Db).CreateAsync(f.Request(f.Today, f.Today.AddDays(8)), "tester");
        Assert.NotNull(result.Conflict);
        Assert.Contains(result.Conflict!.ShippedConflicts, c => c.StartDate == f.Today && c.ConflictReason!.Contains("普通借出: 1"));
        var calendar = await f.Calendar(f.Today, f.Today.AddDays(8));
        Assert.Equal(2, calendar.DailyStocks[0].OccupiedCount);
        Assert.Single(calendar.DailyStocks[0].Details.Where(d => d.IsManualLoan));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SpecificItem_ManualLoanCannotBeHiddenByOtherAvailableStock(bool hasAudit)
    {
        await using var f = await OccupancyFixture.Create(2);
        f.Loan(f.Items[0], f.Today, f.Today.AddDays(2), hasAudit);
        await f.Db.SaveChangesAsync();
        var request = f.Request(f.Today, f.Today.AddDays(1));
        request.ItemDefinitionIds.Clear();
        request.ItemIds.Add(f.Items[0].Id.ToString());
        var result = await CreateRentalService(f.Db).CreateAsync(request, "tester");
        Assert.NotNull(result.Conflict);
        Assert.Contains(result.Conflict!.ShippedConflicts, c => c.ItemId == f.Items[0].Id);
        var availability = await f.Availability(f.Items[0], f.Today, f.Today.AddDays(3));
        Assert.Equal(f.Today.AddDays(3).AddTicks(-1), Assert.Single(availability.BusyPeriods).EndAt);
        Assert.Equal(f.Today.AddDays(3), Assert.Single(availability.FreePeriods).StartAt);
    }

    [Fact]
    public async Task PartialShipments_UseEachItemsOutboundDateAndExtendOnlyOpenItems()
    {
        await using var f = await OccupancyFixture.Create(2);
        var rental = f.Rental(f.Today.AddDays(-10), f.Today.AddDays(-5), f.Items.ToArray());
        rental.Status = RentalStatus.PartiallyShipped;
        f.Ship(rental, rental.Items.First(), f.Today.AddDays(-9));
        await f.Db.SaveChangesAsync();
        var calendar = await f.Calendar(f.Today.AddDays(-9), f.Today.AddDays(1));
        Assert.Equal(1, calendar.DailyStocks[0].OccupiedCount);
        Assert.Equal(2, calendar.DailyStocks.Single(d => d.Date == f.Today).OccupiedCount);
        Assert.Equal(0, calendar.DailyStocks.Last().OccupiedCount);
        var unshipped = await f.Availability(f.Items[1], f.Today.AddDays(-9), f.Today.AddDays(1));
        Assert.Equal(f.Today, Assert.Single(unshipped.BusyPeriods).StartAt);
        var shipped = await f.Availability(f.Items[0], f.Today, f.Today);
        Assert.Single(shipped.BusyPeriods);
    }

    [Fact]
    public async Task LateOutbound_DoesNotCreateReturningOccupancyBeforeItShips()
    {
        await using var f = await OccupancyFixture.Create(1);
        var rental = f.Rental(f.Today.AddDays(-10), f.Today.AddDays(-7), f.Items.ToArray());
        rental.Status = RentalStatus.Active;
        f.Ship(rental, rental.Items.Single(), f.Today.AddDays(-1));
        await f.Db.SaveChangesAsync();
        var calendar = await f.Calendar(f.Today.AddDays(-5), f.Today);
        Assert.All(calendar.DailyStocks.Take(4), d => Assert.Equal(0, d.OccupiedCount));
        Assert.Equal(1, calendar.DailyStocks.Last().OccupiedCount);
    }

    [Fact]
    public async Task EarlyReturnBeforeRentalStart_RemainsVisibleThroughReturnDayInBothCalendars()
    {
        await using var f = await OccupancyFixture.Create(1);
        var rental = f.Rental(f.Today.AddDays(-3), f.Today.AddDays(5), f.Items.ToArray());
        rental.StartDate = f.Today.AddDays(1);
        rental.Status = RentalStatus.Returned;
        rental.ActualEndDate = f.Today;
        rental.Items.Single().ReturnedAt = f.Today;
        f.Ship(rental, rental.Items.Single(), f.Today.AddDays(-3));
        await f.Db.SaveChangesAsync();
        Assert.Equal(1, (await f.Calendar(f.Today, f.Today)).DailyStocks.Single().OccupiedCount);
        Assert.Single((await f.Availability(f.Items[0], f.Today, f.Today)).BusyPeriods);
        Assert.Empty((await f.Availability(f.Items[0], f.Today.AddDays(1), f.Today.AddDays(1))).BusyPeriods);
    }

    [Fact]
    public async Task ReturnedRenewal_DoesNotExtendAnEarlyReturnedItemToOtherItemsReturnDate()
    {
        await using var f = await OccupancyFixture.Create(2);
        var rental = f.Rental(f.Today.AddDays(-5), f.Today.AddDays(-3), f.Items.ToArray());
        rental.RenewedFromRentalId = Guid.NewGuid();
        rental.Status = RentalStatus.Returned;
        rental.ActualEndDate = f.Today;
        rental.Items.First().ReturnedAt = f.Today.AddDays(-2);
        rental.Items.Last().ReturnedAt = f.Today;
        await f.Db.SaveChangesAsync();
        var day = (await f.Calendar(f.Today, f.Today)).DailyStocks.Single();
        Assert.Equal(1, day.OccupiedCount);
        Assert.Empty((await f.Availability(f.Items[0], f.Today, f.Today)).BusyPeriods);
    }

    [Fact]
    public async Task UpdateDates_UsesActualOutboundWhenExpectedShipDateIsMovedLater()
    {
        await using var f = await OccupancyFixture.Create(2);
        var current = f.Rental(f.Today.AddDays(-2), f.Today.AddDays(3), f.Items[0]);
        current.Status = RentalStatus.Active;
        f.Ship(current, current.Items.Single(), f.Today.AddDays(-2));
        f.Rental(f.Today, f.Today.AddDays(1), f.Items[0]);
        await f.Db.SaveChangesAsync();
        var result = await CreateRentalService(f.Db).UpdateAsync(current.Id,
            new UpdateRentalDto { ExpectedShipDate = f.Today.AddDays(2) }, "tester");
        Assert.NotNull(result.Conflict);
        await f.Db.Entry(current).ReloadAsync();
        Assert.Equal(f.Today.AddDays(-2), current.ExpectedShipDate);
    }

    [Fact]
    public async Task UpdateDates_ExtensionConflictsButShorteningImmediatelyReleasesDays()
    {
        await using var f = await OccupancyFixture.Create(1);
        var current = f.Rental(f.Today.AddDays(1), f.Today.AddDays(4), f.Items.ToArray());
        f.Rental(f.Today.AddDays(6), f.Today.AddDays(8), f.Items.ToArray());
        await f.Db.SaveChangesAsync();
        var service = CreateRentalService(f.Db);
        var rejected = await service.UpdateAsync(current.Id, new UpdateRentalDto { ExpectedReturnDate = f.Today.AddDays(6) }, "tester");
        Assert.NotNull(rejected.Conflict);
        var accepted = await service.UpdateAsync(current.Id,
            new UpdateRentalDto { ExpectedEndDate = f.Today.AddDays(2), ExpectedReturnDate = f.Today.AddDays(3) }, "tester");
        Assert.NotNull(accepted.Rental);
        Assert.Equal(0, (await f.Calendar(f.Today.AddDays(4), f.Today.AddDays(4))).DailyStocks.Single().OccupiedCount);
    }

    [Fact]
    public async Task UpdateItems_AddedAfterShipmentDoesNotBackdateNewItemsOccupancy()
    {
        await using var f = await OccupancyFixture.Create(2);
        var current = f.Rental(f.Today.AddDays(-4), f.Today.AddDays(3), f.Items[0]);
        current.Status = RentalStatus.Active;
        f.Ship(current, current.Items.Single(), f.Today.AddDays(-4));
        await f.Db.SaveChangesAsync();
        var result = await CreateRentalService(f.Db).UpdateRentalItemsAsync(current.Id,
            new UpdateRentalItemsDto { ItemIds = f.Items.Select(i => i.Id.ToString()).ToList() }, "tester");
        Assert.NotNull(result.Rental);
        Assert.Empty((await f.Availability(f.Items[1], f.Today.AddDays(-3), f.Today.AddDays(-1))).BusyPeriods);
        Assert.Single((await f.Availability(f.Items[1], f.Today, f.Today)).BusyPeriods);
    }

    [Fact]
    public async Task WarehouseProjection_IsIndependentOfRequestedRangeAndAccountsForManualLoans()
    {
        await using var f = await OccupancyFixture.Create(2);
        var otherWarehouse = new Warehouse { Name = "Other", Location = "B", Description = "B" };
        f.Items[1].Warehouse = otherWarehouse;
        f.Loan(f.Items[0], f.Today, f.Today.AddDays(2));
        var order = f.Rental(f.Today, f.Today.AddDays(2));
        order.Items.Add(new RentalItem { ItemDefinition = f.Definition, ItemNameSnapshot = f.Definition.Name });
        f.Rental(f.Today.AddDays(5), f.Today.AddDays(7), f.Items[1]);
        await f.Db.SaveChangesAsync();
        var single = await f.Calendar(f.Today, f.Today, otherWarehouse.Id);
        var wide = await f.Calendar(f.Today.AddDays(-2), f.Today.AddDays(8), otherWarehouse.Id);
        Assert.Equal(1, single.DailyStocks.Single().OccupiedCount);
        Assert.True(Assert.Single(single.DailyStocks.Single().Details).IsUncertain);
        Assert.Equal(single.DailyStocks.Single().OccupiedCount, wide.DailyStocks.Single(d => d.Date == f.Today).OccupiedCount);
        Assert.Equal(1, (await f.Calendar(f.Today, f.Today, f.Warehouse.Id)).DailyStocks.Single().OccupiedCount);
    }

    [Fact]
    public async Task CancelledAndRemovedBeforeShipment_DoNotOccupyFutureDates()
    {
        await using var f = await OccupancyFixture.Create(2);
        var cancelled = f.Rental(f.Today.AddDays(1), f.Today.AddDays(5), f.Items[0]);
        cancelled.Status = RentalStatus.Cancelled;
        var removed = f.Rental(f.Today.AddDays(1), f.Today.AddDays(5), f.Items[1]);
        removed.Items.Single().ReturnedAt = f.Today;
        removed.Items.Single().ReleasedFromRentalAt = f.Today;
        await f.Db.SaveChangesAsync();
        Assert.All((await f.Calendar(f.Today, f.Today.AddDays(6))).DailyStocks, d => Assert.Equal(0, d.OccupiedCount));
    }

    [Fact]
    public async Task DefinitionAndItemCalendars_AgreeAcrossRenewalIntentAndReturnBuffer()
    {
        await using var f = await OccupancyFixture.Create(1);
        var rental = f.Rental(f.Today.AddDays(1), f.Today.AddDays(3), f.Items.ToArray());
        rental.HasRenewalIntent = true;
        rental.RenewalIntentEndDate = f.Today.AddDays(5);
        rental.ExpectedReturnDate = f.Today.AddDays(7);
        await f.Db.SaveChangesAsync();
        var definition = await f.Calendar(f.Today, f.Today.AddDays(8));
        var item = await f.Availability(f.Items[0], f.Today, f.Today.AddDays(8));
        foreach (var day in definition.DailyStocks)
        {
            var busy = item.BusyPeriods.Where(p => p.StartAt.Date <= day.Date && p.EndAt.Date >= day.Date).ToList();
            Assert.Equal(day.OccupiedCount, busy.Count);
            if (busy.Count > 0) Assert.Equal(day.Details.Single().OccupancyStatus, busy.Single().OccupancyStatus);
        }
    }

    private sealed class OccupancyFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public ApplicationDbContext Db { get; }
        public DateTime Today => BusinessToday();
        public Warehouse Warehouse { get; } = new() { Name = "Main", Location = "A", Description = "A" };
        public ItemDefinition Definition { get; } = new() { Name = "Camera", Unit = "pcs", Description = "Camera", Category = new Category { Name = "Equipment", Description = "Equipment" } };
        public List<Item> Items { get; } = new();
        private OccupancyFixture(SqliteConnection connection)
        {
            this.connection = connection;
            Db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        }
        public static async Task<OccupancyFixture> Create(int count)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var f = new OccupancyFixture(connection);
            await f.Db.Database.EnsureCreatedAsync();
            f.Db.AddRange(f.Warehouse, f.Definition);
            for (var i = 0; i < count; i++) f.Items.Add(new Item { Id = Guid.NewGuid(), ShortId = $"CAM-{i}", Warehouse = f.Warehouse, ItemDefinition = f.Definition, Status = ItemStatus.InStock });
            f.Db.Items.AddRange(f.Items);
            await f.Db.SaveChangesAsync();
            return f;
        }
        public Rental Rental(DateTime start, DateTime end, params Item[] items)
        {
            var rental = new Rental { Id = Guid.NewGuid(), RentalNumber = $"R-{Guid.NewGuid():N}", Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant" }, Status = RentalStatus.Pending, StartDate = start, ExpectedShipDate = start, ExpectedEndDate = end, ExpectedReturnDate = end };
            foreach (var item in items) rental.Items.Add(new RentalItem { Item = item, ItemDefinition = Definition, ItemShortIdSnapshot = item.ShortId, ItemNameSnapshot = Definition.Name });
            Db.Rentals.Add(rental);
            return rental;
        }
        public void Ship(Rental rental, RentalItem item, DateTime day)
        {
            rental.Shipments.Add(new RentalShipment { Direction = ShipmentDirection.Outbound, OriginWarehouse = Warehouse, Carrier = "Test", ShippedAt = day, RentalItems = new List<RentalShipmentItem> { new() { RentalItem = item } } });
        }
        public void Loan(Item item, DateTime day, DateTime? expected, bool audit = true)
        {
            item.Status = ItemStatus.LoanedOut; item.CurrentDestination = "Borrower"; item.LastUpdated = day; item.ExpectedReturnDate = expected;
            if (audit) Db.AuditLogs.Add(new AuditLog { ItemId = item.Id, WarehouseId = Warehouse.Id, Action = AuditAction.Outbound, Timestamp = day, ItemName = Definition.Name, WarehouseName = Warehouse.Name, Destination = "Borrower" });
        }
        public CreateRentalDto Request(DateTime start, DateTime end) => new() { Renter = new RenterInlineDto { Name = "New tenant" }, ItemDefinitionIds = new() { Definition.Id }, StartDate = start, ExpectedShipDate = start, ExpectedEndDate = end, ExpectedReturnDate = end };
        public async Task<ItemDefinitionOccupancyCalendarDto> Calendar(DateTime from, DateTime to, int? warehouse = null) => Assert.IsType<ItemDefinitionOccupancyCalendarDto>(Assert.IsType<OkObjectResult>((await new ItemDefinitionsController(Db).GetOccupancyCalendar(Definition.Id, from, to, warehouse)).Result).Value);
        public async Task<ItemAvailabilityCalendarDto> Availability(Item item, DateTime from, DateTime to) => Assert.IsType<ItemAvailabilityCalendarDto>(Assert.IsType<OkObjectResult>((await new ItemsController(Db, new StubWebHostEnvironment()).GetAvailability(item.Id, from, to)).Result).Value);
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}

using AuditIt.Api.Controllers;
using AuditIt.Api.Data;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
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

    [Fact]
    public async Task UpdateRentalItemsAsync_CanKeepAndAddItemDefinitions()
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
        var camera = new ItemDefinition { Name = "Camera Body", Category = category, Unit = "pcs", Description = "Body" };
        var lens = new ItemDefinition { Name = "Prime Lens", Category = category, Unit = "pcs", Description = "Lens" };
        var cameraItem = new Item { Id = Guid.NewGuid(), ShortId = "CAM-001", Warehouse = warehouse, ItemDefinition = camera, Status = ItemStatus.InStock };
        var lensStock = new Item { Id = Guid.NewGuid(), ShortId = "LEN-001", Warehouse = warehouse, ItemDefinition = lens, Status = ItemStatus.InStock };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var rentalId = Guid.NewGuid();

        context.Items.AddRange(cameraItem, lensStock);
        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = "R20260610-0002",
            Renter = renter,
            Status = RentalStatus.Pending,
            StartDate = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            ExpectedShipDate = new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc),
            ExpectedEndDate = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc)
        });
        context.RentalItems.AddRange(
            new RentalItem
            {
                RentalId = rentalId,
                ItemId = cameraItem.Id,
                ItemShortIdSnapshot = cameraItem.ShortId,
                ItemNameSnapshot = camera.Name
            },
            new RentalItem
            {
                RentalId = rentalId,
                ItemDefinition = camera,
                ItemShortIdSnapshot = "Pending",
                ItemNameSnapshot = camera.Name
            });
        await context.SaveChangesAsync();

        var rentalService = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(),
            new StubSettlementService());

        var result = await rentalService.UpdateRentalItemsAsync(rentalId, new UpdateRentalItemsDto
        {
            ItemIds = new List<string> { cameraItem.Id.ToString() },
            ItemDefinitionIds = new List<int> { camera.Id, lens.Id }
        }, "TestUser");

        Assert.Null(result.Error);
        Assert.NotNull(result.Rental);

        var activeItems = await context.RentalItems
            .Where(ri => ri.RentalId == rentalId && ri.ReturnedAt == null)
            .ToListAsync();

        Assert.Equal(3, activeItems.Count);
        Assert.Contains(activeItems, ri => ri.ItemId == cameraItem.Id);
        Assert.Contains(activeItems, ri => ri.ItemId == null && ri.ItemDefinitionId == camera.Id);
        Assert.Contains(activeItems, ri => ri.ItemId == null && ri.ItemDefinitionId == lens.Id);
    }

    [Fact]
    public async Task UpdateRentalItemsAsync_RejectsItemDefinitionsAfterRentalStarted()
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
        var camera = new ItemDefinition { Name = "Camera Body", Category = category, Unit = "pcs", Description = "Body" };
        var lens = new ItemDefinition { Name = "Prime Lens", Category = category, Unit = "pcs", Description = "Lens" };
        var cameraItem = new Item { Id = Guid.NewGuid(), ShortId = "CAM-001", Warehouse = warehouse, ItemDefinition = camera, Status = ItemStatus.LoanedOut };
        var lensStock = new Item { Id = Guid.NewGuid(), ShortId = "LEN-001", Warehouse = warehouse, ItemDefinition = lens, Status = ItemStatus.InStock };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var rentalId = Guid.NewGuid();

        context.Items.AddRange(cameraItem, lensStock);
        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = "R20260610-0004",
            Renter = renter,
            Status = RentalStatus.Active,
            StartDate = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            ExpectedShipDate = new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc),
            ExpectedEndDate = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc)
        });
        context.RentalItems.Add(new RentalItem
        {
            RentalId = rentalId,
            ItemId = cameraItem.Id,
            ItemShortIdSnapshot = cameraItem.ShortId,
            ItemNameSnapshot = camera.Name
        });
        context.RentalShipments.Add(new RentalShipment
        {
            RentalId = rentalId,
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            ShippedAt = new DateTime(2026, 6, 14, 10, 0, 0, DateTimeKind.Utc)
        });
        await context.SaveChangesAsync();

        var rentalService = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(),
            new StubSettlementService());

        var result = await rentalService.UpdateRentalItemsAsync(rentalId, new UpdateRentalItemsDto
        {
            ItemDefinitionIds = new List<int> { lens.Id }
        }, "TestUser");

        Assert.NotNull(result.Error);
        Assert.Null(result.Rental);
        Assert.DoesNotContain(
            await context.RentalItems.Where(ri => ri.RentalId == rentalId).ToListAsync(),
            ri => ri.ItemId == null && ri.ItemDefinitionId == lens.Id);
    }

    [Fact]
    public async Task DefinitionOccupancy_UsesActualOutboundShipmentDate_IfShippedBeforeRentalStart()
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
        var item = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.LoanedOut
        };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var rentalId = Guid.NewGuid();
        var shippedAt = new DateTime(2026, 6, 18, 10, 0, 0, DateTimeKind.Utc);

        var rental = new Rental
        {
            Id = rentalId,
            RentalNumber = "R20260618-0001",
            Renter = renter,
            Status = RentalStatus.Active,
            StartDate = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc),
            ExpectedShipDate = new DateTime(2026, 6, 19, 0, 0, 0, DateTimeKind.Utc),
            ExpectedEndDate = new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc)
        };

        context.Rentals.Add(rental);
        context.RentalItems.Add(new RentalItem
        {
            RentalId = rentalId,
            ItemId = item.Id,
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name
        });
        context.RentalShipments.Add(new RentalShipment
        {
            RentalId = rentalId,
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            ShippedAt = shippedAt
        });
        await context.SaveChangesAsync();

        var controller = new ItemDefinitionsController(context);
        var action = await controller.GetOccupancyCalendar(definition.Id, shippedAt, shippedAt);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(ok.Value);
        var day = Assert.Single(calendar.DailyStocks);
        Assert.Equal(shippedAt.AddHours(8).Date, day.Date);
        Assert.Equal(1, day.OccupiedCount);
        Assert.Single(day.Details);
    }

    [Fact]
    public async Task DefinitionOccupancy_ExcludesReturnedItemsOnReturnDate()
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
        var item = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.InStock
        };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var rentalId = Guid.NewGuid();
        var returnedAt = new DateTime(2026, 6, 12, 10, 0, 0, DateTimeKind.Utc);

        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = "R20260610-0003",
            Renter = renter,
            Status = RentalStatus.Returned,
            StartDate = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc),
            ExpectedShipDate = new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc),
            ExpectedEndDate = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            ActualEndDate = returnedAt
        });
        context.RentalItems.Add(new RentalItem
        {
            RentalId = rentalId,
            ItemId = item.Id,
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name,
            ReturnedAt = returnedAt,
            ReturnCondition = ReturnCondition.Good
        });
        await context.SaveChangesAsync();

        var controller = new ItemDefinitionsController(context);
        var action = await controller.GetOccupancyCalendar(
            definition.Id,
            new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 13, 0, 0, 0, DateTimeKind.Utc));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(ok.Value);

        var beforeReturnDay = calendar.DailyStocks.Single(day => day.Date == new DateTime(2026, 6, 11));
        var returnDay = calendar.DailyStocks.Single(day => day.Date == new DateTime(2026, 6, 12));
        var afterReturnDay = calendar.DailyStocks.Single(day => day.Date == new DateTime(2026, 6, 13));

        Assert.Equal(1, beforeReturnDay.OccupiedCount);
        Assert.Equal(0, returnDay.OccupiedCount);
        Assert.Empty(returnDay.Details);
        Assert.Equal(0, afterReturnDay.OccupiedCount);
    }

    [Fact]
    public async Task DefinitionOccupancy_ExtendsOpenRentalAfterExpectedEndOnlyUntilToday()
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
        var item = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.LoanedOut
        };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var rentalId = Guid.NewGuid();
        var today = BusinessToday();
        var expectedEndDate = today.AddDays(-1);
        var futureDate = today.AddDays(1);

        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = "R20260610-0005",
            Renter = renter,
            Status = RentalStatus.Overdue,
            StartDate = expectedEndDate.AddDays(-10),
            ExpectedShipDate = expectedEndDate.AddDays(-11),
            ExpectedEndDate = expectedEndDate
        });
        context.RentalItems.Add(new RentalItem
        {
            RentalId = rentalId,
            ItemId = item.Id,
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name
        });
        context.RentalShipments.Add(new RentalShipment
        {
            RentalId = rentalId,
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            ShippedAt = expectedEndDate.AddDays(-11).AddHours(10)
        });
        await context.SaveChangesAsync();

        var controller = new ItemDefinitionsController(context);
        var action = await controller.GetOccupancyCalendar(
            definition.Id,
            expectedEndDate,
            futureDate);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(ok.Value);

        var expectedEndDay = calendar.DailyStocks.Single(day => day.Date == expectedEndDate);
        var todayStock = calendar.DailyStocks.Single(day => day.Date == today);
        var futureStock = calendar.DailyStocks.Single(day => day.Date == futureDate);

        Assert.Equal(1, expectedEndDay.OccupiedCount);
        Assert.Equal(ItemOccupancyStatus.Scheduled, Assert.Single(expectedEndDay.Details).OccupancyStatus);
        Assert.Equal(1, todayStock.OccupiedCount);
        Assert.Equal(ItemOccupancyStatus.Returning, Assert.Single(todayStock.Details).OccupancyStatus);
        Assert.Equal(0, futureStock.OccupiedCount);
        Assert.Empty(futureStock.Details);
    }

    [Fact]
    public async Task ItemAvailability_ExtendsOpenRentalAfterExpectedEndOnlyUntilToday()
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
        var item = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.LoanedOut
        };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var rentalId = Guid.NewGuid();
        var today = BusinessToday();
        var expectedEndDate = today.AddDays(-1);
        var futureDate = today.AddDays(1);

        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = "R20260610-0006",
            Renter = renter,
            Status = RentalStatus.Overdue,
            StartDate = expectedEndDate.AddDays(-10),
            ExpectedShipDate = expectedEndDate.AddDays(-11),
            ExpectedEndDate = expectedEndDate
        });
        context.RentalItems.Add(new RentalItem
        {
            RentalId = rentalId,
            ItemId = item.Id,
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name
        });
        context.RentalShipments.Add(new RentalShipment
        {
            RentalId = rentalId,
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            ShippedAt = expectedEndDate.AddDays(-11).AddHours(10)
        });
        await context.SaveChangesAsync();

        var controller = new ItemsController(context, new StubWebHostEnvironment());
        var action = await controller.GetAvailability(
            item.Id,
            expectedEndDate,
            futureDate);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemAvailabilityCalendarDto>(ok.Value);

        Assert.Equal(2, calendar.BusyPeriods.Count);
        var scheduled = Assert.Single(calendar.BusyPeriods, period => period.OccupancyStatus == ItemOccupancyStatus.Scheduled);
        var returning = Assert.Single(calendar.BusyPeriods, period => period.OccupancyStatus == ItemOccupancyStatus.Returning);
        Assert.Equal(expectedEndDate, scheduled.StartAt);
        Assert.Equal(expectedEndDate.AddDays(1).AddTicks(-1), scheduled.EndAt);
        Assert.Equal(today, returning.StartAt);
        Assert.Equal(today.AddDays(1).AddTicks(-1), returning.EndAt);
        Assert.DoesNotContain(calendar.BusyPeriods, period => period.StartAt.Date > today);
    }

    [Fact]
    public async Task ItemAvailability_DoesNotShowReturnedRentalOnReturnDateAfterExpectedEnd()
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
        var item = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.InStock
        };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var today = BusinessToday();
        var expectedEndDate = today.AddDays(-1);
        var returnedAt = today.AddHours(2);
        var rentalId = Guid.NewGuid();

        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = "R20260610-0007",
            Renter = renter,
            Status = RentalStatus.Returned,
            StartDate = expectedEndDate.AddDays(-10),
            ExpectedShipDate = expectedEndDate.AddDays(-11),
            ExpectedEndDate = expectedEndDate,
            ActualEndDate = returnedAt
        });
        context.RentalItems.Add(new RentalItem
        {
            RentalId = rentalId,
            ItemId = item.Id,
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name,
            ReturnedAt = returnedAt,
            ReturnCondition = ReturnCondition.Good
        });
        context.RentalShipments.Add(new RentalShipment
        {
            RentalId = rentalId,
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            ShippedAt = expectedEndDate.AddDays(-11).AddHours(10)
        });
        await context.SaveChangesAsync();

        var controller = new ItemsController(context, new StubWebHostEnvironment());
        var action = await controller.GetAvailability(item.Id, today, today);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemAvailabilityCalendarDto>(ok.Value);

        Assert.Empty(calendar.BusyPeriods);
    }

    [Fact]
    public async Task CreateAsync_DoesNotConflictWithReturnedDefinitionOnReturnDate()
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
        var item = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.InStock
        };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var today = BusinessToday();
        var expectedEndDate = today.AddDays(-1);
        var returnedAt = today.AddHours(2);
        var rentalId = Guid.NewGuid();

        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = "R20260610-0008",
            Renter = renter,
            Status = RentalStatus.Returned,
            StartDate = expectedEndDate.AddDays(-10),
            ExpectedShipDate = expectedEndDate.AddDays(-11),
            ExpectedEndDate = expectedEndDate,
            ActualEndDate = returnedAt
        });
        context.RentalItems.Add(new RentalItem
        {
            RentalId = rentalId,
            ItemId = item.Id,
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name,
            ReturnedAt = returnedAt,
            ReturnCondition = ReturnCondition.Good
        });
        context.RentalShipments.Add(new RentalShipment
        {
            RentalId = rentalId,
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            ShippedAt = expectedEndDate.AddDays(-11).AddHours(10)
        });
        await context.SaveChangesAsync();

        var rentalService = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(),
            new StubSettlementService());

        var result = await rentalService.CreateAsync(new CreateRentalDto
        {
            Renter = new RenterInlineDto { Name = "Next Tenant", Phone = "13900139000" },
            ItemDefinitionIds = new List<int> { definition.Id },
            StartDate = today,
            ExpectedShipDate = today,
            ExpectedEndDate = today.AddDays(1)
        }, "TestUser");

        Assert.Null(result.Conflict);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task AddShipmentAsync_DoesNotConflictWithReturnedPreviousRentalPendingInbound()
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
        var item = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.InStock
        };
        var previousRenter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var nextRenter = new Renter { Id = Guid.NewGuid(), Name = "Next Tenant", Phone = "13900139000" };
        var today = BusinessToday();
        var expectedEndDate = today.AddDays(-1);
        var returnedAt = today.AddHours(2);
        var previousRentalId = Guid.NewGuid();
        var nextRentalId = Guid.NewGuid();

        context.Rentals.Add(new Rental
        {
            Id = previousRentalId,
            RentalNumber = "R20260610-0009",
            Renter = previousRenter,
            Status = RentalStatus.Returned,
            StartDate = expectedEndDate.AddDays(-10),
            ExpectedShipDate = expectedEndDate.AddDays(-11),
            ExpectedEndDate = expectedEndDate,
            ActualEndDate = returnedAt
        });
        context.RentalItems.Add(new RentalItem
        {
            RentalId = previousRentalId,
            ItemId = item.Id,
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name,
            ReturnedAt = returnedAt,
            ReturnCondition = ReturnCondition.Good
        });
        context.RentalShipments.AddRange(
            new RentalShipment
            {
                RentalId = previousRentalId,
                Direction = ShipmentDirection.Outbound,
                OriginWarehouse = warehouse,
                Carrier = "SF",
                ShippedAt = expectedEndDate.AddDays(-11).AddHours(10),
                DeliveredAt = expectedEndDate.AddDays(-10).AddHours(10)
            },
            new RentalShipment
            {
                RentalId = previousRentalId,
                Direction = ShipmentDirection.Inbound,
                OriginWarehouse = warehouse,
                Carrier = "SF",
                ShippedAt = today.AddHours(1)
            });
        context.Rentals.Add(new Rental
        {
            Id = nextRentalId,
            RentalNumber = "R20260610-0010",
            Renter = nextRenter,
            Status = RentalStatus.Pending,
            StartDate = today,
            ExpectedShipDate = today,
            ExpectedEndDate = today.AddDays(1)
        });
        context.RentalItems.Add(new RentalItem
        {
            RentalId = nextRentalId,
            ItemId = item.Id,
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name
        });
        await context.SaveChangesAsync();

        var rentalService = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(),
            new StubSettlementService());

        var result = await rentalService.AddShipmentAsync(nextRentalId, new CreateShipmentDto
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouseId = warehouse.Id,
            Carrier = "SF",
            ShippedAt = today.AddHours(4)
        }, "TestUser");

        Assert.Null(result.Conflict);
        Assert.Null(result.Error);
        Assert.NotNull(result.Rental);
    }

    [Fact]
    public async Task CreateAsync_ConflictsWithOpenPreviousRentalAfterExpectedEnd()
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
        var item = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.LoanedOut
        };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var rentalId = Guid.NewGuid();
        var today = BusinessToday();
        var expectedEndDate = today.AddDays(-1);

        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = "R20260610-0007",
            Renter = renter,
            Status = RentalStatus.Overdue,
            StartDate = expectedEndDate.AddDays(-10),
            ExpectedShipDate = expectedEndDate.AddDays(-11),
            ExpectedEndDate = expectedEndDate
        });
        context.RentalItems.Add(new RentalItem
        {
            RentalId = rentalId,
            ItemId = item.Id,
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name
        });
        context.RentalShipments.Add(new RentalShipment
        {
            RentalId = rentalId,
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            ShippedAt = expectedEndDate.AddDays(-11).AddHours(10)
        });
        await context.SaveChangesAsync();

        var rentalService = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(),
            new StubSettlementService());

        var result = await rentalService.CreateAsync(new CreateRentalDto
        {
            Renter = new RenterInlineDto { Name = "Next Tenant", Phone = "13900139000" },
            ItemIds = new List<string> { item.Id.ToString() },
            StartDate = today,
            ExpectedShipDate = today,
            ExpectedEndDate = today.AddDays(2)
        }, "TestUser");

        Assert.NotNull(result.Conflict);
        Assert.Contains(result.Conflict!.ShippedConflicts, conflict => conflict.RentalId == rentalId);
    }

    [Fact]
    public async Task CreateAsync_DoesNotConflictWithOpenPreviousRentalBeyondToday()
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
        var item = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-002",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.LoanedOut
        };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var rentalId = Guid.NewGuid();
        var today = BusinessToday();
        var expectedEndDate = today.AddDays(-1);
        var futureStartDate = today.AddDays(1);

        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = "R20260610-0008",
            Renter = renter,
            Status = RentalStatus.Overdue,
            StartDate = expectedEndDate.AddDays(-10),
            ExpectedShipDate = expectedEndDate.AddDays(-11),
            ExpectedEndDate = expectedEndDate
        });
        context.RentalItems.Add(new RentalItem
        {
            RentalId = rentalId,
            ItemId = item.Id,
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name
        });
        context.RentalShipments.Add(new RentalShipment
        {
            RentalId = rentalId,
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            ShippedAt = expectedEndDate.AddDays(-11).AddHours(10)
        });
        await context.SaveChangesAsync();

        var rentalService = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(),
            new StubSettlementService());

        var result = await rentalService.CreateAsync(new CreateRentalDto
        {
            Renter = new RenterInlineDto { Name = "Next Tenant", Phone = "13900139000" },
            ItemIds = new List<string> { item.Id.ToString() },
            StartDate = futureStartDate,
            ExpectedShipDate = futureStartDate,
            ExpectedEndDate = futureStartDate.AddDays(2)
        }, "TestUser");

        Assert.Null(result.Conflict);
    }

    [Fact]
    public async Task Calendar_ShowsReturnRequiredForRenewalRentalDayAfterExpectedEndWithoutOutboundShipment()
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
        var item = new Item { Id = Guid.NewGuid(), ShortId = "CAM-001", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.LoanedOut };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var rentalId = Guid.NewGuid();
        var expectedEndDate = new DateTime(2099, 6, 25, 0, 0, 0, DateTimeKind.Utc);

        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = "R20990620-0001-01",
            Renter = renter,
            Status = RentalStatus.Active,
            StartDate = new DateTime(2099, 6, 20, 0, 0, 0, DateTimeKind.Utc),
            ExpectedShipDate = new DateTime(2099, 6, 20, 0, 0, 0, DateTimeKind.Utc),
            ExpectedEndDate = expectedEndDate,
            RenewedFromRentalId = Guid.NewGuid(),
            RenewedFromRentalNumber = "R20990610-0001",
            CreatedBy = "Alice"
        });
        context.RentalItems.Add(new RentalItem
        {
            RentalId = rentalId,
            ItemId = item.Id,
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name
        });
        await context.SaveChangesAsync();

        var rentalService = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(),
            new StubSettlementService());

        var sameDayEvents = await rentalService.GetCalendarAsync(
            new RentalCalendarQueryParameters { From = expectedEndDate, To = expectedEndDate },
            "Alice",
            includeReminders: false,
            canSeeAllReminders: false);

        Assert.DoesNotContain(sameDayEvents, item =>
            item.Kind == RentalCalendarEventKind.ReturnRequired
            && item.RentalId == rentalId);

        var returnRequiredDay = expectedEndDate.AddDays(1);
        var nextDayEvents = await rentalService.GetCalendarAsync(
            new RentalCalendarQueryParameters { From = returnRequiredDay, To = returnRequiredDay },
            "Alice",
            includeReminders: false,
            canSeeAllReminders: false);

        var returnRequired = Assert.Single(nextDayEvents, item =>
            item.Kind == RentalCalendarEventKind.ReturnRequired
            && item.RentalId == rentalId
            && item.RentalNumber == "R20990620-0001-01");
        Assert.Equal(returnRequiredDay, returnRequired.StartAt);
        Assert.Equal(returnRequiredDay, returnRequired.EndAt);
    }

    private sealed class StubRenterService : IRenterService
    {
        public Task<IEnumerable<RenterDto>> SearchAsync(string? keyword, int limit) => Task.FromResult(Enumerable.Empty<RenterDto>());
        public Task<RenterDto?> GetByIdAsync(Guid id) => Task.FromResult<RenterDto?>(null);
        public Task<RenterDto> CreateAsync(CreateRenterDto dto, string? currentUser) => throw new NotImplementedException();
        public Task<RenterDto?> UpdateAsync(Guid id, UpdateRenterDto dto) => Task.FromResult<RenterDto?>(null);
        public Task<bool> DeleteAsync(Guid id) => Task.FromResult(false);
        public Task<Renter> ResolveOrUpsertAsync(RenterInlineDto inline, string? currentUser) => Task.FromResult(new Renter
        {
            Id = inline.RenterId ?? Guid.NewGuid(),
            Name = inline.Name ?? "Tenant",
            Phone = inline.Phone
        });
    }

    private static DateTime BusinessToday() => DateTime.UtcNow.AddHours(8).Date;

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

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "AuditIt.Api.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
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

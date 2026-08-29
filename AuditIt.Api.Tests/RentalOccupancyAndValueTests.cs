using AuditIt.Api.Controllers;
using AuditIt.Api.Data;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace AuditIt.Api.Tests;

public class RentalOccupancyAndValueTests
{
    [Fact]
    public async Task CreateAsync_SavesPerItemPricesAndDerivesTotalPrice()
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
        var item1 = new Item { Id = Guid.NewGuid(), ShortId = "CAM-001", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.InStock };
        var item2 = new Item { Id = Guid.NewGuid(), ShortId = "CAM-002", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.InStock };
        context.Items.AddRange(item1, item2);
        await context.SaveChangesAsync();

        var rentalService = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(),
            new StubSettlementService());

        var startDate = BusinessToday().AddDays(1);
        var result = await rentalService.CreateAsync(new CreateRentalDto
        {
            Renter = new RenterInlineDto { Name = "Tenant", Phone = "13800138000" },
            ItemIds = new List<string> { item1.Id.ToString(), item2.Id.ToString() },
            StartDate = startDate,
            ExpectedShipDate = startDate,
            ExpectedEndDate = startDate.AddDays(3),
            TotalPrice = 999m,
            ItemPrices = new List<CreateRentalItemPriceDto>
            {
                new() { ItemId = item1.Id.ToString(), PerItemPrice = 120.5m },
                new() { ItemId = item2.Id.ToString(), PerItemPrice = 80m }
            }
        }, "TestUser");

        Assert.Null(result.Error);
        Assert.NotNull(result.Rental);
        Assert.Equal(200.5m, result.Rental!.TotalPrice);
        Assert.Equal(120.5m, result.Rental.Items.Single(item => item.ItemId == item1.Id).PerItemPrice);
        Assert.Equal(80m, result.Rental.Items.Single(item => item.ItemId == item2.Id).PerItemPrice);
    }

    [Fact]
    public async Task CreateAsync_WithoutExpectedShipDate_DefaultsToThreeDaysBeforeStartDate()
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
        var item = new Item { Id = Guid.NewGuid(), ShortId = "CAM-SHIP-DEFAULT-001", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.InStock };
        context.Items.Add(item);
        await context.SaveChangesAsync();

        var startDate = BusinessToday().AddDays(10);
        var result = await CreateRentalService(context).CreateAsync(new CreateRentalDto
        {
            Renter = new RenterInlineDto { Name = "Tenant", Phone = "13800138000" },
            ItemIds = new List<string> { item.Id.ToString() },
            StartDate = startDate,
            ExpectedEndDate = startDate.AddDays(3),
            TotalPrice = 100m
        }, "TestUser");

        Assert.Null(result.Error);
        Assert.NotNull(result.Rental);
        Assert.Equal(startDate.AddDays(-3), result.Rental!.ExpectedShipDate);
    }

    [Fact]
    public async Task ListAsync_MineScopeIncludesCreatedOrAssignedRentalsOnly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var today = BusinessToday();
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        Rental BuildRental(string number, string createdBy, string? assignedTo, string? shippingAddress = null) => new()
        {
            Id = Guid.NewGuid(),
            RentalNumber = number,
            Renter = renter,
            Status = RentalStatus.Pending,
            StartDate = today.AddDays(1),
            ExpectedShipDate = today,
            ExpectedEndDate = today.AddDays(3),
            CreatedBy = createdBy,
            AssignedTo = assignedTo,
            ShippingAddress = shippingAddress
        };

        context.Rentals.AddRange(
            BuildRental("R20990701-MINE-1", "Alice", null, "张三 13800138000 广东省深圳市南山区科技园1号"),
            BuildRental("R20990701-MINE-2", "Bob", "Bob,Alice"),
            BuildRental("R20990701-MINE-3", "Bob", "Alice2"));
        context.Users.AddRange(
            new User { Name = "Alice", Status = UserStatus.Active },
            new User { Name = "Bob", Status = UserStatus.Active },
            new User { Name = "Charlie", Status = UserStatus.Active },
            new User { Name = "Alice2", Status = UserStatus.Left });
        await context.SaveChangesAsync();

        var (items, total) = await CreateRentalService(context).ListAsync(
            new RentalQueryParameters { OwnerScope = "mine", Page = 1, PageSize = 50 },
            "Alice");

        Assert.Equal(2, total);
        Assert.Equal(
            new[] { "R20990701-MINE-1", "R20990701-MINE-2" },
            items.Select(item => item.RentalNumber).OrderBy(number => number));

        var aliceOwned = await CreateRentalService(context).ListAsync(
            new RentalQueryParameters { OwnerName = "Alice", Page = 1, PageSize = 50 });
        Assert.Equal(
            new[] { "R20990701-MINE-1", "R20990701-MINE-2" },
            aliceOwned.items.Select(item => item.RentalNumber).OrderBy(number => number));
        Assert.Equal("广东省", aliceOwned.items.Single(item => item.RentalNumber == "R20990701-MINE-1").ParsedShippingAddress.Province);
        Assert.Equal("深圳市", aliceOwned.items.Single(item => item.RentalNumber == "R20990701-MINE-1").ParsedShippingAddress.City);

        var formerEmployeeOwned = await CreateRentalService(context).ListAsync(
            new RentalQueryParameters { OwnerName = "Alice2", Page = 1, PageSize = 50 });
        Assert.Equal(0, formerEmployeeOwned.total);

        var ownerOptions = await CreateRentalService(context).GetOwnerOptionsAsync();
        Assert.Equal(new[] { "Alice", "Bob", "Charlie" }, ownerOptions.Employees);
    }

    [Fact]
    public async Task ReturnAsync_DamagedItemWithRepairOccupancy_BecomesNormalLoanUntilSelectedDate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var today = BusinessToday();
        var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
        var category = new Category { Name = "Camera", Description = "Camera category" };
        var definition = new ItemDefinition { Name = "Camera Body", Category = category, Unit = "pcs", Description = "Body" };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-REPAIR-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.LoanedOut,
            CurrentDestination = "租赁 R20990101-0001"
        };
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20990101-0001",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" },
            Status = RentalStatus.Active,
            StartDate = today.AddDays(-3),
            ExpectedShipDate = today.AddDays(-4),
            ExpectedEndDate = today.AddDays(2)
        };
        rental.Items.Add(new RentalItem
        {
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name
        });
        rental.Shipments.Add(new RentalShipment
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            ShippedAt = today.AddDays(-4)
        });
        context.Rentals.Add(rental);
        await context.SaveChangesAsync();

        var service = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(),
            new StubSettlementService());

        var repairUntil = today.AddDays(5);
        var (result, error) = await service.ReturnAsync(rental.Id, new ReturnRentalDto
        {
            Condition = ReturnCondition.MajorDamage,
            RepairOccupancy = true,
            RepairExpectedReturnDate = repairUntil,
            Notes = "Lens mount needs repair"
        }, "TestUser");

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(RentalStatus.Returned, result!.Status);
        var returnedItem = Assert.Single(result.Items);
        Assert.Equal(ReturnCondition.MajorDamage, returnedItem.ReturnCondition);
        Assert.NotNull(returnedItem.ReturnedAt);

        var savedItem = await context.Items.SingleAsync(i => i.Id == item.Id);
        Assert.Equal(ItemStatus.LoanedOut, savedItem.Status);
        Assert.Equal("损坏维修", savedItem.CurrentDestination);
        Assert.Equal(repairUntil, savedItem.ExpectedReturnDate);
        Assert.Contains(context.AuditLogs, log => log.ItemId == item.Id
            && log.Action == AuditAction.Outbound
            && log.Destination == "损坏维修");
    }

    [Fact]
    public async Task ReturnAsync_AcceptsPerItemConditions_AndOnlyOccupiesDamagedItemsForRepair()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var today = BusinessToday();
        var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
        var category = new Category { Name = "Camera", Description = "Camera category" };
        var definition = new ItemDefinition { Name = "Camera Body", Category = category, Unit = "pcs", Description = "Body" };
        var damagedItem = new Item { Id = Guid.NewGuid(), ShortId = "CAM-DAMAGE-001", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.LoanedOut };
        var goodItem = new Item { Id = Guid.NewGuid(), ShortId = "CAM-GOOD-001", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.LoanedOut };
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20990101-0002",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" },
            Status = RentalStatus.Active,
            StartDate = today.AddDays(-3),
            ExpectedShipDate = today.AddDays(-4),
            ExpectedEndDate = today.AddDays(2)
        };
        var damagedRentalItem = new RentalItem { Item = damagedItem, ItemShortIdSnapshot = damagedItem.ShortId, ItemNameSnapshot = definition.Name };
        var goodRentalItem = new RentalItem { Item = goodItem, ItemShortIdSnapshot = goodItem.ShortId, ItemNameSnapshot = definition.Name };
        rental.Items.Add(damagedRentalItem);
        rental.Items.Add(goodRentalItem);
        rental.Shipments.Add(new RentalShipment
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            ShippedAt = today.AddDays(-4)
        });
        context.Rentals.Add(rental);
        await context.SaveChangesAsync();

        var service = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(),
            new StubSettlementService());

        var repairUntil = today.AddDays(5);
        var (result, error) = await service.ReturnAsync(rental.Id, new ReturnRentalDto
        {
            Items = new List<ReturnRentalItemDto>
            {
                new() { RentalItemId = damagedRentalItem.Id, Condition = ReturnCondition.MinorDamage },
                new() { RentalItemId = goodRentalItem.Id, Condition = ReturnCondition.Good }
            },
            RepairOccupancy = true,
            RepairExpectedReturnDate = repairUntil
        }, "TestUser");

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(ReturnCondition.MinorDamage, result!.Items.Single(item => item.ItemId == damagedItem.Id).ReturnCondition);
        Assert.Equal(ReturnCondition.Good, result.Items.Single(item => item.ItemId == goodItem.Id).ReturnCondition);

        var savedDamagedItem = await context.Items.SingleAsync(item => item.Id == damagedItem.Id);
        Assert.Equal(ItemStatus.LoanedOut, savedDamagedItem.Status);
        Assert.Equal("损坏维修", savedDamagedItem.CurrentDestination);
        Assert.Equal(repairUntil, savedDamagedItem.ExpectedReturnDate);

        var savedGoodItem = await context.Items.SingleAsync(item => item.Id == goodItem.Id);
        Assert.Equal(ItemStatus.InStock, savedGoodItem.Status);
        Assert.Null(savedGoodItem.CurrentDestination);
        Assert.Null(savedGoodItem.ExpectedReturnDate);
    }

    [Fact]
    public async Task ReturnAsync_PartiallyReturnsSelectedItems_AndKeepsOutstandingReminderOpen()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var today = BusinessToday();
        var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
        var category = new Category { Name = "Camera", Description = "Camera category" };
        var definition = new ItemDefinition { Name = "Camera Body", Category = category, Unit = "pcs", Description = "Body" };
        var firstItem = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-PARTIAL-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.LoanedOut,
            CurrentDestination = "租赁 R20990101-0003"
        };
        var secondItem = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-PARTIAL-002",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.LoanedOut,
            CurrentDestination = "租赁 R20990101-0003"
        };
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20990101-0003",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" },
            Status = RentalStatus.Active,
            StartDate = today.AddDays(-5),
            ExpectedShipDate = today.AddDays(-6),
            ExpectedEndDate = today.AddDays(-2),
            CreatedBy = "Creator"
        };
        var firstRentalItem = new RentalItem
        {
            Item = firstItem,
            ItemShortIdSnapshot = firstItem.ShortId,
            ItemNameSnapshot = definition.Name
        };
        var secondRentalItem = new RentalItem
        {
            Item = secondItem,
            ItemShortIdSnapshot = secondItem.ShortId,
            ItemNameSnapshot = definition.Name
        };
        rental.Items.Add(firstRentalItem);
        rental.Items.Add(secondRentalItem);
        rental.Shipments.Add(new RentalShipment
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            ShippedAt = today.AddDays(-6)
        });
        var overdueReminder = new Reminder
        {
            Type = ReminderType.RentalOverdue,
            Level = ReminderLevel.Critical,
            RelatedEntityType = "Rental",
            RelatedEntityId = rental.Id.ToString(),
            Title = "租赁逾期",
            Message = "请跟进",
            DueAt = today.AddDays(-1),
            CreatedAt = today.AddDays(-1)
        };
        context.AddRange(rental, overdueReminder);
        await context.SaveChangesAsync();

        var result = await CreateRentalService(context).ReturnAsync(rental.Id, new ReturnRentalDto
        {
            Items = new List<ReturnRentalItemDto>
            {
                new() { RentalItemId = firstRentalItem.Id, Condition = ReturnCondition.Good }
            }
        }, "TestUser");

        Assert.Null(result.error);
        Assert.NotNull(result.rental);
        Assert.Equal(RentalStatus.Overdue, result.rental!.Status);
        Assert.Null(result.rental.ActualEndDate);
        Assert.NotNull(result.rental.Items.Single(item => item.Id == firstRentalItem.Id).ReturnedAt);
        Assert.Null(result.rental.Items.Single(item => item.Id == secondRentalItem.Id).ReturnedAt);

        var savedFirstItem = await context.Items.SingleAsync(item => item.Id == firstItem.Id);
        Assert.Equal(ItemStatus.InStock, savedFirstItem.Status);
        Assert.Null(savedFirstItem.CurrentDestination);

        var savedSecondItem = await context.Items.SingleAsync(item => item.Id == secondItem.Id);
        Assert.Equal(ItemStatus.LoanedOut, savedSecondItem.Status);
        Assert.Equal("租赁 R20990101-0003", savedSecondItem.CurrentDestination);

        var savedReminder = await context.Reminders.SingleAsync(reminder => reminder.Id == overdueReminder.Id);
        Assert.Null(savedReminder.DismissedAt);
        Assert.Contains(
            await context.Reminders.Where(reminder => reminder.Type == ReminderType.Manual).ToListAsync(),
            reminder => reminder.Message?.Contains("部分归还", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task InboundShipment_CanBeLinkedToSelectedRentalItems_AndLegacyShipmentRemainsUnassigned()
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
        var item1 = new Item { Id = Guid.NewGuid(), ShortId = "CAM-001", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.LoanedOut };
        var item2 = new Item { Id = Guid.NewGuid(), ShortId = "CAM-002", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.LoanedOut };
        var rentalId = Guid.NewGuid();
        var rental = new Rental
        {
            Id = rentalId,
            RentalNumber = "R20260720-0001",
            Status = RentalStatus.Active,
            StartDate = new DateTime(2026, 7, 20),
            ExpectedShipDate = new DateTime(2026, 7, 20),
            ExpectedEndDate = new DateTime(2026, 7, 25),
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" }
        };
        var rentalItem1 = new RentalItem
        {
            Rental = rental,
            Item = item1,
            ItemShortIdSnapshot = item1.ShortId,
            ItemNameSnapshot = definition.Name
        };
        var rentalItem2 = new RentalItem
        {
            Rental = rental,
            Item = item2,
            ItemShortIdSnapshot = item2.ShortId,
            ItemNameSnapshot = definition.Name
        };
        rental.Items.Add(rentalItem1);
        rental.Items.Add(rentalItem2);
        rental.Shipments.Add(new RentalShipment
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "Carrier",
            ShippedAt = new DateTime(2026, 7, 20),
            DeliveredAt = new DateTime(2026, 7, 21)
        });
        context.Rentals.Add(rental);
        await context.SaveChangesAsync();

        var rentalService = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(),
            new StubSettlementService());

        var result = await rentalService.AddShipmentAsync(rentalId, new CreateShipmentDto
        {
            Direction = ShipmentDirection.Inbound,
            OriginWarehouseId = warehouse.Id,
            Carrier = "Carrier",
            ItemSelections = new List<RentalItemShipSelectionDto>
            {
                new() { RentalItemId = rentalItem1.Id, ItemId = item1.Id }
            }
        }, "TestUser");

        Assert.Null(result.Error);
        var savedShipment = await context.RentalShipments
            .Include(shipment => shipment.RentalItems)
            .SingleAsync(shipment => shipment.Direction == ShipmentDirection.Inbound);
        Assert.Single(savedShipment.RentalItems);
        Assert.Equal(rentalItem1.Id, savedShipment.RentalItems.Single().RentalItemId);
        Assert.Single(result.Rental!.Shipments.Single(shipment => shipment.Direction == ShipmentDirection.Inbound).Items);

        context.RentalShipments.Add(new RentalShipment
        {
            RentalId = rentalId,
            Direction = ShipmentDirection.Inbound,
            OriginWarehouseId = warehouse.Id,
            Carrier = "Legacy Carrier",
            ShippedAt = new DateTime(2026, 7, 22)
        });
        await context.SaveChangesAsync();

        var legacyRental = await rentalService.GetByIdAsync(rentalId);
        Assert.NotNull(legacyRental);
        Assert.Empty(legacyRental!.Shipments.Single(shipment => shipment.Carrier == "Legacy Carrier").Items);
    }

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
    public async Task UpdateRentalItemsAsync_ReleasesRemovedShippedItemFromOccupancyAfterRemovalDate()
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
        var today = BusinessToday();
        var rentalId = Guid.NewGuid();
        var rentalNumber = "R20990610-0001";
        var removedItem = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.LoanedOut,
            CurrentDestination = $"租赁 {rentalNumber}"
        };
        var replacementItem = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-002",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.InStock
        };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };

        context.Items.AddRange(removedItem, replacementItem);
        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = rentalNumber,
            Renter = renter,
            Status = RentalStatus.Active,
            StartDate = today.AddDays(-5),
            ExpectedShipDate = today.AddDays(-6),
            ExpectedEndDate = today.AddDays(5)
        });
        context.RentalItems.Add(new RentalItem
        {
            RentalId = rentalId,
            ItemId = removedItem.Id,
            ItemShortIdSnapshot = removedItem.ShortId,
            ItemNameSnapshot = definition.Name
        });
        context.RentalShipments.Add(new RentalShipment
        {
            RentalId = rentalId,
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            ShippedAt = today.AddDays(-6).AddHours(10)
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
            ItemIds = new List<string> { replacementItem.Id.ToString() }
        }, "TestUser");

        Assert.Null(result.Error);
        Assert.NotNull(result.Rental);
        Assert.Equal(ItemStatus.InStock, removedItem.Status);

        var removedRentalItem = await context.RentalItems.SingleAsync(ri => ri.ItemId == removedItem.Id);
        Assert.NotNull(removedRentalItem.ReturnedAt);

        var tomorrow = today.AddDays(1);
        var itemController = new ItemsController(context, new StubWebHostEnvironment());
        var itemAction = await itemController.GetAvailability(removedItem.Id, tomorrow, tomorrow);
        var itemOk = Assert.IsType<OkObjectResult>(itemAction.Result);
        var itemCalendar = Assert.IsType<ItemAvailabilityCalendarDto>(itemOk.Value);
        Assert.Empty(itemCalendar.BusyPeriods);

        var definitionController = new ItemDefinitionsController(context);
        var definitionAction = await definitionController.GetOccupancyCalendar(definition.Id, tomorrow, tomorrow);
        var definitionOk = Assert.IsType<OkObjectResult>(definitionAction.Result);
        var definitionCalendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(definitionOk.Value);
        Assert.Equal(1, Assert.Single(definitionCalendar.DailyStocks).OccupiedCount);
    }

    [Fact]
    public async Task Calendar_ReleasesLegacyRemovedRentalItemWithoutReleaseTimestamp()
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
        var today = BusinessToday();
        var rentalId = Guid.NewGuid();
        var rentalNumber = "R20990610-0002";
        var legacyRemovedItem = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.InStock
        };
        var activeItem = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-002",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.LoanedOut
        };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };

        context.Items.AddRange(legacyRemovedItem, activeItem);
        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = rentalNumber,
            Renter = renter,
            Status = RentalStatus.Active,
            StartDate = today.AddDays(-5),
            ExpectedShipDate = today.AddDays(-6),
            ExpectedEndDate = today.AddDays(5)
        });
        context.RentalItems.AddRange(
            new RentalItem
            {
                RentalId = rentalId,
                ItemId = legacyRemovedItem.Id,
                ItemShortIdSnapshot = legacyRemovedItem.ShortId,
                ItemNameSnapshot = definition.Name,
                ReturnedAt = today.AddHours(10),
                ReturnCondition = ReturnCondition.Good,
                ReturnNotes = "Removed from rental item list."
            },
            new RentalItem
            {
                RentalId = rentalId,
                ItemId = activeItem.Id,
                ItemShortIdSnapshot = activeItem.ShortId,
                ItemNameSnapshot = definition.Name
            });
        context.RentalShipments.Add(new RentalShipment
        {
            RentalId = rentalId,
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            ShippedAt = today.AddDays(-6).AddHours(10)
        });
        await context.SaveChangesAsync();

        var tomorrow = today.AddDays(1);
        var itemController = new ItemsController(context, new StubWebHostEnvironment());
        var itemAction = await itemController.GetAvailability(legacyRemovedItem.Id, tomorrow, tomorrow);
        var itemOk = Assert.IsType<OkObjectResult>(itemAction.Result);
        var itemCalendar = Assert.IsType<ItemAvailabilityCalendarDto>(itemOk.Value);
        Assert.Empty(itemCalendar.BusyPeriods);

        var definitionController = new ItemDefinitionsController(context);
        var definitionAction = await definitionController.GetOccupancyCalendar(definition.Id, tomorrow, tomorrow);
        var definitionOk = Assert.IsType<OkObjectResult>(definitionAction.Result);
        var definitionCalendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(definitionOk.Value);
        Assert.Equal(1, Assert.Single(definitionCalendar.DailyStocks).OccupiedCount);
    }

    [Fact]
    public async Task Calendars_ExcludeSuspectedMissingItemFromRentalOccupancy()
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
        var missingItem = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.SuspectedMissing
        };
        var availableItem = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-002",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.InStock
        };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var today = BusinessToday();
        var rentalId = Guid.NewGuid();

        context.Items.AddRange(missingItem, availableItem);
        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = "R20260610-0003",
            Renter = renter,
            Status = RentalStatus.Active,
            StartDate = today.AddDays(-2),
            ExpectedShipDate = today.AddDays(-3),
            ExpectedEndDate = today.AddDays(2)
        });
        context.RentalItems.Add(new RentalItem
        {
            RentalId = rentalId,
            ItemId = missingItem.Id,
            ItemShortIdSnapshot = missingItem.ShortId,
            ItemNameSnapshot = definition.Name
        });
        context.RentalShipments.Add(new RentalShipment
        {
            RentalId = rentalId,
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            ShippedAt = today.AddDays(-3).AddHours(10)
        });
        await context.SaveChangesAsync();

        var itemController = new ItemsController(context, new StubWebHostEnvironment());
        var itemAction = await itemController.GetAvailability(missingItem.Id, today, today);
        var itemOk = Assert.IsType<OkObjectResult>(itemAction.Result);
        var itemCalendar = Assert.IsType<ItemAvailabilityCalendarDto>(itemOk.Value);
        Assert.Empty(itemCalendar.BusyPeriods);
        Assert.Empty(itemCalendar.FreePeriods);

        var definitionController = new ItemDefinitionsController(context);
        var definitionAction = await definitionController.GetOccupancyCalendar(definition.Id, today, today);
        var definitionOk = Assert.IsType<OkObjectResult>(definitionAction.Result);
        var definitionCalendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(definitionOk.Value);
        var stock = Assert.Single(definitionCalendar.DailyStocks);
        Assert.Equal(1, definitionCalendar.TotalStock);
        Assert.Equal(1, stock.TotalStock);
        Assert.Equal(0, stock.OccupiedCount);
        Assert.Equal(1, stock.RemainingStock);
        Assert.Empty(stock.Details);
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
    public async Task DefinitionOccupancy_BeginsOnExpectedShipmentDateBeforeActualShipment()
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
        var item = new Item { Id = Guid.NewGuid(), ShortId = "CAM-001", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.InStock };
        context.Items.Add(item);
        await context.SaveChangesAsync();

        var expectedShipDate = new DateTime(2099, 6, 19, 0, 0, 0, DateTimeKind.Utc);
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20990619-0001",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" },
            Status = RentalStatus.Pending,
            StartDate = expectedShipDate.AddDays(2),
            ExpectedShipDate = expectedShipDate,
            ExpectedEndDate = expectedShipDate.AddDays(7)
        };
        context.Rentals.Add(rental);
        context.RentalItems.Add(new RentalItem
        {
            RentalId = rental.Id,
            ItemId = item.Id,
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name
        });
        await context.SaveChangesAsync();

        var controller = new ItemDefinitionsController(context);
        var action = await controller.GetOccupancyCalendar(definition.Id, expectedShipDate, expectedShipDate);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(ok.Value);
        Assert.Equal(1, Assert.Single(calendar.DailyStocks).OccupiedCount);
    }

    [Fact]
    public async Task ItemAvailability_ShowsUncertainDefinitionOccupancyFromExpectedShipmentDate()
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
        var item = new Item { Id = Guid.NewGuid(), ShortId = "CAM-001", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.InStock };
        context.Items.Add(item);
        await context.SaveChangesAsync();

        var expectedShipDate = new DateTime(2099, 6, 19, 0, 0, 0, DateTimeKind.Utc);
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20990619-0001",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" },
            Status = RentalStatus.Pending,
            StartDate = expectedShipDate.AddDays(2),
            ExpectedShipDate = expectedShipDate,
            ExpectedEndDate = expectedShipDate.AddDays(7)
        };
        context.Rentals.Add(rental);
        context.RentalItems.Add(new RentalItem
        {
            RentalId = rental.Id,
            ItemId = null,
            ItemDefinitionId = definition.Id,
            ItemShortIdSnapshot = "Pending",
            ItemNameSnapshot = definition.Name
        });
        await context.SaveChangesAsync();

        var controller = new ItemsController(context, new StubWebHostEnvironment());
        var action = await controller.GetAvailability(item.Id, expectedShipDate, expectedShipDate);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemAvailabilityCalendarDto>(ok.Value);
        var busy = Assert.Single(calendar.BusyPeriods);
        Assert.True(busy.IsUncertain);
        Assert.Equal(expectedShipDate, busy.StartAt);
    }

    [Fact]
    public async Task DefinitionOccupancy_OpenManualLoanOccupiesThroughToday()
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
        var today = BusinessToday();
        var loanDate = today.AddDays(-2);

        context.Items.Add(item);
        context.AuditLogs.Add(new AuditLog
        {
            Timestamp = loanDate.AddHours(10),
            Action = AuditAction.Outbound,
            Item = item,
            ItemShortId = item.ShortId,
            ItemName = definition.Name,
            Warehouse = warehouse,
            WarehouseName = warehouse.Name,
            User = "TestUser",
            Destination = "Manual borrower"
        });
        await context.SaveChangesAsync();

        var controller = new ItemDefinitionsController(context);
        var action = await controller.GetOccupancyCalendar(
            definition.Id,
            loanDate.AddDays(-1),
            today.AddDays(1));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(ok.Value);
        var beforeLoan = calendar.DailyStocks.Single(day => day.Date == loanDate.AddDays(-1));
        var loanDay = calendar.DailyStocks.Single(day => day.Date == loanDate);
        var todayStock = calendar.DailyStocks.Single(day => day.Date == today);
        var tomorrow = calendar.DailyStocks.Single(day => day.Date == today.AddDays(1));

        Assert.Equal(0, beforeLoan.OccupiedCount);
        Assert.Equal(1, loanDay.OccupiedCount);
        Assert.Equal(1, todayStock.OccupiedCount);
        Assert.Equal(0, tomorrow.OccupiedCount);
        var manualLoan = Assert.Single(loanDay.Details);
        Assert.Equal(Guid.Empty, manualLoan.RentalId);
        Assert.Equal("普通借出 (CAM-001)", manualLoan.RentalNumber);

        item.Status = ItemStatus.InStock;
        await context.SaveChangesAsync();

        var returnedAction = await controller.GetOccupancyCalendar(definition.Id, loanDate, loanDate);
        var returnedOk = Assert.IsType<OkObjectResult>(returnedAction.Result);
        var returnedCalendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(returnedOk.Value);
        Assert.Equal(0, Assert.Single(returnedCalendar.DailyStocks).OccupiedCount);
    }

    [Fact]
    public async Task ItemAvailability_OpenManualLoanOccupiesThroughToday()
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
        var today = BusinessToday();
        var loanDate = today.AddDays(-2);

        context.Items.Add(item);
        context.AuditLogs.Add(new AuditLog
        {
            Timestamp = loanDate.AddHours(10),
            Action = AuditAction.Outbound,
            Item = item,
            ItemShortId = item.ShortId,
            ItemName = definition.Name,
            Warehouse = warehouse,
            WarehouseName = warehouse.Name,
            User = "TestUser",
            Destination = "Manual borrower"
        });
        await context.SaveChangesAsync();

        var controller = new ItemsController(context, new StubWebHostEnvironment());
        var action = await controller.GetAvailability(item.Id, loanDate, today.AddDays(2));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemAvailabilityCalendarDto>(ok.Value);
        var manualLoan = Assert.Single(calendar.BusyPeriods);

        Assert.Equal(Guid.Empty, manualLoan.RentalId);
        Assert.Equal("普通借出", manualLoan.RentalNumber);
        Assert.Equal(loanDate, manualLoan.StartAt);
        Assert.Equal(today, manualLoan.EndAt);
        Assert.True(manualLoan.IsOpen);

        item.Status = ItemStatus.InStock;
        await context.SaveChangesAsync();

        var returnedAction = await controller.GetAvailability(item.Id, loanDate, loanDate);
        var returnedOk = Assert.IsType<OkObjectResult>(returnedAction.Result);
        var returnedCalendar = Assert.IsType<ItemAvailabilityCalendarDto>(returnedOk.Value);
        Assert.Empty(returnedCalendar.BusyPeriods);
    }

    [Fact]
    public async Task Calendars_OverdueManualLoanOccupiesThroughTodayButNotTomorrow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var today = BusinessToday();
        var expectedReturnDate = today.AddDays(-1);
        var loanDate = today.AddDays(-3);
        var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
        var category = new Category { Name = "Camera", Description = "Camera category" };
        var definition = new ItemDefinition { Name = "Camera Body", Category = category, Unit = "pcs", Description = "Body" };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-OVERDUE-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.LoanedOut,
            CurrentDestination = "Manual borrower",
            ExpectedReturnDate = expectedReturnDate
        };

        context.Items.Add(item);
        context.AuditLogs.Add(new AuditLog
        {
            Timestamp = loanDate.AddHours(10),
            Action = AuditAction.Outbound,
            Item = item,
            ItemShortId = item.ShortId,
            ItemName = definition.Name,
            Warehouse = warehouse,
            WarehouseName = warehouse.Name,
            User = "LoanOperator",
            Destination = item.CurrentDestination
        });
        await context.SaveChangesAsync();

        var itemController = new ItemsController(context, new StubWebHostEnvironment());
        var itemAction = await itemController.GetAvailability(item.Id, loanDate, today.AddDays(1));
        var itemOk = Assert.IsType<OkObjectResult>(itemAction.Result);
        var itemCalendar = Assert.IsType<ItemAvailabilityCalendarDto>(itemOk.Value);
        var manualLoan = Assert.Single(itemCalendar.BusyPeriods);
        Assert.Equal(today, manualLoan.EndAt);
        Assert.True(manualLoan.IsOpen);

        var definitionController = new ItemDefinitionsController(context);
        var definitionAction = await definitionController.GetOccupancyCalendar(
            definition.Id,
            expectedReturnDate,
            today.AddDays(1));
        var definitionOk = Assert.IsType<OkObjectResult>(definitionAction.Result);
        var definitionCalendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(definitionOk.Value);

        Assert.Equal(1, definitionCalendar.DailyStocks.Single(day => day.Date == expectedReturnDate).OccupiedCount);
        Assert.Equal(1, definitionCalendar.DailyStocks.Single(day => day.Date == today).OccupiedCount);
        Assert.Equal(0, definitionCalendar.DailyStocks.Single(day => day.Date == today.AddDays(1)).OccupiedCount);
    }

    [Fact]
    public async Task ManualLoanReminder_TargetsLatestOutboundOperatorAndDeduplicatesByExpectedDate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        var today = BusinessToday();
        var expectedReturnDate = today.AddDays(-1);
        var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
        var category = new Category { Name = "Camera", Description = "Camera category" };
        var definition = new ItemDefinition { Name = "Camera Body", Category = category, Unit = "pcs", Description = "Body" };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-REMINDER-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.LoanedOut,
            CurrentDestination = "Manual borrower",
            ExpectedReturnDate = expectedReturnDate
        };

        context.Items.Add(item);
        context.AuditLogs.AddRange(
            new AuditLog
            {
                Timestamp = now.AddDays(-5),
                Action = AuditAction.Outbound,
                Item = item,
                ItemShortId = item.ShortId,
                ItemName = definition.Name,
                Warehouse = warehouse,
                WarehouseName = warehouse.Name,
                User = "OldOperator",
                Destination = item.CurrentDestination
            },
            new AuditLog
            {
                Timestamp = now.AddDays(-3),
                Action = AuditAction.Outbound,
                Item = item,
                ItemShortId = item.ShortId,
                ItemName = definition.Name,
                Warehouse = warehouse,
                WarehouseName = warehouse.Name,
                User = "LatestOperator",
                Destination = item.CurrentDestination
            });
        await context.SaveChangesAsync();

        var created = await ManualLoanReminderService.BuildOverdueRemindersAsync(context, now);
        var reminder = Assert.Single(created);
        Assert.Equal("LatestOperator", reminder.TargetUser);
        Assert.Equal(ManualLoanReminderService.RelatedEntityType, reminder.RelatedEntityType);
        Assert.Equal(expectedReturnDate, reminder.DueAt);

        context.Reminders.Add(reminder);
        await context.SaveChangesAsync();

        var duplicate = await ManualLoanReminderService.BuildOverdueRemindersAsync(context, now);
        Assert.Empty(duplicate);
    }

    [Fact]
    public async Task UpdateExpectedReturnDate_ReschedulesManualLoanAndDismissesOldReminder()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var today = BusinessToday();
        var newExpectedReturnDate = today.AddDays(3);
        var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
        var category = new Category { Name = "Camera", Description = "Camera category" };
        var definition = new ItemDefinition { Name = "Camera Body", Category = category, Unit = "pcs", Description = "Body" };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-RESET-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.LoanedOut,
            CurrentDestination = "Manual borrower",
            ExpectedReturnDate = today.AddDays(-1)
        };
        var reminder = new Reminder
        {
            Type = ReminderType.Manual,
            Level = ReminderLevel.Warning,
            RelatedEntityType = ManualLoanReminderService.RelatedEntityType,
            RelatedEntityId = item.Id.ToString(),
            TargetUser = "LoanOperator",
            Title = "Overdue",
            DueAt = today.AddDays(-1)
        };
        context.Items.Add(item);
        context.Reminders.Add(reminder);
        await context.SaveChangesAsync();

        var controller = new ItemsController(context, new StubWebHostEnvironment())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, "Scheduler") },
                        "Test"))
                }
            }
        };

        var action = await controller.UpdateExpectedReturnDate(
            item.Id,
            new UpdateExpectedReturnDateRequest { ExpectedReturnDate = newExpectedReturnDate });

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<ItemDto>(ok.Value);
        Assert.Equal(newExpectedReturnDate, dto.ExpectedReturnDate);

        var savedReminder = await context.Reminders.SingleAsync(saved => saved.Id == reminder.Id);
        Assert.NotNull(savedReminder.DismissedAt);
        Assert.Equal("Scheduler", savedReminder.DismissedBy);
        Assert.Contains(context.AuditLogs, log =>
            log.ItemId == item.Id
            && log.Action == AuditAction.ExpectedReturnUpdated
            && log.User == "Scheduler");
    }

    [Fact]
    public async Task Calendars_ShowManualLoanAfterHistoricalRentalEnds()
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
        var historicalStart = new DateTime(2099, 6, 16, 0, 0, 0, DateTimeKind.Utc);
        var returnedAt = historicalStart.AddDays(4).AddHours(10);
        var manualLoanStart = historicalStart.AddDays(6);
        var expectedManualReturn = manualLoanStart.AddDays(8);
        var item = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.LoanedOut,
            CurrentDestination = "Manual borrower",
            ExpectedReturnDate = expectedManualReturn
        };
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20990616-0001",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Previous tenant", Phone = "13800138000" },
            Status = RentalStatus.Returned,
            StartDate = historicalStart,
            ExpectedShipDate = historicalStart.AddDays(-3),
            ExpectedEndDate = historicalStart.AddDays(2),
            ActualEndDate = returnedAt
        };

        context.Rentals.Add(rental);
        context.Items.Add(item);
        context.RentalItems.Add(new RentalItem
        {
            RentalId = rental.Id,
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name,
            ReturnedAt = returnedAt,
            ReturnCondition = ReturnCondition.Good
        });
        context.AuditLogs.Add(new AuditLog
        {
            Timestamp = manualLoanStart.AddHours(10),
            Action = AuditAction.Outbound,
            Item = item,
            ItemShortId = item.ShortId,
            ItemName = definition.Name,
            Warehouse = warehouse,
            WarehouseName = warehouse.Name,
            User = "TestUser",
            Destination = item.CurrentDestination
        });
        await context.SaveChangesAsync();

        var definitionController = new ItemDefinitionsController(context);
        var definitionAction = await definitionController.GetOccupancyCalendar(
            definition.Id,
            historicalStart.AddDays(-3),
            expectedManualReturn);

        var definitionOk = Assert.IsType<OkObjectResult>(definitionAction.Result);
        var definitionCalendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(definitionOk.Value);
        var manualLoanDay = definitionCalendar.DailyStocks.Single(day => day.Date == manualLoanStart);
        Assert.Equal(1, manualLoanDay.OccupiedCount);
        Assert.Contains(manualLoanDay.Details, detail => detail.IsManualLoan && detail.RentalId == Guid.Empty);

        var itemController = new ItemsController(context, new StubWebHostEnvironment());
        var itemAction = await itemController.GetAvailability(
            item.Id,
            historicalStart.AddDays(-3),
            expectedManualReturn);

        var itemOk = Assert.IsType<OkObjectResult>(itemAction.Result);
        var itemCalendar = Assert.IsType<ItemAvailabilityCalendarDto>(itemOk.Value);
        Assert.Contains(itemCalendar.BusyPeriods, period =>
            period.IsManualLoan
            && period.StartAt == manualLoanStart
            && period.EndAt == expectedManualReturn);
    }

    [Fact]
    public async Task DefinitionOccupancy_DoesNotTreatRentalLoanedOutItemAsManualLoan()
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
            Status = ItemStatus.LoanedOut,
            CurrentDestination = "租赁 R20990610-0001"
        };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var rentalId = Guid.NewGuid();
        var shipDate = new DateTime(2099, 6, 10, 0, 0, 0, DateTimeKind.Utc);

        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = "R20990610-0001",
            Renter = renter,
            Status = RentalStatus.Active,
            StartDate = shipDate,
            ExpectedShipDate = shipDate,
            ExpectedEndDate = shipDate.AddDays(3)
        });
        context.RentalItems.Add(new RentalItem
        {
            RentalId = rentalId,
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
            ShippedAt = shipDate.AddHours(9)
        });
        await context.SaveChangesAsync();

        var controller = new ItemDefinitionsController(context);
        var action = await controller.GetOccupancyCalendar(definition.Id, shipDate, shipDate);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(ok.Value);
        var day = Assert.Single(calendar.DailyStocks);

        Assert.Equal(1, day.OccupiedCount);
        var detail = Assert.Single(day.Details);
        Assert.False(detail.IsManualLoan);
        Assert.Equal(rentalId, detail.RentalId);
    }

    [Fact]
    public async Task ItemAvailability_ShowsManualLoanEvenWhenFutureRentalIsReserved()
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
            Status = ItemStatus.LoanedOut,
            CurrentDestination = "Manual borrower"
        };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Future Tenant", Phone = "13900139000" };
        var rentalId = Guid.NewGuid();
        var loanDate = BusinessToday().AddDays(-2);
        var futureShipDate = loanDate.AddDays(10);

        context.Items.Add(item);
        context.AuditLogs.Add(new AuditLog
        {
            Timestamp = loanDate.AddHours(10),
            Action = AuditAction.Outbound,
            Item = item,
            ItemShortId = item.ShortId,
            ItemName = definition.Name,
            Warehouse = warehouse,
            WarehouseName = warehouse.Name,
            User = "TestUser",
            Destination = "Manual borrower"
        });
        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = "R20990620-0001",
            Renter = renter,
            Status = RentalStatus.Pending,
            StartDate = futureShipDate,
            ExpectedShipDate = futureShipDate,
            ExpectedEndDate = futureShipDate.AddDays(3)
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

        var controller = new ItemsController(context, new StubWebHostEnvironment());
        var action = await controller.GetAvailability(item.Id, loanDate, loanDate.AddDays(1));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemAvailabilityCalendarDto>(ok.Value);
        var manualLoan = Assert.Single(calendar.BusyPeriods);

        Assert.True(manualLoan.IsManualLoan);
        Assert.Equal(Guid.Empty, manualLoan.RentalId);
    }

    [Fact]
    public async Task CreateAsync_ConflictsWithOpenManualLoanForItemDefinition()
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
        var loanDate = BusinessToday().AddDays(-2);

        context.Items.Add(item);
        context.AuditLogs.Add(new AuditLog
        {
            Timestamp = loanDate.AddHours(10),
            Action = AuditAction.Outbound,
            Item = item,
            ItemShortId = item.ShortId,
            ItemName = definition.Name,
            Warehouse = warehouse,
            WarehouseName = warehouse.Name,
            User = "TestUser",
            Destination = "Manual borrower"
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
            StartDate = loanDate,
            ExpectedShipDate = loanDate,
            ExpectedEndDate = loanDate.AddDays(1)
        }, "TestUser");

        Assert.NotNull(result.Conflict);

        var futureResult = await rentalService.CreateAsync(new CreateRentalDto
        {
            Renter = new RenterInlineDto { Name = "Future Tenant", Phone = "13700137000" },
            ItemDefinitionIds = new List<int> { definition.Id },
            StartDate = BusinessToday().AddDays(1),
            ExpectedShipDate = BusinessToday().AddDays(1),
            ExpectedEndDate = BusinessToday().AddDays(2)
        }, "TestUser");

        Assert.Null(futureResult.Conflict);
        Assert.NotNull(futureResult.Rental);
    }

    [Fact]
    public async Task CreateAsync_ConflictsOnExpectedShipmentDateForSpecificItem()
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
        var item = new Item { Id = Guid.NewGuid(), ShortId = "CAM-001", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.InStock };
        var spareItem = new Item { Id = Guid.NewGuid(), ShortId = "CAM-002", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.InStock };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var occupiedDay = new DateTime(2099, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        var existingRentalId = Guid.NewGuid();

        context.Items.AddRange(item, spareItem);
        context.Rentals.Add(new Rental
        {
            Id = existingRentalId,
            RentalNumber = "R20990609-0001",
            Renter = renter,
            Status = RentalStatus.Pending,
            StartDate = occupiedDay.AddDays(-1),
            ExpectedShipDate = occupiedDay.AddDays(-1),
            ExpectedEndDate = occupiedDay
        });
        context.RentalItems.Add(new RentalItem
        {
            RentalId = existingRentalId,
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

        var result = await rentalService.CreateAsync(new CreateRentalDto
        {
            Renter = new RenterInlineDto { Name = "Next Tenant", Phone = "13900139000" },
            ItemIds = new List<string> { item.Id.ToString() },
            ExpectedShipDate = occupiedDay,
            StartDate = occupiedDay.AddDays(2),
            ExpectedEndDate = occupiedDay.AddDays(2)
        }, "TestUser");

        Assert.NotNull(result.Conflict);
        Assert.Contains(result.Conflict!.PendingShipmentConflicts, conflict => conflict.RentalId == existingRentalId);
    }

    [Fact]
    public async Task CreateAsync_ConflictsOnExpectedShipmentDateForItemDefinition()
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
        var item = new Item { Id = Guid.NewGuid(), ShortId = "CAM-001", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.InStock };
        var renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" };
        var occupiedDay = new DateTime(2099, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        var existingRentalId = Guid.NewGuid();

        context.Items.Add(item);
        context.Rentals.Add(new Rental
        {
            Id = existingRentalId,
            RentalNumber = "R20990609-0001",
            Renter = renter,
            Status = RentalStatus.Pending,
            StartDate = occupiedDay.AddDays(-1),
            ExpectedShipDate = occupiedDay.AddDays(-1),
            ExpectedEndDate = occupiedDay
        });
        context.RentalItems.Add(new RentalItem
        {
            RentalId = existingRentalId,
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

        var result = await rentalService.CreateAsync(new CreateRentalDto
        {
            Renter = new RenterInlineDto { Name = "Next Tenant", Phone = "13900139000" },
            ItemDefinitionIds = new List<int> { definition.Id },
            ExpectedShipDate = occupiedDay,
            StartDate = occupiedDay.AddDays(2),
            ExpectedEndDate = occupiedDay.AddDays(2)
        }, "TestUser");

        Assert.NotNull(result.Conflict);
        Assert.Contains(result.Conflict!.PendingShipmentConflicts, conflict => conflict.RentalId == existingRentalId);
    }

    [Fact]
    public async Task DefinitionOccupancy_ReturnedRentalOccupiesThroughActualReturnDate()
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
            new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(ok.Value);

        var beforeReturnDay = calendar.DailyStocks.Single(day => day.Date == new DateTime(2026, 6, 11));
        var returnDay = calendar.DailyStocks.Single(day => day.Date == new DateTime(2026, 6, 12));
        var expectedEndDay = calendar.DailyStocks.Single(day => day.Date == new DateTime(2026, 6, 15));
        var afterExpectedEndDay = calendar.DailyStocks.Single(day => day.Date == new DateTime(2026, 6, 16));

        Assert.Equal(1, beforeReturnDay.OccupiedCount);
        Assert.Equal(1, returnDay.OccupiedCount);
        Assert.Equal(ItemOccupancyStatus.Scheduled, Assert.Single(returnDay.Details).OccupancyStatus);
        Assert.Equal(0, expectedEndDay.OccupiedCount);
        Assert.Empty(expectedEndDay.Details);
        Assert.Equal(0, afterExpectedEndDay.OccupiedCount);
        Assert.Empty(afterExpectedEndDay.Details);
    }

    [Fact]
    public async Task DefinitionOccupancy_AddsReturnBufferForPendingRental()
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
        var expectedEndDate = new DateTime(2099, 6, 12, 0, 0, 0, DateTimeKind.Utc);
        var returnBufferDate = expectedEndDate.AddDays(2);

        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = "R20990610-0002",
            Renter = renter,
            Status = RentalStatus.Pending,
            StartDate = new DateTime(2099, 6, 10, 0, 0, 0, DateTimeKind.Utc),
            ExpectedShipDate = new DateTime(2099, 6, 9, 0, 0, 0, DateTimeKind.Utc),
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
        await context.SaveChangesAsync();

        var controller = new ItemDefinitionsController(context);
        var action = await controller.GetOccupancyCalendar(definition.Id, returnBufferDate, returnBufferDate);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(ok.Value);
        var day = Assert.Single(calendar.DailyStocks);

        Assert.Equal(returnBufferDate, day.Date);
        Assert.Equal(1, day.OccupiedCount);
        Assert.Equal(ItemOccupancyStatus.Returning, Assert.Single(day.Details).OccupancyStatus);
    }

    [Fact]
    public async Task DefinitionOccupancy_AddsReturnBufferOnlyToFinalRenewalRental()
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
        var sourceRentalId = Guid.NewGuid();
        var renewalRentalId = Guid.NewGuid();
        var sourceEndDate = new DateTime(2099, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        var renewalStartDate = sourceEndDate.AddDays(1);
        var renewalEndDate = new DateTime(2099, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var renewalBufferDate = renewalEndDate.AddDays(2);

        context.Rentals.AddRange(
            new Rental
            {
                Id = sourceRentalId,
                RentalNumber = "R20990601-0001",
                Renter = renter,
                Status = RentalStatus.Renewed,
                StartDate = new DateTime(2099, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpectedShipDate = new DateTime(2099, 5, 31, 0, 0, 0, DateTimeKind.Utc),
                ExpectedEndDate = sourceEndDate,
                ActualEndDate = sourceEndDate,
                RenewedToRentalId = renewalRentalId,
                RenewedToRentalNumber = "R20990601-0001-01"
            },
            new Rental
            {
                Id = renewalRentalId,
                RentalNumber = "R20990601-0001-01",
                Renter = renter,
                Status = RentalStatus.Active,
                StartDate = renewalStartDate,
                ExpectedShipDate = renewalStartDate,
                ExpectedEndDate = renewalEndDate,
                RenewedFromRentalId = sourceRentalId,
                RenewedFromRentalNumber = "R20990601-0001"
            });
        context.RentalItems.AddRange(
            new RentalItem
            {
                RentalId = sourceRentalId,
                ItemId = item.Id,
                Item = item,
                ItemShortIdSnapshot = item.ShortId,
                ItemNameSnapshot = definition.Name
            },
            new RentalItem
            {
                RentalId = renewalRentalId,
                ItemId = item.Id,
                Item = item,
                ItemShortIdSnapshot = item.ShortId,
                ItemNameSnapshot = definition.Name
            });
        await context.SaveChangesAsync();

        var controller = new ItemDefinitionsController(context);
        var action = await controller.GetOccupancyCalendar(
            definition.Id,
            renewalStartDate,
            renewalBufferDate);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(ok.Value);

        var renewalStart = calendar.DailyStocks.Single(day => day.Date == renewalStartDate);
        var renewalBuffer = calendar.DailyStocks.Single(day => day.Date == renewalBufferDate);

        Assert.Equal(1, renewalStart.OccupiedCount);
        Assert.Equal("R20990601-0001-01", Assert.Single(renewalStart.Details).RentalNumber);
        Assert.Equal(ItemOccupancyStatus.Scheduled, Assert.Single(renewalStart.Details).OccupancyStatus);
        Assert.Equal(1, renewalBuffer.OccupiedCount);
        Assert.Equal("R20990601-0001-01", Assert.Single(renewalBuffer.Details).RentalNumber);
        Assert.Equal(ItemOccupancyStatus.Returning, Assert.Single(renewalBuffer.Details).OccupancyStatus);
    }

    [Fact]
    public async Task ItemAvailability_ShowsFinalRenewalRentalOnReturnBufferDate()
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
        var sourceRentalId = Guid.NewGuid();
        var renewalRentalId = Guid.NewGuid();
        var sourceEndDate = new DateTime(2099, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        var renewalStartDate = sourceEndDate.AddDays(1);
        var renewalEndDate = new DateTime(2099, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var renewalBufferDate = renewalEndDate.AddDays(2);

        context.Rentals.AddRange(
            new Rental
            {
                Id = sourceRentalId,
                RentalNumber = "R20990601-0001",
                Renter = renter,
                Status = RentalStatus.Renewed,
                StartDate = new DateTime(2099, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpectedShipDate = new DateTime(2099, 5, 31, 0, 0, 0, DateTimeKind.Utc),
                ExpectedEndDate = sourceEndDate,
                ActualEndDate = sourceEndDate,
                RenewedToRentalId = renewalRentalId,
                RenewedToRentalNumber = "R20990601-0001-01"
            },
            new Rental
            {
                Id = renewalRentalId,
                RentalNumber = "R20990601-0001-01",
                Renter = renter,
                Status = RentalStatus.Active,
                StartDate = renewalStartDate,
                ExpectedShipDate = renewalStartDate,
                ExpectedEndDate = renewalEndDate,
                RenewedFromRentalId = sourceRentalId,
                RenewedFromRentalNumber = "R20990601-0001"
            });
        context.RentalItems.AddRange(
            new RentalItem
            {
                RentalId = sourceRentalId,
                ItemId = item.Id,
                Item = item,
                ItemShortIdSnapshot = item.ShortId,
                ItemNameSnapshot = definition.Name
            },
            new RentalItem
            {
                RentalId = renewalRentalId,
                ItemId = item.Id,
                Item = item,
                ItemShortIdSnapshot = item.ShortId,
                ItemNameSnapshot = definition.Name
            });
        await context.SaveChangesAsync();

        var controller = new ItemsController(context, new StubWebHostEnvironment());
        var action = await controller.GetAvailability(item.Id, renewalBufferDate, renewalBufferDate);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemAvailabilityCalendarDto>(ok.Value);
        var busy = Assert.Single(calendar.BusyPeriods);

        Assert.Equal("R20990601-0001-01", busy.RentalNumber);
        Assert.Equal(ItemOccupancyStatus.Returning, busy.OccupancyStatus);
    }

    [Fact]
    public async Task DefinitionOccupancy_ShowsFinalRenewalRentalAfterExpectedEndUntilToday()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var today = BusinessToday();
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
        var sourceRentalId = Guid.NewGuid();
        var renewalRentalId = Guid.NewGuid();
        var sourceEndDate = today.AddDays(-10);
        var renewalStartDate = sourceEndDate.AddDays(1);
        var renewalEndDate = today.AddDays(-3);

        context.Rentals.AddRange(
            new Rental
            {
                Id = sourceRentalId,
                RentalNumber = "R20260601-0001",
                Renter = renter,
                Status = RentalStatus.Renewed,
                StartDate = sourceEndDate.AddDays(-5),
                ExpectedShipDate = sourceEndDate.AddDays(-6),
                ExpectedEndDate = sourceEndDate,
                ActualEndDate = sourceEndDate,
                RenewedToRentalId = renewalRentalId,
                RenewedToRentalNumber = "R20260601-0001-01"
            },
            new Rental
            {
                Id = renewalRentalId,
                RentalNumber = "R20260601-0001-01",
                Renter = renter,
                Status = RentalStatus.Active,
                StartDate = renewalStartDate,
                ExpectedShipDate = renewalStartDate,
                ExpectedEndDate = renewalEndDate,
                RenewedFromRentalId = sourceRentalId,
                RenewedFromRentalNumber = "R20260601-0001"
            });
        context.RentalItems.AddRange(
            new RentalItem
            {
                RentalId = sourceRentalId,
                ItemId = item.Id,
                Item = item,
                ItemShortIdSnapshot = item.ShortId,
                ItemNameSnapshot = definition.Name
            },
            new RentalItem
            {
                RentalId = renewalRentalId,
                ItemId = item.Id,
                Item = item,
                ItemShortIdSnapshot = item.ShortId,
                ItemNameSnapshot = definition.Name
            });
        await context.SaveChangesAsync();

        var controller = new ItemDefinitionsController(context);
        var action = await controller.GetOccupancyCalendar(definition.Id, today, today);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(ok.Value);
        var day = Assert.Single(calendar.DailyStocks);
        var detail = Assert.Single(day.Details);

        Assert.Equal(1, day.OccupiedCount);
        Assert.Equal("R20260601-0001-01", detail.RentalNumber);
        Assert.Equal(ItemOccupancyStatus.Returning, detail.OccupancyStatus);
    }

    [Fact]
    public async Task DefinitionOccupancy_ShowsHistoricalRenewalWithoutShipmentFromExpectedShipDate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var today = BusinessToday();
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
        var sourceRentalId = Guid.NewGuid();
        var renewalRentalId = Guid.NewGuid();
        var sourceEndDate = today.AddDays(-10);
        var renewalStartDate = sourceEndDate.AddDays(1);
        var renewalEndDate = today.AddDays(-3);

        context.Rentals.AddRange(
            new Rental
            {
                Id = sourceRentalId,
                RentalNumber = "R20260601-0001",
                Renter = renter,
                Status = RentalStatus.Renewed,
                StartDate = sourceEndDate.AddDays(-5),
                ExpectedShipDate = sourceEndDate.AddDays(-6),
                ExpectedEndDate = sourceEndDate,
                ActualEndDate = sourceEndDate,
                RenewedToRentalId = renewalRentalId,
                RenewedToRentalNumber = "R20260601-0001-01"
            },
            new Rental
            {
                Id = renewalRentalId,
                RentalNumber = "R20260601-0001-01",
                Renter = renter,
                Status = RentalStatus.Active,
                StartDate = renewalStartDate,
                ExpectedShipDate = renewalStartDate,
                ExpectedEndDate = renewalEndDate,
                RenewedFromRentalId = sourceRentalId,
                RenewedFromRentalNumber = "R20260601-0001"
            });
        context.RentalItems.AddRange(
            new RentalItem
            {
                RentalId = sourceRentalId,
                ItemId = item.Id,
                Item = item,
                ItemShortIdSnapshot = item.ShortId,
                ItemNameSnapshot = definition.Name
            },
            new RentalItem
            {
                RentalId = renewalRentalId,
                ItemId = item.Id,
                Item = item,
                ItemShortIdSnapshot = item.ShortId,
                ItemNameSnapshot = definition.Name
            });
        await context.SaveChangesAsync();

        var controller = new ItemDefinitionsController(context);
        var action = await controller.GetOccupancyCalendar(definition.Id, renewalStartDate, renewalStartDate);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(ok.Value);
        var day = Assert.Single(calendar.DailyStocks);
        var detail = Assert.Single(day.Details);

        Assert.Equal(1, day.OccupiedCount);
        Assert.Equal("R20260601-0001-01", detail.RentalNumber);
        Assert.Equal(ItemOccupancyStatus.Scheduled, detail.OccupancyStatus);
    }

    [Fact]
    public async Task DefinitionOccupancy_KeepsFinalRenewalRentalVisibleAfterOverdueReturn()
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
        var sourceRentalId = Guid.NewGuid();
        var renewalRentalId = Guid.NewGuid();
        var sourceEndDate = new DateTime(2099, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        var renewalStartDate = sourceEndDate.AddDays(1);
        var renewalEndDate = new DateTime(2099, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var actualReturnDate = renewalEndDate.AddDays(4);

        context.Rentals.AddRange(
            new Rental
            {
                Id = sourceRentalId,
                RentalNumber = "R20990601-0001",
                Renter = renter,
                Status = RentalStatus.Renewed,
                StartDate = new DateTime(2099, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpectedShipDate = new DateTime(2099, 5, 31, 0, 0, 0, DateTimeKind.Utc),
                ExpectedEndDate = sourceEndDate,
                ActualEndDate = sourceEndDate,
                RenewedToRentalId = renewalRentalId,
                RenewedToRentalNumber = "R20990601-0001-01"
            },
            new Rental
            {
                Id = renewalRentalId,
                RentalNumber = "R20990601-0001-01",
                Renter = renter,
                Status = RentalStatus.Returned,
                StartDate = renewalStartDate,
                ExpectedShipDate = renewalStartDate,
                ExpectedEndDate = renewalEndDate,
                ActualEndDate = actualReturnDate,
                RenewedFromRentalId = sourceRentalId,
                RenewedFromRentalNumber = "R20990601-0001"
            });
        context.RentalItems.AddRange(
            new RentalItem
            {
                RentalId = sourceRentalId,
                ItemId = item.Id,
                Item = item,
                ItemShortIdSnapshot = item.ShortId,
                ItemNameSnapshot = definition.Name
            },
            new RentalItem
            {
                RentalId = renewalRentalId,
                ItemId = item.Id,
                Item = item,
                ItemShortIdSnapshot = item.ShortId,
                ItemNameSnapshot = definition.Name,
                ReturnedAt = actualReturnDate,
                ReturnCondition = ReturnCondition.Good
            });
        await context.SaveChangesAsync();

        var controller = new ItemDefinitionsController(context);
        var action = await controller.GetOccupancyCalendar(definition.Id, actualReturnDate, actualReturnDate);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var calendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(ok.Value);
        var day = Assert.Single(calendar.DailyStocks);
        var detail = Assert.Single(day.Details);

        Assert.Equal(1, day.OccupiedCount);
        Assert.Equal("R20990601-0001-01", detail.RentalNumber);
        Assert.Equal(ItemOccupancyStatus.Returning, detail.OccupancyStatus);
    }

    [Fact]
    public async Task DefinitionOccupancy_ExtendsOpenRentalAfterExpectedEndUntilExpectedReturnDate()
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
        Assert.Equal(1, futureStock.OccupiedCount);
        Assert.Equal(ItemOccupancyStatus.Returning, Assert.Single(futureStock.Details).OccupancyStatus);
    }

    [Fact]
    public async Task ItemAvailability_ExtendsOpenRentalAfterExpectedEndUntilExpectedReturnDate()
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
        Assert.Equal(futureDate.AddDays(1).AddTicks(-1), returning.EndAt);
        Assert.DoesNotContain(calendar.BusyPeriods, period => period.StartAt.Date > futureDate);
    }

    [Fact]
    public async Task ItemAvailability_ShowsReturnedRentalThroughActualReturnDateAfterExpectedEnd()
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

        var busy = Assert.Single(calendar.BusyPeriods);
        Assert.Equal(today, busy.StartAt);
        Assert.Equal(today.AddDays(1).AddTicks(-1), busy.EndAt);
    }

    [Fact]
    public async Task EarlyReturnedRental_DoesNotOccupyAnyFollowingDay()
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
            ShortId = "CAM-EARLY-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.InStock
        };
        var today = BusinessToday();
        var returnedAt = today.AddHours(2);
        var expectedEndDate = today.AddDays(10);
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20260824-EARLY",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" },
            Status = RentalStatus.Returned,
            StartDate = today.AddDays(-3),
            ExpectedShipDate = today.AddDays(-4),
            ExpectedEndDate = expectedEndDate,
            ExpectedReturnDate = expectedEndDate.AddDays(2),
            ActualEndDate = returnedAt
        };
        rental.Items.Add(new RentalItem
        {
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name,
            ReturnedAt = returnedAt,
            ReleasedFromRentalAt = returnedAt,
            ReturnCondition = ReturnCondition.Good
        });
        rental.Shipments.Add(new RentalShipment
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            ShippedAt = today.AddDays(-4).AddHours(10)
        });
        context.Rentals.Add(rental);
        await context.SaveChangesAsync();

        var rangeStart = today.AddDays(1);
        var itemAction = await new ItemsController(context, new StubWebHostEnvironment())
            .GetAvailability(item.Id, rangeStart, expectedEndDate);
        var itemOk = Assert.IsType<OkObjectResult>(itemAction.Result);
        var itemCalendar = Assert.IsType<ItemAvailabilityCalendarDto>(itemOk.Value);
        Assert.Empty(itemCalendar.BusyPeriods);

        var definitionAction = await new ItemDefinitionsController(context)
            .GetOccupancyCalendar(definition.Id, rangeStart, expectedEndDate);
        var definitionOk = Assert.IsType<OkObjectResult>(definitionAction.Result);
        var definitionCalendar = Assert.IsType<ItemDefinitionOccupancyCalendarDto>(definitionOk.Value);
        Assert.All(definitionCalendar.DailyStocks, day =>
        {
            Assert.Equal(0, day.OccupiedCount);
            Assert.Equal(definitionCalendar.TotalStock, day.RemainingStock);
        });
    }

    [Fact]
    public async Task CreateAsync_DoesNotConflictWithReturnedDefinitionAfterReturnDate()
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
            StartDate = today.AddDays(1),
            ExpectedShipDate = today.AddDays(1),
            ExpectedEndDate = today.AddDays(2)
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
    public async Task AddShipmentAsync_NotifiesCreatorAssigneeAndShipperWithoutDuplicateTargets()
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
        var today = BusinessToday();

        context.Rentals.Add(new Rental
        {
            Id = rentalId,
            RentalNumber = "R20260610-0011",
            Renter = renter,
            Status = RentalStatus.Pending,
            CreatedBy = "Creator",
            AssignedTo = "Manager, Creator",
            SenderName = "Shipper",
            StartDate = today,
            ExpectedShipDate = today,
            ExpectedEndDate = today.AddDays(3)
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

        var result = await rentalService.AddShipmentAsync(rentalId, new CreateShipmentDto
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouseId = warehouse.Id,
            Carrier = "SF",
            TrackingNumber = "SF123",
            ShippingFee = 18.5m,
            ShippedAt = today.AddHours(4)
        }, "Shipper");

        Assert.Null(result.Conflict);
        Assert.Null(result.Error);

        var targets = await context.Reminders
            .Where(reminder => reminder.RelatedEntityId == rentalId.ToString()
                && reminder.Type == ReminderType.Manual)
            .Select(reminder => reminder.TargetUser)
            .OrderBy(target => target)
            .ToListAsync();

        Assert.Equal(new[] { "Creator", "Manager", "Shipper" }, targets);
        var reminder = await context.Reminders.FirstAsync(reminder => reminder.TargetUser == "Creator");
        Assert.Contains("物流：SF SF123；运费：￥18.5", reminder.Message);
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
        var futureStartDate = today.AddDays(2);

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
    public async Task Calendar_ShowsReturnRequiredForRenewalRentalOnExpectedReturnDateWithoutOutboundShipment()
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

        var returnRequiredDay = expectedEndDate.AddDays(2);
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

    [Fact]
    public async Task Calendar_EndsReturnedRentalPeriodOnEarlyActualReturnDate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var startDate = new DateTime(2099, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var returnedAt = new DateTime(2099, 6, 5, 2, 0, 0, DateTimeKind.Utc);
        var expectedEndDate = new DateTime(2099, 6, 20, 0, 0, 0, DateTimeKind.Utc);
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20990601-EARLY",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" },
            Status = RentalStatus.Returned,
            StartDate = startDate,
            ExpectedShipDate = startDate.AddDays(-1),
            ExpectedEndDate = expectedEndDate,
            ExpectedReturnDate = expectedEndDate.AddDays(2),
            ActualEndDate = returnedAt,
            CreatedBy = "Alice"
        };
        rental.Items.Add(new RentalItem
        {
            ItemShortIdSnapshot = "CAM-EARLY-002",
            ItemNameSnapshot = "Camera Body",
            ReturnedAt = returnedAt,
            ReleasedFromRentalAt = returnedAt,
            ReturnCondition = ReturnCondition.Good
        });
        context.Rentals.Add(rental);
        await context.SaveChangesAsync();

        var service = CreateRentalService(context);
        var events = await service.GetCalendarAsync(
            new RentalCalendarQueryParameters { From = startDate, To = expectedEndDate },
            "Alice",
            includeReminders: false,
            canSeeAllReminders: false);

        var rentalPeriod = Assert.Single(events, item =>
            item.Kind == RentalCalendarEventKind.RentalPeriod && item.RentalId == rental.Id);
        var actualReturnDay = returnedAt.Date;
        Assert.Equal(actualReturnDay, rentalPeriod.EndAt);

        var futureEvents = await service.GetCalendarAsync(
            new RentalCalendarQueryParameters
            {
                From = actualReturnDay.AddDays(1),
                To = expectedEndDate
            },
            "Alice",
            includeReminders: false,
            canSeeAllReminders: false);
        Assert.DoesNotContain(futureEvents, item =>
            item.Kind == RentalCalendarEventKind.RentalPeriod && item.RentalId == rental.Id);
    }

    [Fact]
    public async Task Calendar_HidesRenewedSourceRentalAndShowsFinalRenewalReturnRequired()
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
        var sourceRentalId = Guid.NewGuid();
        var renewalRentalId = Guid.NewGuid();
        var sourceEndDate = new DateTime(2099, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        var renewalStartDate = sourceEndDate.AddDays(1);
        var renewalEndDate = new DateTime(2099, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var returnRequiredDay = renewalEndDate.AddDays(2);

        context.Rentals.AddRange(
            new Rental
            {
                Id = sourceRentalId,
                RentalNumber = "R20990601-0001",
                Renter = renter,
                Status = RentalStatus.Renewed,
                StartDate = new DateTime(2099, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpectedShipDate = new DateTime(2099, 5, 31, 0, 0, 0, DateTimeKind.Utc),
                ExpectedEndDate = sourceEndDate,
                ActualEndDate = sourceEndDate,
                RenewedToRentalId = renewalRentalId,
                RenewedToRentalNumber = "R20990601-0001-01",
                CreatedBy = "Alice"
            },
            new Rental
            {
                Id = renewalRentalId,
                RentalNumber = "R20990601-0001-01",
                Renter = renter,
                Status = RentalStatus.Active,
                StartDate = renewalStartDate,
                ExpectedShipDate = renewalStartDate,
                ExpectedEndDate = renewalEndDate,
                RenewedFromRentalId = sourceRentalId,
                RenewedFromRentalNumber = "R20990601-0001",
                CreatedBy = "Alice"
            });
        context.RentalItems.AddRange(
            new RentalItem
            {
                RentalId = sourceRentalId,
                ItemId = item.Id,
                Item = item,
                ItemShortIdSnapshot = item.ShortId,
                ItemNameSnapshot = definition.Name
            },
            new RentalItem
            {
                RentalId = renewalRentalId,
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

        var events = await rentalService.GetCalendarAsync(
            new RentalCalendarQueryParameters
            {
                From = new DateTime(2099, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                To = returnRequiredDay
            },
            "Alice",
            includeReminders: false,
            canSeeAllReminders: false);

        Assert.DoesNotContain(events, item =>
            item.Kind == RentalCalendarEventKind.RentalPeriod
            && item.RentalId == sourceRentalId);

        Assert.Contains(events, item =>
            item.Kind == RentalCalendarEventKind.ReturnRequired
            && item.RentalId == renewalRentalId
            && item.RentalNumber == "R20990601-0001-01");
    }

    [Fact]
    public async Task UpdateRentalItemsAsync_UpdatesSelectionPricesAndTotalAtomically()
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
        var firstItem = new Item { Id = Guid.NewGuid(), ShortId = "CAM-PRICE-001", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.InStock };
        var secondItem = new Item { Id = Guid.NewGuid(), ShortId = "CAM-PRICE-002", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.InStock };
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20990701-0001",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" },
            Status = RentalStatus.Pending,
            StartDate = new DateTime(2099, 7, 2, 0, 0, 0, DateTimeKind.Utc),
            ExpectedShipDate = new DateTime(2099, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            ExpectedEndDate = new DateTime(2099, 7, 5, 0, 0, 0, DateTimeKind.Utc),
            ExpectedReturnDate = new DateTime(2099, 7, 7, 0, 0, 0, DateTimeKind.Utc),
            TotalPrice = 100m
        };
        rental.Items.Add(new RentalItem
        {
            Item = firstItem,
            ItemShortIdSnapshot = firstItem.ShortId,
            ItemNameSnapshot = definition.Name,
            PerItemPrice = 100m
        });
        context.AddRange(secondItem, rental);
        await context.SaveChangesAsync();

        var service = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(),
            new StubSettlementService());

        var result = await service.UpdateRentalItemsAsync(
            rental.Id,
            new UpdateRentalItemsDto
            {
                ItemIds = new List<string> { firstItem.Id.ToString(), secondItem.Id.ToString() },
                ItemPrices = new List<CreateRentalItemPriceDto>
                {
                    new() { ItemId = firstItem.Id.ToString(), PerItemPrice = 125m },
                    new() { ItemId = secondItem.Id.ToString(), PerItemPrice = 75.5m }
                }
            },
            "TestUser");

        Assert.Null(result.Error);
        Assert.NotNull(result.Rental);
        Assert.Equal(200.5m, result.Rental!.TotalPrice);
        Assert.Equal(125m, result.Rental.Items.Single(item => item.ItemId == firstItem.Id).PerItemPrice);
        Assert.Equal(75.5m, result.Rental.Items.Single(item => item.ItemId == secondItem.Id).PerItemPrice);
    }

    [Fact]
    public async Task AddShipmentAsync_PartiallyShippedRentalStaysPendingUntilAllItemsShip()
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
        var definition = new ItemDefinition { Name = "Camera", Category = category, Unit = "pcs", Description = "Camera" };
        var firstItem = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-PARTIAL-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.InStock
        };
        var secondItem = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "CAM-PARTIAL-002",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.InStock
        };
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20990701-PARTIAL",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" },
            Status = RentalStatus.Pending,
            StartDate = BusinessToday(),
            ExpectedShipDate = BusinessToday(),
            ExpectedEndDate = BusinessToday().AddDays(3),
            TotalPrice = 300m
        };
        var firstRentalItem = new RentalItem
        {
            Item = firstItem,
            ItemShortIdSnapshot = firstItem.ShortId,
            ItemNameSnapshot = definition.Name
        };
        var secondRentalItem = new RentalItem
        {
            Item = secondItem,
            ItemShortIdSnapshot = secondItem.ShortId,
            ItemNameSnapshot = definition.Name
        };
        rental.Items.Add(firstRentalItem);
        rental.Items.Add(secondRentalItem);
        context.Rentals.Add(rental);
        await context.SaveChangesAsync();

        var service = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(),
            new StubSettlementService());

        var emptySelection = await service.AddShipmentAsync(rental.Id, new CreateShipmentDto
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouseId = warehouse.Id,
            Carrier = "SF",
            RentalItemIds = new List<int>()
        }, "TestUser");
        Assert.NotNull(emptySelection.Error);
        Assert.False(await context.RentalShipments.AnyAsync());

        var firstShipment = await service.AddShipmentAsync(rental.Id, new CreateShipmentDto
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouseId = warehouse.Id,
            Carrier = "SF",
            RentalItemIds = new List<int> { firstRentalItem.Id },
            ItemSelections = new List<RentalItemShipSelectionDto>
            {
                new() { RentalItemId = firstRentalItem.Id, ItemId = firstItem.Id }
            }
        }, "TestUser");

        Assert.Null(firstShipment.Error);
        Assert.NotNull(firstShipment.Rental);
        Assert.Equal(RentalStatus.PartiallyShipped, firstShipment.Rental!.Status);
        Assert.Single(firstShipment.Rental.Shipments);
        Assert.Single(firstShipment.Rental.Shipments.Single().Items);
        Assert.Equal(firstRentalItem.Id, firstShipment.Rental.Shipments.Single().Items.Single().RentalItemId);
        Assert.Equal(ItemStatus.LoanedOut, firstItem.Status);
        Assert.Equal(ItemStatus.InStock, secondItem.Status);

        var inboundWhilePartial = await service.AddShipmentAsync(rental.Id, new CreateShipmentDto
        {
            Direction = ShipmentDirection.Inbound,
            OriginWarehouseId = warehouse.Id,
            Carrier = "SF"
        }, "TestUser");
        Assert.NotNull(inboundWhilePartial.Error);

        var secondShipment = await service.AddShipmentAsync(rental.Id, new CreateShipmentDto
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouseId = warehouse.Id,
            Carrier = "SF",
            RentalItemIds = new List<int> { secondRentalItem.Id },
            ItemSelections = new List<RentalItemShipSelectionDto>
            {
                new() { RentalItemId = secondRentalItem.Id, ItemId = secondItem.Id }
            }
        }, "TestUser");

        Assert.Null(secondShipment.Error);
        Assert.NotNull(secondShipment.Rental);
        Assert.Equal(RentalStatus.Active, secondShipment.Rental!.Status);
        Assert.Equal(2, secondShipment.Rental.Shipments.Count);
        Assert.Equal(ItemStatus.LoanedOut, secondItem.Status);
    }

    [Fact]
    public async Task AddShipmentAsync_WithoutSelections_LinksEveryOpenItemAndActivatesRental()
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
        var definition = new ItemDefinition { Name = "Camera", Category = category, Unit = "pcs", Description = "Camera" };
        var firstItem = new Item { Id = Guid.NewGuid(), ShortId = "CAM-ALL-001", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.InStock };
        var secondItem = new Item { Id = Guid.NewGuid(), ShortId = "CAM-ALL-002", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.InStock };
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20990701-ALL",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" },
            Status = RentalStatus.Pending,
            StartDate = BusinessToday(),
            ExpectedShipDate = BusinessToday(),
            ExpectedEndDate = BusinessToday().AddDays(3)
        };
        rental.Items.Add(new RentalItem { Item = firstItem, ItemShortIdSnapshot = firstItem.ShortId, ItemNameSnapshot = definition.Name });
        rental.Items.Add(new RentalItem { Item = secondItem, ItemShortIdSnapshot = secondItem.ShortId, ItemNameSnapshot = definition.Name });
        context.Rentals.Add(rental);
        await context.SaveChangesAsync();

        var result = await CreateRentalService(context).AddShipmentAsync(rental.Id, new CreateShipmentDto
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouseId = warehouse.Id,
            Carrier = "SF"
        }, "TestUser");

        Assert.Null(result.Error);
        Assert.NotNull(result.Rental);
        Assert.Equal(RentalStatus.Active, result.Rental!.Status);
        var shipment = Assert.Single(result.Rental.Shipments);
        Assert.Equal(2, shipment.Items.Count);
        Assert.Equal(
            result.Rental.Items.Select(item => item.Id).OrderBy(id => id),
            shipment.Items.Select(item => item.RentalItemId).OrderBy(id => id));
        Assert.Equal(ItemStatus.LoanedOut, firstItem.Status);
        Assert.Equal(ItemStatus.LoanedOut, secondItem.Status);
    }

    [Fact]
    public async Task UpdateShipmentAsync_CanRepairLegacyUnassignedOutboundAndRestoreRentalState()
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
        var definition = new ItemDefinition { Name = "Camera", Category = category, Unit = "pcs", Description = "Camera" };
        var firstItem = new Item { Id = Guid.NewGuid(), ShortId = "CAM-REPAIR-001", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.InStock };
        var secondItem = new Item { Id = Guid.NewGuid(), ShortId = "CAM-REPAIR-002", Warehouse = warehouse, ItemDefinition = definition, Status = ItemStatus.InStock };
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20990701-REPAIR",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" },
            Status = RentalStatus.PartiallyShipped,
            StartDate = BusinessToday(),
            ExpectedShipDate = BusinessToday(),
            ExpectedEndDate = BusinessToday().AddDays(3)
        };
        var firstRentalItem = new RentalItem { Item = firstItem, ItemShortIdSnapshot = firstItem.ShortId, ItemNameSnapshot = definition.Name };
        var secondRentalItem = new RentalItem { Item = secondItem, ItemShortIdSnapshot = secondItem.ShortId, ItemNameSnapshot = definition.Name };
        rental.Items.Add(firstRentalItem);
        rental.Items.Add(secondRentalItem);
        var legacyShipment = new RentalShipment
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "SF",
            ShippedAt = DateTime.UtcNow
        };
        rental.Shipments.Add(legacyShipment);
        context.Rentals.Add(rental);
        await context.SaveChangesAsync();

        var result = await CreateRentalService(context).UpdateShipmentAsync(rental.Id, legacyShipment.Id, new UpdateShipmentDto
        {
            ItemSelections = new List<RentalItemShipSelectionDto>
            {
                new() { RentalItemId = firstRentalItem.Id, ItemId = firstItem.Id },
                new() { RentalItemId = secondRentalItem.Id, ItemId = secondItem.Id }
            }
        }, "TestUser");

        Assert.Null(result.error);
        Assert.NotNull(result.rental);
        Assert.Equal(RentalStatus.Active, result.rental!.Status);
        var repairedShipment = Assert.Single(result.rental.Shipments);
        Assert.Equal(2, repairedShipment.Items.Count);
        Assert.All(repairedShipment.Items, item => Assert.NotNull(item.ItemId));
        Assert.Equal(ItemStatus.LoanedOut, firstItem.Status);
        Assert.Equal(ItemStatus.LoanedOut, secondItem.Status);
    }

    [Fact]
    public async Task DeleteShipmentAsync_LastOutboundRestoresPendingInventoryAndRecalculatesShippingFee()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
        var category = new Category { Name = "Lens", Description = "Lens category" };
        var definition = new ItemDefinition { Name = "Lens", Category = category, Unit = "pcs", Description = "Lens" };
        var rentalNumber = "R20990701-0002";
        var item = new Item
        {
            Id = Guid.NewGuid(),
            ShortId = "LENS-DELETE-001",
            Warehouse = warehouse,
            ItemDefinition = definition,
            Status = ItemStatus.LoanedOut,
            CurrentDestination = $"租赁 {rentalNumber}"
        };
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = rentalNumber,
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" },
            Status = RentalStatus.Active,
            StartDate = BusinessToday(),
            ExpectedShipDate = BusinessToday().AddDays(-1),
            ExpectedEndDate = BusinessToday().AddDays(3),
            TotalPrice = 300m
        };
        var rentalItem = new RentalItem
        {
            Item = item,
            ItemShortIdSnapshot = item.ShortId,
            ItemNameSnapshot = definition.Name,
            PerItemPrice = 300m
        };
        rental.Items.Add(rentalItem);
        var shipment = new RentalShipment
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "顺丰速运",
            TrackingNumber = "SF-DELETE-001",
            ShippingFee = 18.5m,
            ShippedAt = DateTime.UtcNow
        };
        shipment.RentalItems.Add(new RentalShipmentItem { RentalItem = rentalItem });
        rental.Shipments.Add(shipment);
        context.Rentals.Add(rental);
        await context.SaveChangesAsync();

        var service = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(),
            new StubSettlementService());

        var (updated, error) = await service.DeleteShipmentAsync(rental.Id, shipment.Id, "TestUser");

        Assert.Null(error);
        Assert.NotNull(updated);
        Assert.Equal(RentalStatus.Pending, updated!.Status);
        Assert.Empty(updated.Shipments);
        Assert.Equal(0m, updated.TotalShippingFee);
        Assert.Equal(ItemStatus.InStock, item.Status);
        Assert.Null(item.CurrentDestination);
        Assert.False(await context.RentalShipments.AnyAsync());
        Assert.False(await context.RentalShipmentItems.AnyAsync());
    }

    [Fact]
    public async Task DeleteShipmentAsync_LastInboundRestoresOverdueStatusAndKeepsOutboundFee()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20000101-0001",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" },
            Status = RentalStatus.Active,
            StartDate = BusinessToday().AddDays(-10),
            ExpectedShipDate = BusinessToday().AddDays(-11),
            ExpectedEndDate = BusinessToday().AddDays(-2),
            TotalPrice = 300m
        };
        var outbound = new RentalShipment
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "顺丰速运",
            ShippingFee = 10m,
            ShippedAt = DateTime.UtcNow.AddDays(-11)
        };
        var inbound = new RentalShipment
        {
            Direction = ShipmentDirection.Inbound,
            OriginWarehouse = warehouse,
            Carrier = "顺丰速运",
            ShippingFee = 6m,
            ShippedAt = DateTime.UtcNow.AddDays(-1)
        };
        rental.Shipments.Add(outbound);
        rental.Shipments.Add(inbound);
        context.Rentals.Add(rental);
        await context.SaveChangesAsync();

        var service = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            Array.Empty<INotificationChannel>(),
            new StubSfExpressService(),
            new StubSettlementService());

        var (updated, error) = await service.DeleteShipmentAsync(rental.Id, inbound.Id, "TestUser");

        Assert.Null(error);
        Assert.NotNull(updated);
        Assert.Equal(RentalStatus.Overdue, updated!.Status);
        Assert.Single(updated.Shipments);
        Assert.Equal(outbound.Id, updated.Shipments.Single().Id);
        Assert.Equal(10m, updated.TotalShippingFee);
    }

    [Fact]
    public async Task UpdateShipmentAsync_NotifiesWithLogisticsAndFreightChange()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
        var rental = new Rental
        {
            Id = Guid.NewGuid(),
            RentalNumber = "R20990701-0003",
            Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" },
            Status = RentalStatus.Active,
            StartDate = BusinessToday(),
            ExpectedShipDate = BusinessToday(),
            ExpectedEndDate = BusinessToday().AddDays(3),
            TotalPrice = 300m,
            CreatedBy = "Creator"
        };
        var shipment = new RentalShipment
        {
            Direction = ShipmentDirection.Outbound,
            OriginWarehouse = warehouse,
            Carrier = "顺丰速运",
            TrackingNumber = "SF-FEE-001",
            ShippingFee = 12.5m,
            ShippedAt = DateTime.UtcNow
        };
        rental.Shipments.Add(shipment);
        context.Rentals.Add(rental);
        await context.SaveChangesAsync();

        var channel = new CapturingNotificationChannel();
        var service = new RentalService(
            context,
            new StubRenterService(),
            new StubIdentityService(),
            new[] { channel },
            new StubSfExpressService(),
            new StubSettlementService());

        var (updated, error) = await service.UpdateShipmentAsync(
            rental.Id,
            shipment.Id,
            new UpdateShipmentDto { ShippingFee = 24m },
            "TestUser");

        Assert.Null(error);
        Assert.NotNull(updated);
        Assert.Equal(24m, updated!.Shipments.Single().ShippingFee);

        var reminder = await context.Reminders.SingleAsync();
        Assert.Equal("Creator", reminder.TargetUser);
        Assert.Contains("物流：顺丰速运 SF-FEE-001", reminder.Message);
        Assert.Contains("运费：￥12.5 -> ￥24.0", reminder.Message);
        Assert.Single(channel.Delivered);
    }

    [Theory]
    [InlineData(false, RentalStatus.Active)]
    [InlineData(true, RentalStatus.Overdue)]
    public async Task CancelAsync_RenewalRental_RestoresSourceRental(bool isOverdue, RentalStatus expectedStatus)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        var today = BusinessToday();
        var expectedEndDate = isOverdue ? today.AddDays(-2) : today.AddDays(2);
        Guid sourceRentalId;
        Guid itemId;
        Guid renewalRentalId;

        await using (var arrangeContext = new ApplicationDbContext(options))
        {
            await arrangeContext.Database.EnsureCreatedAsync();

            var warehouse = new Warehouse { Name = "Main", Location = "A1", Description = "Main warehouse" };
            var category = new Category { Name = "Camera", Description = "Camera category" };
            var definition = new ItemDefinition { Name = "Camera Body", Category = category, Unit = "pcs", Description = "Body" };
            var item = new Item
            {
                Id = Guid.NewGuid(),
                ShortId = "CAM-RENEW-CANCEL-001",
                Warehouse = warehouse,
                ItemDefinition = definition,
                Status = ItemStatus.LoanedOut,
                CurrentDestination = "租赁 R20990101-0001"
            };
            var sourceRental = new Rental
            {
                Id = Guid.NewGuid(),
                RentalNumber = "R20990101-0001",
                Renter = new Renter { Id = Guid.NewGuid(), Name = "Tenant", Phone = "13800138000" },
                Status = isOverdue ? RentalStatus.Overdue : RentalStatus.Active,
                StartDate = today.AddDays(-5),
                ExpectedShipDate = today.AddDays(-6),
                ExpectedEndDate = expectedEndDate,
                ExpectedReturnDate = expectedEndDate.AddDays(2)
            };
            sourceRental.Items.Add(new RentalItem
            {
                Item = item,
                ItemShortIdSnapshot = item.ShortId,
                ItemNameSnapshot = definition.Name,
                PerItemPrice = 100m
            });
            sourceRental.Shipments.Add(new RentalShipment
            {
                Direction = ShipmentDirection.Outbound,
                OriginWarehouse = warehouse,
                Carrier = "SF",
                ShippedAt = today.AddDays(-6)
            });
            arrangeContext.Rentals.Add(sourceRental);
            await arrangeContext.SaveChangesAsync();

            sourceRentalId = sourceRental.Id;
            itemId = item.Id;

            var renewService = CreateRentalService(arrangeContext);
            var renewResult = await renewService.RenewAsync(sourceRentalId, new RenewRentalDto
            {
                StartDate = expectedEndDate.AddDays(1),
                ExpectedEndDate = expectedEndDate.AddDays(5),
                TotalPrice = 100m
            }, "TestUser");

            Assert.Null(renewResult.Error);
            Assert.NotNull(renewResult.RenewalRental);
            renewalRentalId = renewResult.RenewalRental!.Id;
        }

        await using (var cancelContext = new ApplicationDbContext(options))
        {
            var cancelService = CreateRentalService(cancelContext);
            var (cancelledRental, error) = await cancelService.CancelAsync(
                renewalRentalId,
                new CancelRentalDto { Reason = "Customer changed plans" },
                "TestUser");

            Assert.Null(error);
            Assert.NotNull(cancelledRental);
            Assert.Equal(RentalStatus.Cancelled, cancelledRental!.Status);
            Assert.All(cancelledRental.Items, rentalItem => Assert.NotNull(rentalItem.ReturnedAt));
        }

        await using var assertContext = new ApplicationDbContext(options);
        var restoredSource = await assertContext.Rentals.SingleAsync(rental => rental.Id == sourceRentalId);
        Assert.Equal(expectedStatus, restoredSource.Status);
        Assert.Null(restoredSource.ActualEndDate);
        Assert.Equal(expectedEndDate.AddDays(2), restoredSource.ExpectedReturnDate);
        Assert.Null(restoredSource.RenewedToRentalId);
        Assert.Null(restoredSource.RenewedToRentalNumber);

        var restoredItem = await assertContext.Items.SingleAsync(item => item.Id == itemId);
        Assert.Equal(ItemStatus.LoanedOut, restoredItem.Status);
        Assert.Equal($"租赁 {restoredSource.RentalNumber}", restoredItem.CurrentDestination);
    }

    private static RentalService CreateRentalService(ApplicationDbContext context) => new(
        context,
        new StubRenterService(),
        new StubIdentityService(),
        Array.Empty<INotificationChannel>(),
        new StubSfExpressService(),
        new StubSettlementService());

    private sealed class StubRenterService : IRenterService
    {
        public Task<IEnumerable<RenterDto>> SearchAsync(string? keyword, int limit) => Task.FromResult(Enumerable.Empty<RenterDto>());
        public Task<RenterDto?> GetByIdAsync(Guid id) => Task.FromResult<RenterDto?>(null);
        public Task<RenterDto> CreateAsync(CreateRenterDto dto, string? currentUser) => throw new NotImplementedException();
        public Task<RenterDto?> UpdateAsync(Guid id, UpdateRenterDto dto, string? currentUser) => Task.FromResult<RenterDto?>(null);
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

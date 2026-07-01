using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AuditIt.Api.Tests;

public class ItemVisibilityTests
{
    [Fact]
    public async Task ItemsQuery_showsDisposedItemsButHidesSoftDeletedItems()
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

        context.Items.AddRange(
            new Item
            {
                Id = Guid.NewGuid(),
                ShortId = "DISPOSED-VISIBLE",
                Warehouse = warehouse,
                ItemDefinition = definition,
                Status = ItemStatus.Disposed
            },
            new Item
            {
                Id = Guid.NewGuid(),
                ShortId = "DISPOSED-HIDDEN",
                Warehouse = warehouse,
                ItemDefinition = definition,
                Status = ItemStatus.Disposed,
                IsDeleted = true,
                DeletedAt = DateTime.UtcNow
            });
        await context.SaveChangesAsync();

        var visibleShortIds = await context.Items
            .OrderBy(i => i.ShortId)
            .Select(i => i.ShortId)
            .ToListAsync();
        var allCount = await context.Items.IgnoreQueryFilters().CountAsync();

        Assert.Equal(["DISPOSED-VISIBLE"], visibleShortIds);
        Assert.Equal(2, allCount);
    }
}

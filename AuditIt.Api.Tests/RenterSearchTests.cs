using AuditIt.Api.Data;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AuditIt.Api.Tests;

public class RenterSearchTests
{
    [Theory]
    [InlineData("lovelace")]
    [InlineData("001380")]
    [InlineData("ALPHA-42")]
    [InlineData("xy-ada")]
    [InlineData("tb-ada")]
    [InlineData("xhs-ada")]
    [InlineData("Huangpu")]
    [InlineData("photography")]
    public async Task SearchAsync_fuzzyMatchesEveryRenterProfileField(string keyword)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var renter = new Renter
        {
            Id = Guid.NewGuid(),
            Name = "Ada Lovelace",
            Phone = "13800138000",
            IdCardNo = "ID-ALPHA-42",
            XianyuId = "xy-ada-store",
            TaobaoId = "tb-ada-shop",
            XiaohongshuId = "xhs-ada-red",
            DefaultAddress = "Shanghai Huangpu Road 1",
            Notes = "VIP photography client"
        };
        context.Renters.Add(renter);
        await context.SaveChangesAsync();

        var service = new RenterService(context, new EmptyIdentityService(), Array.Empty<INotificationChannel>());

        var result = await service.SearchAsync(keyword, 50);

        Assert.Equal(renter.Id, Assert.Single(result).Id);
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
}

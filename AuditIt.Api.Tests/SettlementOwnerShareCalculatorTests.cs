using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Xunit;

namespace AuditIt.Api.Tests;

public class SettlementOwnerShareCalculatorTests
{
    [Fact]
    public void BuildOwnerShares_mergesDistinctOwnersIntoSingleSettlementLine()
    {
        var rental = new Rental
        {
            Items =
            {
                new RentalItem
                {
                    ItemShortIdSnapshot = "CAM-001",
                    ItemNameSnapshot = "Camera A",
                    PerItemPrice = 100m,
                    Item = new Item { OwnerUserNamesSnapshot = "Owner,Alice" }
                },
                new RentalItem
                {
                    ItemShortIdSnapshot = "CAM-002",
                    ItemNameSnapshot = "Camera B",
                    PerItemPrice = 300m,
                    Item = new Item { OwnerUserNamesSnapshot = "alice,Bob" }
                }
            }
        };

        var shares = SettlementOwnerShareCalculator.BuildOwnerShares(rental, 1000m, 50m);

        var share = Assert.Single(shares);
        Assert.Equal("Owner,Alice,Bob", share.OwnerName);
        Assert.Null(share.ItemShortId);
        Assert.Null(share.ItemName);
        Assert.Equal(500m, share.Amount);
    }

    [Fact]
    public void NormalizeOwnerSnapshot_trimsEmptyNamesAndKeepsFirstDistinctName()
    {
        var snapshot = ItemOwnerSnapshot.Normalize(new[] { " Alice ", "", "Bob", "alice", " Bob " });

        Assert.Equal("Alice,Bob", snapshot);
        Assert.Equal(["Alice", "Bob"], ItemOwnerSnapshot.Split(snapshot));
    }
}

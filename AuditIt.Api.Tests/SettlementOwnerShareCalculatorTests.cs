using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Xunit;

namespace AuditIt.Api.Tests;

public class SettlementOwnerShareCalculatorTests
{
    [Fact]
    public void BuildOwnerShares_keepsEachRentalItemAsSeparateSettlementLine()
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
                    Item = new Item { OwnerUserNamesSnapshot = "Owner" }
                },
                new RentalItem
                {
                    ItemShortIdSnapshot = "CAM-002",
                    ItemNameSnapshot = "Camera B",
                    PerItemPrice = 300m,
                    Item = new Item { OwnerUserNamesSnapshot = "Owner" }
                }
            }
        };

        var shares = SettlementOwnerShareCalculator.BuildOwnerShares(rental, 1000m, 50m);

        Assert.Collection(
            shares,
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
    }

    [Fact]
    public void NormalizeOwnerSnapshot_trimsEmptyNamesAndKeepsFirstDistinctName()
    {
        var snapshot = ItemOwnerSnapshot.Normalize(new[] { " Alice ", "", "Bob", "alice", " Bob " });

        Assert.Equal("Alice,Bob", snapshot);
        Assert.Equal(["Alice", "Bob"], ItemOwnerSnapshot.Split(snapshot));
    }
}

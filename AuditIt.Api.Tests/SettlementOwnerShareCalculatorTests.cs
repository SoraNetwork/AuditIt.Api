using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Xunit;

namespace AuditIt.Api.Tests;

public class SettlementOwnerShareCalculatorTests
{
    [Fact]
    public void BuildOwnerShares_usesMultiOwnerSnapshotAsSingleSettlementLabel()
    {
        var rental = new Rental
        {
            Items =
            {
                new RentalItem
                {
                    PerItemPrice = 100m,
                    Item = new Item { OwnerUserNamesSnapshot = " Alice , Bob " }
                },
                new RentalItem
                {
                    PerItemPrice = 300m,
                    Item = new Item { OwnerUserNamesSnapshot = "Bob" }
                }
            }
        };

        var shares = SettlementOwnerShareCalculator.BuildOwnerShares(rental, 1000m, 50m);

        Assert.Collection(
            shares,
            share =>
            {
                Assert.Equal("Bob", share.OwnerName);
                Assert.Equal(375m, share.Amount);
            },
            share =>
            {
                Assert.Equal("Alice,Bob", share.OwnerName);
                Assert.Equal(125m, share.Amount);
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

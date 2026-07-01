using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    public static class SettlementOwnerShareCalculator
    {
        public static IReadOnlyList<(string? OwnerName, decimal Amount, string? ItemShortId, string? ItemName)> BuildOwnerShares(
            Rental rental,
            decimal accountedAmount,
            decimal itemOwnerPercent)
        {
            var ownerPool = PercentAmount(accountedAmount, itemOwnerPercent);
            if (ownerPool <= 0 || rental.Items.Count == 0)
            {
                return Array.Empty<(string? OwnerName, decimal Amount, string? ItemShortId, string? ItemName)>();
            }

            var ownerName = ItemOwnerSnapshot.Normalize(
                rental.Items.SelectMany(i => ItemOwnerSnapshot.Split(i.Item?.OwnerUserNamesSnapshot)));

            return [(
                OwnerName: ownerName,
                Amount: ownerPool,
                ItemShortId: (string?)null,
                ItemName: (string?)null)];
        }

        private static decimal PercentAmount(decimal amount, decimal percent) =>
            RoundMoney(amount * percent / 100m);

        private static decimal RoundMoney(decimal value) =>
            Math.Round(value, 1, MidpointRounding.AwayFromZero);

    }
}

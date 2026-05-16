using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    public static class SettlementOwnerShareCalculator
    {
        public static IReadOnlyList<(string? OwnerName, decimal Amount)> BuildOwnerShares(
            Rental rental,
            decimal accountedAmount,
            decimal itemOwnerPercent)
        {
            var ownerPool = PercentAmount(accountedAmount, itemOwnerPercent);
            if (ownerPool <= 0 || rental.Items.Count == 0)
            {
                return Array.Empty<(string? OwnerName, decimal Amount)>();
            }

            var pricedTotal = rental.Items.Sum(i => i.PerItemPrice ?? 0m);
            var equalWeight = pricedTotal <= 0 ? 1m / rental.Items.Count : 0m;
            var rows = rental.Items
                .Select(i =>
                {
                    var weight = pricedTotal > 0
                        ? (i.PerItemPrice ?? 0m) / pricedTotal
                        : equalWeight;
                    return new
                    {
                        OwnerName = ItemOwnerSnapshot.Normalize(
                            ItemOwnerSnapshot.Split(i.Item?.OwnerUserNamesSnapshot)),
                        Amount = ownerPool * weight
                    };
                })
                .Where(i => i.Amount > 0)
                .GroupBy(i => i.OwnerName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(g => (OwnerName: g.Key, Amount: RoundMoney(g.Sum(i => i.Amount))))
                .Where(i => i.Amount > 0)
                .OrderBy(i => string.IsNullOrWhiteSpace(i.OwnerName))
                .ThenByDescending(i => i.Amount)
                .ThenBy(i => i.OwnerName)
                .Select(i => (OwnerName: string.IsNullOrWhiteSpace(i.OwnerName) ? null : i.OwnerName, i.Amount))
                .ToList();

            return rows;
        }

        private static decimal PercentAmount(decimal amount, decimal percent) =>
            RoundMoney(amount * percent / 100m);

        private static decimal RoundMoney(decimal value) =>
            Math.Round(value, 1, MidpointRounding.AwayFromZero);
    }
}

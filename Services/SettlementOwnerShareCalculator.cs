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

            var pricedTotal = rental.Items.Sum(i => i.PerItemPrice ?? 0m);
            var equalWeight = pricedTotal <= 0 ? 1m / rental.Items.Count : 0m;
            var rows = rental.Items
                .Select((i, index) =>
                {
                    var weight = pricedTotal > 0
                        ? (i.PerItemPrice ?? 0m) / pricedTotal
                        : equalWeight;
                    return new
                    {
                        Index = index,
                        OwnerName = ItemOwnerSnapshot.Normalize(
                            ItemOwnerSnapshot.Split(i.Item?.OwnerUserNamesSnapshot)),
                        Amount = ownerPool * weight,
                        ItemShortId = NormalizeText(i.ItemShortIdSnapshot),
                        ItemName = NormalizeText(i.ItemNameSnapshot)
                    };
                })
                .Where(i => i.Amount > 0)
                .OrderBy(i => i.Index)
                .Select(i => (
                    OwnerName: i.OwnerName,
                    Amount: RoundMoney(i.Amount),
                    ItemShortId: i.ItemShortId,
                    ItemName: i.ItemName))
                .Where(i => i.Amount > 0)
                .ToList();

            var roundingDelta = ownerPool - rows.Sum(i => i.Amount);
            if (rows.Count > 0 && roundingDelta != 0)
            {
                var last = rows[^1];
                rows[^1] = (
                    last.OwnerName,
                    RoundMoney(last.Amount + roundingDelta),
                    last.ItemShortId,
                    last.ItemName);
            }

            return rows;
        }

        private static decimal PercentAmount(decimal amount, decimal percent) =>
            RoundMoney(amount * percent / 100m);

        private static decimal RoundMoney(decimal value) =>
            Math.Round(value, 1, MidpointRounding.AwayFromZero);

        private static string? NormalizeText(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

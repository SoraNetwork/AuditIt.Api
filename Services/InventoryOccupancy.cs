using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AuditIt.Api.Services;

// One source of truth for the definition calendar, individual calendar and
// scheduling validation. All intervals include both China business dates.
internal sealed class InventoryOccupancy
{
    internal sealed record Period(int DefinitionId, Item? Item, Rental? Rental,
        RentalItem? RentalItem, DateTime Start, DateTime End)
    {
        public bool IsManualLoan => Rental == null;
        public bool IsUncertain => Item == null;
        public bool Contains(DateTime day) => Start <= day && End >= day;

        public ItemOccupancyStatus StatusOn(DateTime day) => Rental == null
            || day <= RentalDateRules.ToBusinessDate(Rental.ExpectedEndDate)
                ? ItemOccupancyStatus.Scheduled
                : day <= RentalDateRules.EffectiveExpectedEndDate(Rental)
                    ? ItemOccupancyStatus.RenewalIntent
                    : ItemOccupancyStatus.Returning;

        public ItemDefinitionDailyOccupancyDto ToDetail(DateTime day) => new()
        {
            RentalId = Rental?.Id ?? Guid.Empty,
            RentalNumber = Rental?.RentalNumber ?? $"普通借出 ({Item!.ShortId})",
            RentalStatus = Rental?.Status ?? RentalStatus.Active,
            RenterId = Rental?.RenterId ?? Guid.Empty,
            RenterName = Rental?.Renter?.Name,
            Quantity = 1,
            IsManualLoan = IsManualLoan,
            IsUncertain = IsUncertain,
            HasRenewalIntent = Rental?.HasRenewalIntent ?? false,
            RenewalIntentEndDate = Rental?.HasRenewalIntent == true ? Rental.RenewalIntentEndDate : null,
            ExpectedReturnDate = IsManualLoan ? Item!.ExpectedReturnDate : Rental!.ExpectedReturnDate,
            OccupancyStatus = StatusOn(day)
        };
    }

    public List<Item> Stock { get; private init; } = new();
    public List<Period> Periods { get; private init; } = new();

    public static async Task<InventoryOccupancy> LoadAsync(ApplicationDbContext context,
        IReadOnlyCollection<int> definitionIds, DateTime from, DateTime to, Guid? excludeRentalId = null)
    {
        var ids = definitionIds.Distinct().ToArray();
        var stock = await context.Items.AsNoTracking()
            .Where(i => ids.Contains(i.ItemDefinitionId)
                && i.Status != ItemStatus.Disposed && i.Status != ItemStatus.SuspectedMissing)
            .ToListAsync();
        var stockById = stock.ToDictionary(i => i.Id);
        var rentals = await context.Rentals.AsNoTracking().AsSplitQuery()
            .Include(r => r.Renter)
            .Include(r => r.Items).ThenInclude(ri => ri.Item)
            .Include(r => r.Shipments).ThenInclude(s => s.RentalItems)
            .Where(r => r.Status != RentalStatus.Cancelled
                && (!excludeRentalId.HasValue || r.Id != excludeRentalId.Value))
            .Where(r => r.Items.Any(ri => (ri.ItemDefinitionId.HasValue && ids.Contains(ri.ItemDefinitionId.Value))
                || (ri.Item != null && ids.Contains(ri.Item.ItemDefinitionId))))
            .ToListAsync();
        var periods = new List<Period>();
        foreach (var rental in rentals)
        {
            foreach (var line in rental.Items)
            {
                Item? item = null;
                if (line.ItemId.HasValue && !stockById.TryGetValue(line.ItemId.Value, out item)) continue;
                var definitionId = item?.ItemDefinitionId ?? line.ItemDefinitionId;
                if (!definitionId.HasValue || !ids.Contains(definitionId.Value)) continue;
                var start = RentalDateRules.OccupancyStartDate(rental, line);
                var end = RentalDateRules.OccupancyEndDate(rental, line, to);
                if (end < start || start > to.Date || end < from.Date) continue;
                periods.Add(new Period(definitionId.Value, item, rental, line, start, end));
            }
        }

        var loanIds = stock.Where(i => i.Status == ItemStatus.LoanedOut
            && !(i.CurrentDestination?.StartsWith("租赁 ") ?? false)).Select(i => i.Id).ToArray();
        var outboundDates = await context.AuditLogs.AsNoTracking()
            .Where(log => loanIds.Contains(log.ItemId) && log.Action == AuditAction.Outbound)
            .GroupBy(log => log.ItemId)
            .Select(group => new { ItemId = group.Key, At = group.Max(log => log.Timestamp) })
            .ToDictionaryAsync(entry => entry.ItemId, entry => entry.At);
        var today = RentalDateRules.Today(DateTime.UtcNow);
        foreach (var id in loanIds)
        {
            var item = stockById[id];
            // Legacy/imported loans can lack an audit row. They still consume stock.
            var start = RentalDateRules.ToBusinessDate(outboundDates.GetValueOrDefault(id, item.LastUpdated));
            var end = RentalDateRules.ManualLoanOccupancyEndDate(item.ExpectedReturnDate, today);
            var cursor = start;
            // Subtract only overlapping days, never suppress a loan for the whole
            // query merely because the same item has a reservation later on.
            foreach (var rentalPeriod in periods.Where(p => p.Item?.Id == id && !p.IsManualLoan).OrderBy(p => p.Start).ToList())
            {
                if (rentalPeriod.End < cursor) continue;
                if (rentalPeriod.Start > end) break;
                if (rentalPeriod.Start > cursor)
                    AddLoan(cursor, rentalPeriod.Start.AddDays(-1));
                cursor = rentalPeriod.End.AddDays(1);
                if (cursor > end) break;
            }
            if (cursor <= end) AddLoan(cursor, end);

            void AddLoan(DateTime first, DateTime last)
            {
                if (first <= to.Date && last >= from.Date)
                    periods.Add(new Period(item.ItemDefinitionId, item, null, null, first, last));
            }
        }
        return new InventoryOccupancy { Stock = stock, Periods = periods };
    }

    public List<Period> OnDay(int definitionId, DateTime day, int? warehouseId = null)
    {
        var active = Periods.Where(p => p.DefinitionId == definitionId && p.Contains(day)).ToList();
        if (!warehouseId.HasValue) return active;
        var capacity = Stock.Where(i => i.ItemDefinitionId == definitionId)
            .GroupBy(i => i.WarehouseId).ToDictionary(g => g.Key, g => g.Count());
        var result = active.Where(p => p.Item?.WarehouseId == warehouseId).ToList();
        foreach (var period in active.Where(p => p.Item != null))
            capacity[period.Item!.WarehouseId] = capacity.GetValueOrDefault(period.Item.WarehouseId) - 1;

        // Recompute capacity for this day. Non-overlapping orders must not consume
        // each other's capacity; selecting a different month must not move demand.
        foreach (var group in active.Where(p => p.IsUncertain).OrderBy(p => p.Rental!.Id)
            .ThenBy(p => p.RentalItem!.Id).GroupBy(p => p.Rental!.Id))
        {
            var candidates = capacity.Where(c => c.Value >= group.Count()).Select(c => c.Key).Order().ToList();
            if (candidates.Count > 0)
            {
                var selected = candidates[StableOffset(group.Key, definitionId, candidates.Count)];
                capacity[selected] -= group.Count();
                if (selected == warehouseId) result.AddRange(group);
                continue;
            }
            foreach (var period in group)
            {
                candidates = capacity.Where(c => c.Value > 0).Select(c => c.Key).Order().ToList();
                if (candidates.Count == 0) candidates = capacity.Keys.Order().ToList();
                if (candidates.Count == 0) continue;
                var selected = candidates[StableOffset(group.Key, period.RentalItem!.Id, candidates.Count)];
                capacity[selected]--;
                if (selected == warehouseId) result.Add(period);
            }
        }
        return result;
    }

    private static int StableOffset(Guid id, int discriminator, int count)
    {
        var bytes = id.ToByteArray();
        uint hash = 2166136261;
        foreach (var value in bytes) hash = unchecked((hash ^ value) * 16777619);
        hash = unchecked((hash ^ (uint)discriminator) * 16777619);
        return (int)(hash % (uint)count);
    }

    public static List<ItemBusyPeriodDto> ToBusyPeriods(IEnumerable<Period> periods, DateTime from, DateTime to)
    {
        var result = new List<ItemBusyPeriodDto>();
        foreach (var period in periods)
        {
            var start = period.Start < from.Date ? from.Date : period.Start;
            var end = period.End > to.Date ? to.Date : period.End;
            for (var day = start; day <= end;)
            {
                var status = period.StatusOn(day);
                var last = day;
                while (last < end && period.StatusOn(last.AddDays(1)) == status) last = last.AddDays(1);
                var detail = period.ToDetail(day);
                result.Add(new ItemBusyPeriodDto
                {
                    RentalId = detail.RentalId, RentalNumber = period.IsManualLoan ? "普通借出" : detail.RentalNumber,
                    RentalStatus = detail.RentalStatus, RenterId = detail.RenterId, RenterName = detail.RenterName,
                    StartAt = day, EndAt = last.AddDays(1).AddTicks(-1),
                    IsOpen = period.IsManualLoan
                        ? !period.Item!.ExpectedReturnDate.HasValue || RentalDateRules.ToBusinessDate(period.Item.ExpectedReturnDate.Value) < RentalDateRules.Today(DateTime.UtcNow)
                        : period.RentalItem!.ReturnedAt == null && period.Rental!.Status != RentalStatus.Returned && period.Rental.Status != RentalStatus.Renewed,
                    IsUncertain = detail.IsUncertain, IsManualLoan = detail.IsManualLoan,
                    HasRenewalIntent = detail.HasRenewalIntent, RenewalIntentEndDate = detail.RenewalIntentEndDate,
                    ExpectedReturnDate = detail.ExpectedReturnDate, OccupancyStatus = status
                });
                day = last.AddDays(1);
            }
        }
        return result.OrderBy(p => p.StartAt).ThenBy(p => p.EndAt).ToList();
    }
}

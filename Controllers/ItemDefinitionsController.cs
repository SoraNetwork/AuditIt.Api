using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AuditIt.Api.Data;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.AspNetCore.Authorization;

namespace AuditIt.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ItemDefinitionsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public ItemDefinitionsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: api/ItemDefinitions
        [HttpGet]
        [RequirePermission(PermissionCodes.ItemView)]
        public async Task<ActionResult<IEnumerable<ItemDefinition>>> GetItemDefinitions()
        {
            return await _context.ItemDefinitions.Include(i => i.Category).ToListAsync();
        }

        // GET: api/ItemDefinitions/5
        [HttpGet("{id}")]
        [RequirePermission(PermissionCodes.ItemView)]
        public async Task<ActionResult<ItemDefinition>> GetItemDefinition(int id)
        {
            var itemDefinition = await _context.ItemDefinitions.Include(i => i.Category).FirstOrDefaultAsync(i => i.Id == id);

            if (itemDefinition == null)
            {
                return NotFound();
            }

            return itemDefinition;
        }

        // PUT: api/ItemDefinitions/5
        [HttpPut("{id}")]
        [RequirePermission(PermissionCodes.ItemDefinitionManage)]
        public async Task<IActionResult> PutItemDefinition(int id, ItemDefinition itemDefinition)
        {
            if (id != itemDefinition.Id)
            {
                return BadRequest();
            }

            _context.Entry(itemDefinition).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!ItemDefinitionExists(id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }

            return NoContent();
        }

        // POST: api/ItemDefinitions
        [HttpPost]
        [RequirePermission(PermissionCodes.ItemDefinitionManage)]
        public async Task<ActionResult<ItemDefinition>> PostItemDefinition(CreateItemDefinitionDto itemDefinitionDto)
        {
            var category = await _context.Categories.FindAsync(itemDefinitionDto.CategoryId);
            if (category == null)
            {
                return BadRequest("Invalid CategoryId");
            }

            var itemDefinition = new ItemDefinition
            {
                Name = itemDefinitionDto.Name,
                CategoryId = itemDefinitionDto.CategoryId,
                Unit = itemDefinitionDto.Unit,
                Description = itemDefinitionDto.Description,
                CreatedAt = DateTime.UtcNow
            };

            _context.ItemDefinitions.Add(itemDefinition);
            await _context.SaveChangesAsync();

            // Load the category to include it in the response
            await _context.Entry(itemDefinition).Reference(i => i.Category).LoadAsync();

            return CreatedAtAction("GetItemDefinition", new { id = itemDefinition.Id }, itemDefinition);
        }

        // GET: api/ItemDefinitions/5/occupancy
        [HttpGet("{id}/occupancy")]
        [RequirePermission(PermissionCodes.ItemView)]
        public async Task<ActionResult<ItemDefinitionOccupancyCalendarDto>> GetOccupancyCalendar(
            int id,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to)
        {
            var def = await _context.ItemDefinitions.FindAsync(id);
            if (def == null)
            {
                return NotFound();
            }

            var today = RentalDateRules.Today(DateTime.UtcNow);
            var rangeStart = (from ?? today.AddDays(-7)).Date;
            var rangeEnd = (to ?? today.AddDays(60)).Date.AddDays(1).AddTicks(-1);

            if (rangeEnd < rangeStart)
            {
                (rangeStart, rangeEnd) = (rangeEnd.Date, rangeStart.Date.AddDays(1).AddTicks(-1));
            }

            if ((rangeEnd - rangeStart).TotalDays > 180)
            {
                rangeEnd = rangeStart.AddDays(180).AddTicks(-1);
            }

            var totalStock = await _context.Items.CountAsync(i => i.ItemDefinitionId == id && i.Status != ItemStatus.Disposed);

            var candidateRentals = await _context.Rentals
                .Include(r => r.Renter)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                .Include(r => r.Shipments)
                .Where(r => r.Status != RentalStatus.Cancelled)
                .Where(r => (r.ExpectedShipDate <= rangeEnd.AddDays(1)
                    || r.StartDate <= rangeEnd.AddDays(1)
                    || r.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound && s.ShippedAt <= rangeEnd.AddDays(1)))
                    && (r.ExpectedEndDate >= rangeStart.AddDays(-3)
                        || r.ExpectedReturnDate >= rangeStart.AddDays(-1)
                        || (r.HasRenewalIntent
                            && r.RenewalIntentEndDate.HasValue
                            && r.RenewalIntentEndDate.Value >= rangeStart.AddDays(-1))
                        || r.ActualEndDate >= rangeStart.AddDays(-1)
                        || r.Items.Any(ri => ri.ReturnedAt >= rangeStart.AddDays(-1))
                        || ((r.RenewedFromRentalId != null
                                || r.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound))
                            && r.Items.Any(ri => ri.ReturnedAt == null))))
                .ToListAsync();

            var overlappingRentals = candidateRentals
                .Where(r => RentalDateRules.Overlaps(
                    OccupancyStartDate(r),
                    RentalDateRules.OccupancyEndDate(r, rangeEnd),
                    rangeStart,
                    rangeEnd))
                .ToList();
            var specificRentalPeriodsByItemId = overlappingRentals
                .SelectMany(r => r.Items.Select(ri => new { Rental = r, RentalItem = ri }))
                .Where(entry => entry.RentalItem.ItemId.HasValue
                    && entry.RentalItem.Item != null
                    && entry.RentalItem.Item.ItemDefinitionId == id
                    && RentalDateRules.Overlaps(
                        RentalDateRules.OccupancyStartDate(entry.Rental),
                        RentalDateRules.OccupancyEndDate(entry.Rental, entry.RentalItem, rangeEnd),
                        rangeStart,
                        rangeEnd))
                .Select(entry => new
                {
                    ItemId = entry.RentalItem.ItemId!.Value,
                    StartDay = RentalDateRules.OccupancyStartDate(entry.Rental),
                    EndDay = RentalDateRules.OccupancyEndDate(entry.Rental, entry.RentalItem, rangeEnd)
                })
                .GroupBy(entry => entry.ItemId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(entry => (StartDay: entry.StartDay.Date, EndDay: entry.EndDay.Date)).ToList());

            var manualLoanCandidates = await _context.Items
                .Where(i => i.ItemDefinitionId == id && i.Status == ItemStatus.LoanedOut)
                .Where(i => i.CurrentDestination == null || !i.CurrentDestination.StartsWith("租赁 "))
                .Select(i => new
                {
                    i.Id,
                    i.ShortId,
                    i.LastUpdated,
                    i.ExpectedReturnDate,
                    OutboundAt = _context.AuditLogs
                        .Where(log => log.ItemId == i.Id && log.Action == AuditAction.Outbound)
                        .OrderByDescending(log => log.Timestamp)
                        .Select(log => (DateTime?)log.Timestamp)
                        .FirstOrDefault()
                })
                .ToListAsync();
            var manualLoans = manualLoanCandidates
                .Where(loan => loan.OutboundAt.HasValue)
                .ToList();

            var dayCount = (rangeEnd.Date - rangeStart.Date).Days + 1;
            var occupancyDiff = new int[dayCount + 1];
            var detailBuckets = Enumerable.Range(0, dayCount)
                .Select(_ => new Dictionary<string, ItemDefinitionDailyOccupancyDto>())
                .ToList();

            void AddDetail(int dayIndex, ItemDefinitionDailyOccupancyDto detail)
            {
                var key = string.Join("|",
                    detail.RentalId,
                    detail.RentalNumber,
                    detail.RentalStatus,
                    detail.RenterId,
                    detail.IsUncertain,
                    detail.IsManualLoan,
                    detail.HasRenewalIntent,
                    detail.RenewalIntentEndDate,
                    detail.ExpectedReturnDate,
                    detail.OccupancyStatus);

                if (detailBuckets[dayIndex].TryGetValue(key, out var existing))
                {
                    existing.Quantity += detail.Quantity;
                    return;
                }

                detailBuckets[dayIndex][key] = detail;
            }

            void AddSegment(DateTime startDay, DateTime endDay, ItemDefinitionDailyOccupancyDto detail)
            {
                var clippedStart = startDay.Date < rangeStart.Date ? rangeStart.Date : startDay.Date;
                var clippedEnd = endDay.Date > rangeEnd.Date ? rangeEnd.Date : endDay.Date;
                if (clippedEnd < clippedStart)
                {
                    return;
                }

                var startIndex = (clippedStart - rangeStart.Date).Days;
                var endIndex = (clippedEnd - rangeStart.Date).Days;
                occupancyDiff[startIndex] += detail.Quantity;
                if (endIndex + 1 < occupancyDiff.Length)
                {
                    occupancyDiff[endIndex + 1] -= detail.Quantity;
                }

                for (var dayIndex = startIndex; dayIndex <= endIndex; dayIndex++)
                {
                    AddDetail(dayIndex, new ItemDefinitionDailyOccupancyDto
                    {
                        RentalId = detail.RentalId,
                        RentalNumber = detail.RentalNumber,
                        RentalStatus = detail.RentalStatus,
                        RenterId = detail.RenterId,
                        RenterName = detail.RenterName,
                        Quantity = detail.Quantity,
                        IsUncertain = detail.IsUncertain,
                        IsManualLoan = detail.IsManualLoan,
                        HasRenewalIntent = detail.HasRenewalIntent,
                        RenewalIntentEndDate = detail.RenewalIntentEndDate,
                        ExpectedReturnDate = detail.ExpectedReturnDate,
                        OccupancyStatus = detail.OccupancyStatus
                    });
                }
            }

            void AddRentalItemSegment(Rental rental, RentalItem rentalItem, bool isUncertain)
            {
                var startDay = RentalDateRules.OccupancyStartDate(rental);
                var endDay = RentalDateRules.OccupancyEndDate(rental, rentalItem, rangeEnd);
                var expectedEndDay = RentalDateRules.ToBusinessDate(rental.ExpectedEndDate);
                var effectiveExpectedEndDay = RentalDateRules.EffectiveExpectedEndDate(rental);

                var detail = new ItemDefinitionDailyOccupancyDto
                {
                    RentalId = rental.Id,
                    RentalNumber = rental.RentalNumber,
                    RentalStatus = rental.Status,
                    RenterId = rental.RenterId,
                    RenterName = rental.Renter?.Name,
                    Quantity = 1,
                    IsUncertain = isUncertain,
                    HasRenewalIntent = rental.HasRenewalIntent,
                    RenewalIntentEndDate = rental.HasRenewalIntent && rental.RenewalIntentEndDate.HasValue
                        ? RentalDateRules.ToBusinessDate(rental.RenewalIntentEndDate.Value)
                        : null,
                    ExpectedReturnDate = rental.ExpectedReturnDate.HasValue
                        ? RentalDateRules.ToBusinessDate(rental.ExpectedReturnDate.Value)
                        : null
                };

                var scheduledEnd = endDay < expectedEndDay ? endDay : expectedEndDay;
                if (startDay <= scheduledEnd)
                {
                    detail.OccupancyStatus = ItemOccupancyStatus.Scheduled;
                    AddSegment(startDay, scheduledEnd, detail);
                }

                if (rental.HasRenewalIntent
                    && rental.RenewalIntentEndDate.HasValue
                    && effectiveExpectedEndDay > expectedEndDay)
                {
                    var renewalIntentStart = expectedEndDay.AddDays(1);
                    var renewalIntentEnd = endDay < effectiveExpectedEndDay ? endDay : effectiveExpectedEndDay;
                    if (renewalIntentStart <= renewalIntentEnd)
                    {
                        detail.OccupancyStatus = ItemOccupancyStatus.RenewalIntent;
                        AddSegment(renewalIntentStart, renewalIntentEnd, detail);
                    }
                }

                var returningStart = effectiveExpectedEndDay.AddDays(1);
                if (returningStart <= endDay)
                {
                    detail.OccupancyStatus = ItemOccupancyStatus.Returning;
                    AddSegment(returningStart, endDay, detail);
                }
            }

            void AddManualLoanSegments(
                Guid itemId,
                DateTime startDay,
                DateTime endDay,
                ItemDefinitionDailyOccupancyDto detail)
            {
                var clippedStart = startDay.Date < rangeStart.Date ? rangeStart.Date : startDay.Date;
                var clippedEnd = endDay.Date > rangeEnd.Date ? rangeEnd.Date : endDay.Date;
                if (clippedEnd < clippedStart)
                {
                    return;
                }

                specificRentalPeriodsByItemId.TryGetValue(itemId, out var rentalPeriods);
                DateTime? freeSegmentStart = null;

                for (var day = clippedStart; day <= clippedEnd; day = day.AddDays(1))
                {
                    var occupiedByRental = rentalPeriods?.Any(period => period.StartDay <= day && period.EndDay >= day) == true;
                    if (!occupiedByRental)
                    {
                        freeSegmentStart ??= day;
                        continue;
                    }

                    if (freeSegmentStart.HasValue)
                    {
                        AddSegment(freeSegmentStart.Value, day.AddDays(-1), detail);
                        freeSegmentStart = null;
                    }
                }

                if (freeSegmentStart.HasValue)
                {
                    AddSegment(freeSegmentStart.Value, clippedEnd, detail);
                }
            }

            foreach (var rental in overlappingRentals)
            {
                foreach (var rentalItem in rental.Items.Where(ri =>
                    ri.ItemId != null
                    && ri.Item != null
                    && ri.Item.ItemDefinitionId == id))
                {
                    AddRentalItemSegment(rental, rentalItem, isUncertain: false);
                }

                foreach (var rentalItem in rental.Items.Where(ri =>
                    ri.ItemId == null
                    && ri.ItemDefinitionId == id))
                {
                    AddRentalItemSegment(rental, rentalItem, isUncertain: true);
                }
            }

            foreach (var manualLoan in manualLoans.OrderBy(loan => loan.ShortId))
            {
                AddManualLoanSegments(
                    manualLoan.Id,
                    RentalDateRules.ToBusinessDate(manualLoan.OutboundAt ?? manualLoan.LastUpdated),
                    RentalDateRules.ManualLoanOccupancyEndDate(manualLoan.ExpectedReturnDate, today),
                    new ItemDefinitionDailyOccupancyDto
                    {
                        RentalId = Guid.Empty,
                        RentalNumber = $"普通借出 ({manualLoan.ShortId})",
                        RentalStatus = RentalStatus.Active,
                        Quantity = 1,
                        IsManualLoan = true,
                        ExpectedReturnDate = manualLoan.ExpectedReturnDate.HasValue
                            ? RentalDateRules.ToBusinessDate(manualLoan.ExpectedReturnDate.Value)
                            : null,
                        OccupancyStatus = ItemOccupancyStatus.Scheduled
                    });
            }

            var prefixDailyStocks = new List<ItemDefinitionDailyStockDto>();
            var prefixOccupiedCount = 0;
            for (var dayIndex = 0; dayIndex < dayCount; dayIndex++)
            {
                prefixOccupiedCount += occupancyDiff[dayIndex];
                prefixDailyStocks.Add(new ItemDefinitionDailyStockDto
                {
                    Date = rangeStart.Date.AddDays(dayIndex),
                    TotalStock = totalStock,
                    OccupiedCount = prefixOccupiedCount,
                    RemainingStock = totalStock - prefixOccupiedCount,
                    Details = detailBuckets[dayIndex]
                        .Values
                        .OrderBy(detail => detail.IsManualLoan)
                        .ThenBy(detail => detail.IsUncertain)
                        .ThenBy(detail => detail.RentalNumber)
                        .ThenBy(detail => detail.OccupancyStatus)
                        .ToList()
                });
            }

            return Ok(new ItemDefinitionOccupancyCalendarDto
            {
                ItemDefinitionId = id,
                Name = def.Name,
                TotalStock = totalStock,
                From = rangeStart,
                To = rangeEnd,
                DailyStocks = prefixDailyStocks
            });

#if false
            var dailyStocks = new List<ItemDefinitionDailyStockDto>();
            var currentDay = rangeStart.Date;

            while (currentDay <= rangeEnd.Date)
            {
                var dayRentals = overlappingRentals
                    .Where(r => OccupancyStartDate(r) <= currentDay
                        && RentalDateRules.OccupancyEndDate(
                            r.ExpectedEndDate,
                            r.ActualEndDate,
                            openEndedUntil: RentalDateRules.OpenEndedUntil(
                                r.ActualEndDate,
                                null,
                                HasRentalStarted(r) && r.Items.Any(ri => ri.ReturnedAt == null),
                                currentDay),
                            includeReturnBuffer: ShouldUseReturnBuffer(r)) >= currentDay)
                    .ToList();

                var details = new List<ItemDefinitionDailyOccupancyDto>();
                var occupiedCount = 0;

                foreach (var r in dayRentals)
                {
                    var hasRentalStarted = HasRentalStarted(r);
                    var specificGroups = r.Items
                        .Where(ri =>
                            ri.ItemId != null
                            && ri.Item != null
                            && ri.Item.ItemDefinitionId == id
                            && RentalDateRules.OccupiesBusinessDate(
                                r.ExpectedShipDate,
                                r.ExpectedEndDate,
                                r.Shipments,
                                currentDay,
                                r.ActualEndDate,
                                ri.ReturnedAt,
                                RentalDateRules.OpenEndedUntil(r.ActualEndDate, ri.ReturnedAt, hasRentalStarted, currentDay),
                                ShouldUseReturnBuffer(r),
                                RentalDateRules.EffectiveReleasedFromRentalAt(ri),
                                r.Status == RentalStatus.Returned ? r.StartDate : null))
                        .GroupBy(_ => ResolveOccupancyStatus(r.ExpectedEndDate, currentDay));
                    var uncertainGroups = r.Items
                        .Where(ri =>
                            ri.ItemId == null
                            && ri.ItemDefinitionId == id
                            && RentalDateRules.OccupiesBusinessDate(
                                r.ExpectedShipDate,
                                r.ExpectedEndDate,
                                r.Shipments,
                                currentDay,
                                r.ActualEndDate,
                                ri.ReturnedAt,
                                RentalDateRules.OpenEndedUntil(r.ActualEndDate, ri.ReturnedAt, hasRentalStarted, currentDay),
                                ShouldUseReturnBuffer(r),
                                RentalDateRules.EffectiveReleasedFromRentalAt(ri),
                                r.Status == RentalStatus.Returned ? r.StartDate : null))
                        .GroupBy(_ => ResolveOccupancyStatus(r.ExpectedEndDate, currentDay));

                    foreach (var group in specificGroups)
                    {
                        var quantity = group.Count();
                        occupiedCount += quantity;
                        details.Add(new ItemDefinitionDailyOccupancyDto
                        {
                            RentalId = r.Id,
                            RentalNumber = r.RentalNumber,
                            RentalStatus = r.Status,
                            RenterId = r.RenterId,
                            RenterName = r.Renter?.Name,
                            Quantity = quantity,
                            IsUncertain = false,
                            OccupancyStatus = group.Key
                        });
                    }

                    foreach (var group in uncertainGroups)
                    {
                        var quantity = group.Count();
                        occupiedCount += quantity;
                        details.Add(new ItemDefinitionDailyOccupancyDto
                        {
                            RentalId = r.Id,
                            RentalNumber = r.RentalNumber,
                            RentalStatus = r.Status,
                            RenterId = r.RenterId,
                            RenterName = r.Renter?.Name,
                            Quantity = quantity,
                            IsUncertain = true,
                            OccupancyStatus = group.Key
                        });
                    }
                }

                foreach (var manualLoan in manualLoans
                    .Where(loan => RentalDateRules.ToBusinessDate(loan.OutboundAt ?? loan.LastUpdated) <= currentDay)
                    .OrderBy(loan => loan.ShortId))
                {
                    occupiedCount++;
                    details.Add(new ItemDefinitionDailyOccupancyDto
                    {
                        RentalId = Guid.Empty,
                        RentalNumber = $"普通借出 ({manualLoan.ShortId})",
                        RentalStatus = RentalStatus.Active,
                        Quantity = 1,
                        IsUncertain = false,
                        IsManualLoan = true,
                        OccupancyStatus = ItemOccupancyStatus.Scheduled
                    });
                }

                dailyStocks.Add(new ItemDefinitionDailyStockDto
                {
                    Date = currentDay,
                    TotalStock = totalStock,
                    OccupiedCount = occupiedCount,
                    RemainingStock = totalStock - occupiedCount,
                    Details = details
                });

                currentDay = currentDay.AddDays(1);
            }

            return Ok(new ItemDefinitionOccupancyCalendarDto
            {
                ItemDefinitionId = id,
                Name = def.Name,
                TotalStock = totalStock,
                From = rangeStart,
                To = rangeEnd,
                DailyStocks = dailyStocks
            });
#endif
        }

        // DELETE: api/ItemDefinitions/5
        [HttpDelete("{id}")]
        [RequirePermission(PermissionCodes.ItemDefinitionManage)]
        public async Task<IActionResult> DeleteItemDefinition(int id)
        {
            var itemDefinition = await _context.ItemDefinitions.FindAsync(id);
            if (itemDefinition == null)
            {
                return NotFound();
            }

            _context.ItemDefinitions.Remove(itemDefinition);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        private bool ItemDefinitionExists(int id)
        {
            return _context.ItemDefinitions.Any(e => e.Id == id);
        }

        private static bool HasRentalStarted(Rental rental) =>
            rental.RenewedFromRentalId.HasValue
            || rental.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound);

        private static DateTime OccupancyStartDate(Rental rental) =>
            RentalDateRules.OccupancyStartDate(rental);

        private static bool ShouldUseReturnBuffer(Rental rental) =>
            rental.Status != RentalStatus.Renewed
            && !rental.RenewedToRentalId.HasValue;

        private static ItemOccupancyStatus ResolveOccupancyStatus(DateTime expectedEndDate, DateTime day) =>
            RentalDateRules.IsReturningBusinessDate(expectedEndDate, day)
                ? ItemOccupancyStatus.Returning
                : ItemOccupancyStatus.Scheduled;
    }
}

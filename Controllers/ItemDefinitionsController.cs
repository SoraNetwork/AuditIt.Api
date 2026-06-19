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
                .Where(r => (r.StartDate <= rangeEnd.AddDays(1)
                    || r.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound && s.ShippedAt <= rangeEnd.AddDays(1)))
                    && (r.ExpectedEndDate >= rangeStart.AddDays(-1)
                        || r.ActualEndDate >= rangeStart.AddDays(-1)
                        || r.Items.Any(ri => ri.ReturnedAt >= rangeStart.AddDays(-1))
                        || ((r.RenewedFromRentalId != null
                                || r.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound))
                            && r.Items.Any(ri => ri.ReturnedAt == null))))
                .ToListAsync();

            var overlappingRentals = candidateRentals
                .Where(r => RentalDateRules.Overlaps(
                    RentalDateRules.OccupancyStartDate(r.StartDate, r.Shipments),
                    RentalDateRules.OccupancyEndDate(
                        r.ExpectedEndDate,
                        r.ActualEndDate,
                        openEndedUntil: RentalDateRules.OpenEndedUntil(
                            r.ActualEndDate,
                            null,
                            HasRentalStarted(r) && r.Items.Any(ri => ri.ReturnedAt == null),
                            rangeEnd)),
                    rangeStart,
                    rangeEnd))
                .ToList();

            var dailyStocks = new List<ItemDefinitionDailyStockDto>();
            var currentDay = rangeStart.Date;

            while (currentDay <= rangeEnd.Date)
            {
                var dayRentals = overlappingRentals
                    .Where(r => RentalDateRules.OccupancyStartDate(r.StartDate, r.Shipments) <= currentDay
                        && RentalDateRules.OccupancyEndDate(
                            r.ExpectedEndDate,
                            r.ActualEndDate,
                            openEndedUntil: RentalDateRules.OpenEndedUntil(
                                r.ActualEndDate,
                                null,
                                HasRentalStarted(r) && r.Items.Any(ri => ri.ReturnedAt == null),
                                currentDay)) >= currentDay)
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
                                r.StartDate,
                                r.ExpectedEndDate,
                                r.Shipments,
                                currentDay,
                                r.ActualEndDate,
                                ri.ReturnedAt,
                                RentalDateRules.OpenEndedUntil(r.ActualEndDate, ri.ReturnedAt, hasRentalStarted, currentDay)))
                        .GroupBy(_ => ResolveOccupancyStatus(r.ExpectedEndDate, currentDay));
                    var uncertainGroups = r.Items
                        .Where(ri =>
                            ri.ItemId == null
                            && ri.ItemDefinitionId == id
                            && RentalDateRules.OccupiesBusinessDate(
                                r.StartDate,
                                r.ExpectedEndDate,
                                r.Shipments,
                                currentDay,
                                r.ActualEndDate,
                                ri.ReturnedAt,
                                RentalDateRules.OpenEndedUntil(r.ActualEndDate, ri.ReturnedAt, hasRentalStarted, currentDay)))
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

        private static ItemOccupancyStatus ResolveOccupancyStatus(DateTime expectedEndDate, DateTime day) =>
            RentalDateRules.IsReturningBusinessDate(expectedEndDate, day)
                ? ItemOccupancyStatus.Returning
                : ItemOccupancyStatus.Scheduled;
    }
}

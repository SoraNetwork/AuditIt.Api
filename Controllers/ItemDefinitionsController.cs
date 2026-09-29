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
            [FromQuery] DateTime? to,
            [FromQuery] int? warehouseId = null)
        {
            var def = await _context.ItemDefinitions.FindAsync(id);
            if (def == null)
            {
                return NotFound();
            }

            Warehouse? warehouse = null;
            if (warehouseId.HasValue)
            {
                warehouse = await _context.Warehouses.FindAsync(warehouseId.Value);
                if (warehouse == null)
                {
                    return BadRequest("仓库不存在");
                }
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

            var occupancy = await InventoryOccupancy.LoadAsync(_context, new[] { id }, rangeStart, rangeEnd);
            var totalStock = occupancy.Stock.Count(i => !warehouseId.HasValue || i.WarehouseId == warehouseId);
            var days = new List<ItemDefinitionDailyStockDto>();
            for (var day = rangeStart.Date; day <= rangeEnd.Date; day = day.AddDays(1))
            {
                var active = occupancy.OnDay(id, day, warehouseId);
                var details = active.Select(p => p.ToDetail(day))
                    .GroupBy(d => new { d.RentalId, d.RentalNumber, d.IsUncertain, d.IsManualLoan, d.OccupancyStatus, d.ExpectedReturnDate })
                    .Select(group => { var detail = group.First(); detail.Quantity = group.Count(); return detail; })
                    .OrderBy(d => d.IsManualLoan).ThenBy(d => d.RentalNumber).ThenBy(d => d.OccupancyStatus)
                    .ToList();
                days.Add(new ItemDefinitionDailyStockDto
                {
                    Date = day, TotalStock = totalStock, OccupiedCount = active.Count,
                    RemainingStock = totalStock - active.Count, Details = details
                });
            }
            return Ok(new ItemDefinitionOccupancyCalendarDto
            {
                ItemDefinitionId = id, Name = def.Name, WarehouseId = warehouseId,
                WarehouseName = warehouse?.Name, TotalStock = totalStock,
                From = rangeStart, To = rangeEnd, DailyStocks = days
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

    }
}

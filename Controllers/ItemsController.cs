using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.AspNetCore.Authorization;

namespace AuditIt.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ItemsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public ItemsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: api/Items
        [HttpGet]
        public async Task<ActionResult<IEnumerable<Item>>> GetItems([FromQuery] ItemQueryParameters queryParameters)
        {
            var query = _context.Items.Include(i => i.ItemDefinition).Include(i => i.Warehouse).AsQueryable();

            if (queryParameters.WarehouseId.HasValue)
            {
                query = query.Where(i => i.WarehouseId == queryParameters.WarehouseId.Value);
            }

            if (queryParameters.Status.HasValue)
            {
                query = query.Where(i => i.Status == queryParameters.Status.Value);
            }

            if (queryParameters.Id.HasValue)
            {
                query = query.Where(i => i.Id == queryParameters.Id.Value);
            }

            if (!string.IsNullOrEmpty(queryParameters.ShortId))
            {
                query = query.Where(i => i.ShortId == queryParameters.ShortId);
            }

            return await query.OrderByDescending(i => i.LastUpdated).ToListAsync();
        }

        // POST: api/Items/inbound
        [HttpPost("inbound")]
        public async Task<ActionResult<Item>> Inbound(InboundRequest request)
        {
            var itemDefinition = await _context.ItemDefinitions.FindAsync(request.ItemDefinitionId);
            if (itemDefinition == null)
            {
                return BadRequest("Item definition not found.");
            }

            var warehouse = await _context.Warehouses.FindAsync(request.WarehouseId);
            if (warehouse == null)
            {
                return BadRequest("Warehouse not found.");
            }

            var item = new Item
            {
                Id = Guid.NewGuid(), // Generate a new UUID
                ShortId = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper(),
                ItemDefinitionId = request.ItemDefinitionId,
                WarehouseId = request.WarehouseId,
                Status = ItemStatus.InStock,
                EntryDate = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow
            };

            _context.Items.Add(item);
            
            await LogAudit(item, AuditAction.Inbound, itemDefinition.Name, warehouse.Name);

            await _context.SaveChangesAsync();

            // Manually load navigation properties to be returned in the response
            await _context.Entry(item).Reference(i => i.ItemDefinition).LoadAsync();
            await _context.Entry(item).Reference(i => i.Warehouse).LoadAsync();

            return CreatedAtAction(nameof(GetItems), new { id = item.Id }, item);
        }

        // PUT: api/Items/{id}/outbound
        [HttpPut("{id}/outbound")]
        public async Task<ActionResult<Item>> Outbound(Guid id)
        {
            return await UpdateItemStatus(id, ItemStatus.LoanedOut, AuditAction.Outbound);
        }

        // PUT: api/Items/{id}/check
        [HttpPut("{id}/check")]
        public async Task<ActionResult<Item>> Check(Guid id)
        {
            var item = await _context.Items
                .Include(i => i.ItemDefinition)
                .Include(i => i.Warehouse)
                .FirstOrDefaultAsync(i => i.Id == id);

            if (item == null)
            {
                return NotFound();
            }
            
            await LogAudit(item, AuditAction.Check, item.ItemDefinition.Name, item.Warehouse.Name);
            await _context.SaveChangesAsync();
            return Ok(item);
        }

        // PUT: api/Items/{id}/return
        [HttpPut("{id}/return")]
        public async Task<ActionResult<Item>> Return(Guid id)
        {
            return await UpdateItemStatus(id, ItemStatus.InStock, AuditAction.Return);
        }

        // PUT: api/Items/{id}/dispose
        [HttpPut("{id}/dispose")]
        public async Task<ActionResult<Item>> Dispose(Guid id)
        {
            return await UpdateItemStatus(id, ItemStatus.Disposed, AuditAction.Dispose);
        }

        private async Task<ActionResult<Item>> UpdateItemStatus(Guid id, ItemStatus newStatus, AuditAction action)
        {
            var item = await _context.Items
                .Include(i => i.ItemDefinition)
                .Include(i => i.Warehouse)
                .FirstOrDefaultAsync(i => i.Id == id);

            if (item == null)
            {
                return NotFound();
            }

            item.Status = newStatus;
            item.LastUpdated = DateTime.UtcNow;
            _context.Entry(item).State = EntityState.Modified;

            await LogAudit(item, action, item.ItemDefinition.Name, item.Warehouse.Name);
            
            await _context.SaveChangesAsync();

            return Ok(item);
        }

        private async Task LogAudit(Item item, AuditAction action, string itemName, string warehouseName)
        {
            var userName = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "Unknown User";
            
            var auditLog = new AuditLog
            {
                Timestamp = DateTime.UtcNow,
                Action = action,
                ItemId = item.Id, // Now a Guid
                Item = item,
                ItemShortId = item.ShortId,
                ItemName = itemName,
                WarehouseId = item.WarehouseId,
                WarehouseName = warehouseName,
                User = userName
            };

            _context.AuditLogs.Add(auditLog);
        }
    }

    public class InboundRequest
    {
        public int ItemDefinitionId { get; set; }
        public int WarehouseId { get; set; }
    }
}
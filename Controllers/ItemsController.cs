using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Webp;

namespace AuditIt.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ItemsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _env;

        public ItemsController(ApplicationDbContext context, IWebHostEnvironment env)
        {
            _context = context;
            _env = env;
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

        // POST: api/Items/batch
        [HttpPost("batch")]
        public async Task<ActionResult<IEnumerable<Item>>> GetItemsBatch([FromBody] Guid[] ids)
        {
            if (ids == null || !ids.Any())
            {
                return BadRequest("No item IDs provided.");
            }
            var items = await _context.Items
                .Where(i => ids.Contains(i.Id))
                .Include(i => i.ItemDefinition)
                .Include(i => i.Warehouse)
                .ToListAsync();
            return Ok(items);
        }

        // POST: api/Items/create
        [HttpPost("create")]
        [Consumes("multipart/form-data")]
        public async Task<ActionResult<Item>> CreateItem([FromForm] CreateItemDto dto)
        {
            var itemDefinition = await _context.ItemDefinitions.FindAsync(dto.ItemDefinitionId);
            if (itemDefinition == null) return BadRequest("Item definition not found.");

            var warehouse = await _context.Warehouses.FindAsync(dto.WarehouseId);
            if (warehouse == null) return BadRequest("Warehouse not found.");

            string? photoUrl = null;
            if (dto.Photo != null)
            {
                photoUrl = await SavePhoto(dto.Photo);
            }

            var newItemId = Guid.NewGuid();
            var shortId = !string.IsNullOrEmpty(dto.ShortId)
                ? dto.ShortId
                : newItemId.ToString().Substring(0, 8).ToUpper();

            var item = new Item
            {
                Id = newItemId,
                ShortId = shortId,
                ItemDefinitionId = dto.ItemDefinitionId,
                WarehouseId = dto.WarehouseId,
                Remarks = dto.Remarks,
                PhotoUrl = photoUrl,
                Status = ItemStatus.InStock,
                EntryDate = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow
            };

            _context.Items.Add(item);
            await LogAudit(item, AuditAction.Inbound, itemDefinition.Name, warehouse.Name, "Initial creation");
            await _context.SaveChangesAsync();

            await _context.Entry(item).Reference(i => i.ItemDefinition).LoadAsync();
            await _context.Entry(item).Reference(i => i.Warehouse).LoadAsync();

            return CreatedAtAction(nameof(GetItems), new { id = item.Id }, item);
        }

	// POST: api/Items/create/batch
        [HttpPost("create/batch")]
        public async Task<IActionResult> PostItems([FromBody] CreateItemsDto dto)
        {
            var itemDefinition = await _context.ItemDefinitions.FindAsync(dto.ItemDefinitionId);
            if (itemDefinition == null) return BadRequest("Item definition not found.");
            var warehouse = await _context.Warehouses.FindAsync(dto.WarehouseId);
            if (warehouse == null) return BadRequest("Warehouse not found.");
            var newItems = new List<Item>();
            foreach (var itemDto in dto.Items)
            {
                var newItemId = Guid.NewGuid();
                var shortId = !string.IsNullOrEmpty(itemDto.ShortId)
                    ? itemDto.ShortId
                    : newItemId.ToString().Substring(0, 8).ToUpper();
                var item = new Item
                {
                    Id = newItemId,
                    ShortId = shortId,
                    ItemDefinitionId = dto.ItemDefinitionId,
                    WarehouseId = dto.WarehouseId,
                    Remarks = itemDto.Remarks,
                    Status = ItemStatus.InStock,
                    EntryDate = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow
                };
                newItems.Add(item);
                _context.Items.Add(item);
                await LogAudit(item, AuditAction.Inbound, itemDefinition.Name, warehouse.Name, "Initial creation");
            }
            await _context.SaveChangesAsync();
            return Ok(new { message = $"{newItems.Count} items created successfully." });
        }

        // PUT: api/Items/{id}
        [HttpPut("{id}")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UpdateItem(Guid id, [FromForm] UpdateItemDto dto)
        {
            var item = await _context.Items.FirstOrDefaultAsync(i => i.Id == id);
            if (item == null)
            {
                return NotFound();
            }

            if (!string.IsNullOrEmpty(dto.ShortId))
            {
                // Optional: Add validation to ensure ShortId is unique if needed
                item.ShortId = dto.ShortId;
            }

            item.Remarks = dto.Remarks;
            item.CurrentDestination = dto.CurrentDestination;
            item.LastUpdated = DateTime.UtcNow;

            if (dto.Photo != null)
            {
                DeletePhoto(item.PhotoUrl); // Delete the old photo
                item.PhotoUrl = await SavePhoto(dto.Photo); // Save the new one
            }
            else if (dto.DeletePhoto == true)
            {
                DeletePhoto(item.PhotoUrl);
                item.PhotoUrl = null;
            }

            await _context.SaveChangesAsync();

            return NoContent();
        }


        // PUT: api/Items/{id}/outbound
        [HttpPut("{id}/outbound")]
        public async Task<ActionResult<Item>> Outbound(Guid id, [FromBody] UpdateItemRequest request)
        {
            return await UpdateItemStatus(id, ItemStatus.LoanedOut, AuditAction.Outbound, request.Destination);
        }

        // PUT: api/Items/{id}/check
        [HttpPut("{id}/check")]
        public async Task<ActionResult<Item>> Check(Guid id)
        {
            return await UpdateItemStatus(id, ItemStatus.InStock, AuditAction.Check, null);
        }

        // PUT: api/Items/{id}/return
        [HttpPut("{id}/return")]
        public async Task<ActionResult<Item>> Return(Guid id)
        {
            return await UpdateItemStatus(id, ItemStatus.InStock, AuditAction.Return, null);
        }

        // PUT: api/Items/{id}/dispose
        [HttpPut("{id}/dispose")]
        public async Task<ActionResult<Item>> Dispose(Guid id, [FromBody] UpdateItemRequest request)
        {
            return await UpdateItemStatus(id, ItemStatus.Disposed, AuditAction.Dispose, request.Destination);
        }

        // POST: api/Items/update-status/batch
        [HttpPost("update-status/batch")]
        public async Task<IActionResult> UpdateStatusBatch([FromBody] UpdateStatusBatchRequest request)
        {
            if (request.ItemIds == null || !request.ItemIds.Any())
            {
                return BadRequest("No item IDs provided.");
            }

            var itemsToUpdate = await _context.Items
                .Where(i => request.ItemIds.Contains(i.Id))
                .Include(i => i.ItemDefinition)
                .Include(i => i.Warehouse)
                .ToListAsync();

            if (itemsToUpdate.Count != request.ItemIds.Length)
            {
                return NotFound("One or more items were not found.");
            }

            foreach (var item in itemsToUpdate)
            {
                item.Status = request.Status;
                item.LastUpdated = DateTime.UtcNow;
                _context.Entry(item).State = EntityState.Modified;
                await LogAudit(item, AuditAction.Check, item.ItemDefinition.Name, item.Warehouse.Name, "Marked as Suspected Missing");
            }

            await _context.SaveChangesAsync();

            return Ok(new { message = $"{itemsToUpdate.Count} items updated to {request.Status}." });
        }

        // PUT: api/Items/{id}/transfer
        [HttpPut("{id}/transfer")]
        public async Task<ActionResult<Item>> TransferWarehouse(Guid id, [FromBody] TransferWarehouseRequest request)
        {
            var item = await _context.Items
                .Include(i => i.ItemDefinition)
                .Include(i => i.Warehouse)
                .FirstOrDefaultAsync(i => i.Id == id);

            if (item == null)
            {
                return NotFound("Item not found.");
            }

            var newWarehouse = await _context.Warehouses.FindAsync(request.NewWarehouseId);
            if (newWarehouse == null)
            {
                return BadRequest("Target warehouse not found.");
            }

            // 记录旧库房信息
            var oldWarehouseName = item.Warehouse?.Name ?? "Unknown";
            var oldWarehouseId = item.WarehouseId;

            // 更新库房
            item.WarehouseId = request.NewWarehouseId;
            item.LastUpdated = DateTime.UtcNow;
            
            // 如果需要，可以更新备注
            if (!string.IsNullOrEmpty(request.Remarks))
            {
                item.Remarks = request.Remarks;
            }

            _context.Entry(item).State = EntityState.Modified;

            // 记录审计日志 - 从旧库房转移到新库房
            await LogAudit(
                item, 
                AuditAction.Transfer, 
                item.ItemDefinition?.Name ?? "Unknown Item", 
                newWarehouse.Name, 
                $"From: {oldWarehouseName} (ID: {oldWarehouseId})"
            );

            await _context.SaveChangesAsync();

            // 重新加载关联数据以返回完整信息
            await _context.Entry(item).Reference(i => i.Warehouse).LoadAsync();

            return Ok(item);
        }

        private async Task<ActionResult<Item>> UpdateItemStatus(Guid id, ItemStatus newStatus, AuditAction action, string? destination)
        {
            var item = await _context.Items
                .Include(i => i.ItemDefinition)
                .Include(i => i.Warehouse)
                .FirstOrDefaultAsync(i => i.Id == id);

            if (item == null) return NotFound();

            item.Status = newStatus;
            item.LastUpdated = DateTime.UtcNow;

            if (action == AuditAction.Outbound || action == AuditAction.Dispose)
            {
                item.CurrentDestination = destination;
            }
            else if (action == AuditAction.Return)
            {
                item.CurrentDestination = null;
            }
            
            _context.Entry(item).State = EntityState.Modified;

            await LogAudit(item, action, item.ItemDefinition.Name, item.Warehouse.Name, destination);
            
            await _context.SaveChangesAsync();

            return Ok(item);
        }

        private async Task LogAudit(Item item, AuditAction action, string itemName, string warehouseName, string? destination)
        {
            var userName = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "Unknown User";
            
            var auditLog = new AuditLog
            {
                Timestamp = DateTime.UtcNow,
                Action = action,
                ItemId = item.Id,
                ItemShortId = item.ShortId,
                ItemName = itemName,
                WarehouseId = item.WarehouseId,
                WarehouseName = warehouseName,
                User = userName,
                Destination = destination
            };

            _context.AuditLogs.Add(auditLog);
        }

        private async Task<string> SavePhoto(IFormFile photo)
        {
            var uploadsFolderPath = Path.Combine(_env.ContentRootPath, "photos");
            if (!Directory.Exists(uploadsFolderPath))
            {
                Directory.CreateDirectory(uploadsFolderPath);
            }

            var uniqueFileName = Guid.NewGuid().ToString() + ".webp";
            var filePath = Path.Combine(uploadsFolderPath, uniqueFileName);

            using var image = await Image.LoadAsync(photo.OpenReadStream());
            
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(800, 800),
                Mode = ResizeMode.Max
            }));

            await image.SaveAsync(filePath, new WebpEncoder { Quality = 80 });

            return $"/photos/{uniqueFileName}";
        }

        private void DeletePhoto(string? photoUrl)
        {
            if (string.IsNullOrEmpty(photoUrl)) return;

            var fileName = Path.GetFileName(photoUrl);
            var filePath = Path.Combine(_env.ContentRootPath, "photos", fileName);

            if (System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
            }
        }
    }
}

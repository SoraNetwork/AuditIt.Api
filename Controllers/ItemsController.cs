using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AuditIt.Api.Data;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
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
        [RequirePermission(PermissionCodes.ItemView)]
        public async Task<ActionResult<IEnumerable<ItemDto>>> GetItems([FromQuery] ItemQueryParameters queryParameters)
        {
            var query = _context.Items
                .Include(i => i.ItemDefinition)
                    .ThenInclude(d => d!.Category)
                .Include(i => i.Warehouse)
                .AsQueryable();

            if (queryParameters.WarehouseId.HasValue)
            {
                query = query.Where(i => i.WarehouseId == queryParameters.WarehouseId.Value);
            }
            if (queryParameters.CategoryId.HasValue)
            {
                query = query.Where(i => i.ItemDefinition != null
                    && i.ItemDefinition.CategoryId == queryParameters.CategoryId.Value);
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
            if (!string.IsNullOrEmpty(queryParameters.SerialNumber))
            {
                query = query.Where(i => i.SerialNumber == queryParameters.SerialNumber);
            }
            if (!string.IsNullOrEmpty(queryParameters.Search))
            {
                var search = queryParameters.Search.Trim();
                query = query.Where(i =>
                    i.ShortId.Contains(search) ||
                    (i.ItemDefinition != null && i.ItemDefinition.Name.Contains(search)) ||
                    (i.SerialNumber != null && i.SerialNumber.Contains(search)));
            }

            return await query.OrderByDescending(i => i.LastUpdated).Select(i => ToItemDto(i)).ToListAsync();
        }

        private static ItemDto ToItemDto(Item i)
        {
            var ownerUserNames = ItemOwnerSnapshot.Split(i.OwnerUserNamesSnapshot).ToList();
            return new()
            {
                Id = i.Id.ToString(),
                ShortId = i.ShortId,
                SerialNumber = i.SerialNumber,
                Status = i.Status,
                WarehouseId = i.WarehouseId,
                OwnerUserNames = ownerUserNames,
                OwnerUserName = ownerUserNames.Count == 0 ? null : string.Join(",", ownerUserNames),
                ItemDefinitionId = i.ItemDefinitionId,
                CategoryId = i.ItemDefinition?.CategoryId,
                CategoryName = i.ItemDefinition?.Category?.Name ?? string.Empty,
                Remarks = i.Remarks,
                PhotoUrl = i.PhotoUrl,
                ItemValue = i.ItemValue,
                LastUpdated = i.LastUpdated.ToString("O"),
                EntryDate = i.EntryDate.ToString("O"),
                CurrentDestination = i.CurrentDestination,
                ItemDefinitionName = i.ItemDefinition?.Name ?? string.Empty,
                WarehouseName = i.Warehouse?.Name ?? string.Empty,
            };
        }

        // POST: api/Items/batch
        [HttpPost("batch")]
        [RequirePermission(PermissionCodes.ItemView)]
        public async Task<ActionResult<IEnumerable<ItemDto>>> GetItemsBatch([FromBody] Guid[] ids)
        {
            if (ids == null || !ids.Any())
            {
                return BadRequest("No item IDs provided.");
            }
            var items = await _context.Items
                .Where(i => ids.Contains(i.Id))
                .Include(i => i.ItemDefinition)
                    .ThenInclude(d => d!.Category)
                .Include(i => i.Warehouse)
                .ToListAsync();
            return Ok(items.Select(ToItemDto));
        }

        [HttpGet("{id:guid}/availability")]
        [RequirePermission(PermissionCodes.ItemView)]
        public async Task<ActionResult<ItemAvailabilityCalendarDto>> GetAvailability(Guid id, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
        {
            var item = await _context.Items
                .Include(i => i.ItemDefinition)
                .Include(i => i.Warehouse)
                .FirstOrDefaultAsync(i => i.Id == id);

            if (item == null)
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

            var itemDefinitionId = item.ItemDefinitionId;
            var rentals = await _context.Rentals
                .Include(r => r.Renter)
                .Include(r => r.Items)
                .Include(r => r.Shipments)
                .Where(r => r.Status != RentalStatus.Cancelled)
                .Where(r => r.Items.Any(ri => ri.ItemId == id && (ri.ReturnedAt == null || ri.ReturnedAt > r.StartDate)))
                .Where(r => (r.ExpectedShipDate <= rangeEnd.AddDays(1)
                    || r.StartDate <= rangeEnd.AddDays(1)
                    || r.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound && s.ShippedAt <= rangeEnd.AddDays(1)))
                    && (r.ExpectedEndDate >= rangeStart.AddDays(-1)
                        || (r.HasRenewalIntent
                            && r.RenewalIntentEndDate.HasValue
                            && r.RenewalIntentEndDate.Value >= rangeStart.AddDays(-1))
                        || r.ActualEndDate >= rangeStart.AddDays(-1)
                        || r.Items.Any(ri => ri.ReturnedAt >= rangeStart.AddDays(-1))
                        || ((r.RenewedFromRentalId != null
                                || r.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound))
                            && r.Items.Any(ri => ri.ItemId == id && ri.ReturnedAt == null))))
                .ToListAsync();

            var uncertainRentals = await _context.Rentals
                .Include(r => r.Renter)
                .Include(r => r.Items)
                .Include(r => r.Shipments)
                .Where(r => r.Status == RentalStatus.Pending)
                .Where(r => r.Items.Any(ri => ri.ItemId == null && ri.ItemDefinitionId == itemDefinitionId && ri.ReturnedAt == null))
                .Where(r => r.ExpectedShipDate <= rangeEnd.AddDays(1)
                    && (r.ExpectedEndDate >= rangeStart.AddDays(-1)
                        || (r.HasRenewalIntent
                            && r.RenewalIntentEndDate.HasValue
                            && r.RenewalIntentEndDate.Value >= rangeStart.AddDays(-1))))
                .ToListAsync();

            var busy = rentals
                .Where(r => RentalDateRules.Overlaps(
                    OccupancyStartDate(r),
                    RentalDateRules.OccupancyEndDate(r, rangeEnd),
                    rangeStart,
                    rangeEnd))
                .SelectMany(r => r.Items
                    .Where(ri => ri.ItemId == id && (ri.ReturnedAt == null || ri.ReturnedAt > r.StartDate))
                    .SelectMany(ri => BuildBusyPeriods(r, ri, rangeStart, rangeEnd)))
                .ToList();

            var uncertainBusy = uncertainRentals
                .Where(r => RentalDateRules.Overlaps(r.ExpectedShipDate, r.ExpectedEndDate, rangeStart, rangeEnd))
                .SelectMany(r => r.Items
                    .Where(ri => ri.ItemId == null && ri.ItemDefinitionId == itemDefinitionId && ri.ReturnedAt == null)
                    .Select(ri =>
                    {
                        var startAt = RentalDateRules.OccupancyStartDate(r);
                        var endAt = RentalDateRules.EndOfBusinessDay(RentalDateRules.EffectiveExpectedEndDate(r));
                        return new ItemBusyPeriodDto
                        {
                            RentalId = r.Id,
                            RentalNumber = $"{r.RentalNumber} (待定)",
                            RentalStatus = r.Status,
                            RenterId = r.RenterId,
                            RenterName = r.Renter?.Name,
                            StartAt = startAt < rangeStart ? rangeStart : startAt,
                            EndAt = endAt > rangeEnd ? rangeEnd : endAt,
                            IsOpen = true,
                            IsUncertain = true,
                            HasRenewalIntent = r.HasRenewalIntent,
                            RenewalIntentEndDate = r.HasRenewalIntent && r.RenewalIntentEndDate.HasValue
                                ? RentalDateRules.ToBusinessDate(r.RenewalIntentEndDate.Value)
                                : null,
                            OccupancyStatus = ItemOccupancyStatus.Scheduled
                        };
                    }))
                .ToList();

            var outboundAt = await _context.AuditLogs
                .Where(log => log.ItemId == id && log.Action == AuditAction.Outbound)
                .OrderByDescending(log => log.Timestamp)
                .Select(log => (DateTime?)log.Timestamp)
                .FirstOrDefaultAsync();
            var hasSpecificRentalBusy = busy.Any(period => !period.IsManualLoan && !period.IsUncertain);
            var isManualLoan = item.Status == ItemStatus.LoanedOut
                && outboundAt.HasValue
                && (item.CurrentDestination == null || !item.CurrentDestination.StartsWith("租赁 ", StringComparison.Ordinal))
                && !hasSpecificRentalBusy;

            if (isManualLoan)
            {
                var loanStart = RentalDateRules.ToBusinessDate(outboundAt ?? item.LastUpdated);

                if (loanStart <= rangeEnd)
                {
                    busy.Add(new ItemBusyPeriodDto
                    {
                        RentalId = Guid.Empty,
                        RentalNumber = "普通借出",
                        RentalStatus = RentalStatus.Active,
                        StartAt = loanStart < rangeStart ? rangeStart : loanStart,
                        EndAt = rangeEnd,
                        IsOpen = true,
                        IsUncertain = false,
                        IsManualLoan = true,
                        OccupancyStatus = ItemOccupancyStatus.Scheduled
                    });
                }
            }

            busy.AddRange(uncertainBusy);
            busy = busy
                .Where(p => p.EndAt >= rangeStart && p.StartAt <= rangeEnd)
                .OrderBy(p => p.StartAt)
                .ThenBy(p => p.EndAt)
                .ToList();

            return Ok(new ItemAvailabilityCalendarDto
            {
                Item = ToItemDto(item),
                From = rangeStart,
                To = rangeEnd,
                BusyPeriods = busy,
                FreePeriods = BuildFreePeriods(rangeStart, rangeEnd, busy)
            });
        }

        // POST: api/Items/create
        [HttpPost("create")]
        [Consumes("multipart/form-data")]
        [RequirePermission(PermissionCodes.ItemCreate)]
        public async Task<ActionResult<ItemDto>> CreateItem([FromForm] CreateItemDto dto)
        {
            var itemDefinition = await _context.ItemDefinitions.FindAsync(dto.ItemDefinitionId);
            if (itemDefinition == null) return BadRequest("Item definition not found.");

            var warehouse = await _context.Warehouses.FindAsync(dto.WarehouseId);
            if (warehouse == null) return BadRequest("Warehouse not found.");

            if (!string.IsNullOrWhiteSpace(dto.SerialNumber))
            {
                var dup = await _context.Items.AnyAsync(i => i.SerialNumber == dto.SerialNumber);
                if (dup) return Conflict($"SN {dto.SerialNumber} 已存在。");
            }

            string? photoUrl = null;
            if (dto.Photo != null)
            {
                photoUrl = await SavePhoto(dto.Photo);
            }

            var ownerUserNamesSnapshot = ResolveOwnerUserNamesSnapshot(dto.OwnerUserNames, defaultToCurrentUser: true);

            var newItemId = Guid.NewGuid();
            var shortId = !string.IsNullOrEmpty(dto.ShortId)
                ? dto.ShortId
                : newItemId.ToString().Substring(0, 8).ToUpper();

            var item = new Item
            {
                Id = newItemId,
                ShortId = shortId,
                SerialNumber = string.IsNullOrWhiteSpace(dto.SerialNumber) ? null : dto.SerialNumber,
                ItemDefinitionId = dto.ItemDefinitionId,
                WarehouseId = dto.WarehouseId,
                OwnerUserNamesSnapshot = ownerUserNamesSnapshot,
                Remarks = dto.Remarks,
                PhotoUrl = photoUrl,
                Status = ItemStatus.InStock,
                ItemValue = dto.ItemValue,
                EntryDate = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow
            };

            _context.Items.Add(item);
            await LogAudit(item, AuditAction.Inbound, itemDefinition.Name, warehouse.Name, "Initial creation");
            await _context.SaveChangesAsync();

            await _context.Entry(item).Reference(i => i.ItemDefinition).LoadAsync();
            await _context.Entry(item).Reference(i => i.Warehouse).LoadAsync();

            return CreatedAtAction(nameof(GetItems), new { id = item.Id }, ToItemDto(item));
        }

	// POST: api/Items/create/batch
        [HttpPost("create/batch")]
        [RequirePermission(PermissionCodes.ItemCreate)]
        public async Task<IActionResult> PostItems([FromBody] CreateItemsDto dto)
        {
            var itemDefinition = await _context.ItemDefinitions.FindAsync(dto.ItemDefinitionId);
            if (itemDefinition == null) return BadRequest("Item definition not found.");
            var warehouse = await _context.Warehouses.FindAsync(dto.WarehouseId);
            if (warehouse == null) return BadRequest("Warehouse not found.");
            var snList = dto.Items.Where(i => !string.IsNullOrWhiteSpace(i.SerialNumber))
                                   .Select(i => i.SerialNumber!).ToList();
            if (snList.Count != snList.Distinct().Count())
                return BadRequest("批量请求中存在重复 SerialNumber。");
            if (snList.Count > 0)
            {
                var taken = await _context.Items.Where(i => snList.Contains(i.SerialNumber!))
                    .Select(i => i.SerialNumber).ToListAsync();
                if (taken.Count > 0) return Conflict($"以下 SN 已存在：{string.Join(", ", taken)}");
            }

            var ownerUserNamesSnapshot = ResolveOwnerUserNamesSnapshot(dto.OwnerUserNames, defaultToCurrentUser: true);

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
                    SerialNumber = string.IsNullOrWhiteSpace(itemDto.SerialNumber) ? null : itemDto.SerialNumber,
                    ItemDefinitionId = dto.ItemDefinitionId,
                    WarehouseId = dto.WarehouseId,
                    OwnerUserNamesSnapshot = ownerUserNamesSnapshot,
                    Remarks = itemDto.Remarks,
                    Status = ItemStatus.InStock,
                    ItemValue = itemDto.ItemValue,
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
        [RequirePermission(PermissionCodes.ItemUpdate)]
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

            if (dto.SerialNumber != null)
            {
                var normalized = string.IsNullOrWhiteSpace(dto.SerialNumber) ? null : dto.SerialNumber;
                if (normalized != null && normalized != item.SerialNumber)
                {
                    var dup = await _context.Items.AnyAsync(i => i.Id != id && i.SerialNumber == normalized);
                    if (dup) return Conflict($"SN {normalized} 已存在。");
                }
                item.SerialNumber = normalized;
            }

            item.Remarks = dto.Remarks;
            item.CurrentDestination = dto.CurrentDestination;
            item.ItemValue = dto.ItemValue;
            item.LastUpdated = DateTime.UtcNow;

            if (dto.ClearOwnerUser == true)
            {
                item.OwnerUserNamesSnapshot = null;
            }
            else if (dto.OwnerUserNames != null)
            {
                item.OwnerUserNamesSnapshot = ResolveOwnerUserNamesSnapshot(dto.OwnerUserNames, defaultToCurrentUser: false);
            }

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
        [RequirePermission(PermissionCodes.ItemUpdate)]
        public async Task<ActionResult<ItemDto>> Outbound(Guid id, [FromBody] UpdateItemRequest request)
        {
            return await UpdateItemStatus(id, ItemStatus.LoanedOut, AuditAction.Outbound, request.Destination);
        }

        // PUT: api/Items/{id}/check
        [HttpPut("{id}/check")]
        [RequirePermission(PermissionCodes.ItemUpdate)]
        public async Task<ActionResult<ItemDto>> Check(Guid id)
        {
            return await UpdateItemStatus(id, ItemStatus.InStock, AuditAction.Check, null);
        }

        // PUT: api/Items/{id}/return
        [HttpPut("{id}/return")]
        [RequirePermission(PermissionCodes.ItemUpdate)]
        public async Task<ActionResult<ItemDto>> Return(Guid id)
        {
            return await UpdateItemStatus(id, ItemStatus.InStock, AuditAction.Return, null);
        }

        // PUT: api/Items/{id}/dispose
        [HttpPut("{id}/dispose")]
        [RequirePermission(PermissionCodes.ItemDelete)]
        public async Task<ActionResult<ItemDto>> Dispose(Guid id, [FromBody] UpdateItemRequest request)
        {
            return await UpdateItemStatus(id, ItemStatus.Disposed, AuditAction.Dispose, request.Destination, request.PermanentlyHidden);
        }

        [HttpPut("{id}/hide-permanently")]
        [RequirePermission(PermissionCodes.ItemDelete)]
        public async Task<IActionResult> HidePermanently(Guid id)
        {
            var item = await _context.Items
                .IgnoreQueryFilters()
                .Include(i => i.ItemDefinition)
                .Include(i => i.Warehouse)
                .FirstOrDefaultAsync(i => i.Id == id);

            if (item == null || item.IsDeleted)
            {
                return NotFound();
            }

            if (item.Status != ItemStatus.Disposed)
            {
                return BadRequest("只有已处置物品可以转为永远不可见。");
            }

            MarkItemDeleted(item);
            await LogAudit(item, AuditAction.Dispose, item.ItemDefinition?.Name ?? "Unknown Item", item.Warehouse?.Name ?? "Unknown Warehouse", "转为永远不可见");
            await _context.SaveChangesAsync();

            return NoContent();
        }

        // POST: api/Items/update-status/batch
        [HttpPost("update-status/batch")]
        [RequirePermission(PermissionCodes.ItemUpdate)]
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
        [RequirePermission(PermissionCodes.ItemTransfer)]
        public async Task<ActionResult<ItemDto>> TransferWarehouse(Guid id, [FromBody] TransferWarehouseRequest request)
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

            return Ok(ToItemDto(item));
        }

        private async Task<ActionResult<ItemDto>> UpdateItemStatus(Guid id, ItemStatus newStatus, AuditAction action, string? destination, bool permanentlyHidden = false)
        {
            var item = await _context.Items
                .Include(i => i.ItemDefinition)
                .Include(i => i.Warehouse)
                .FirstOrDefaultAsync(i => i.Id == id);

            if (item == null) return NotFound();
            if (permanentlyHidden && action != AuditAction.Dispose)
            {
                return BadRequest("只有处置物品时可以设置永远不可见。");
            }

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

            if (permanentlyHidden)
            {
                MarkItemDeleted(item);
            }
            
            await _context.SaveChangesAsync();

            if (permanentlyHidden)
            {
                return NoContent();
            }

            return Ok(ToItemDto(item));
        }

        private void MarkItemDeleted(Item item)
        {
            item.IsDeleted = true;
            item.DeletedAt = DateTime.UtcNow;
            item.DeletedBy = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            item.LastUpdated = DateTime.UtcNow;
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

        private string? ResolveOwnerUserNamesSnapshot(IEnumerable<string>? requestedOwnerNames, bool defaultToCurrentUser)
        {
            var snapshot = ItemOwnerSnapshot.Normalize(requestedOwnerNames);
            if (!string.IsNullOrWhiteSpace(snapshot))
            {
                return snapshot;
            }

            if (!defaultToCurrentUser)
            {
                return null;
            }

            var userName = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return ItemOwnerSnapshot.Normalize(string.IsNullOrWhiteSpace(userName) ? null : [userName]);
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

        private static List<ItemFreePeriodDto> BuildFreePeriods(
            DateTime rangeStart,
            DateTime rangeEnd,
            IReadOnlyList<ItemBusyPeriodDto> busyPeriods)
        {
            var free = new List<ItemFreePeriodDto>();
            var cursor = rangeStart;

            foreach (var period in busyPeriods
                .OrderBy(p => p.StartAt)
                .ThenBy(p => p.EndAt))
            {
                if (period.EndAt < cursor)
                {
                    continue;
                }

                if (period.StartAt > cursor)
                {
                    free.Add(new ItemFreePeriodDto
                    {
                        StartAt = cursor,
                        EndAt = period.StartAt.AddTicks(-1)
                    });
                }

                if (period.EndAt > cursor)
                {
                    cursor = period.EndAt.AddTicks(1);
                }
            }

            if (cursor <= rangeEnd)
            {
                free.Add(new ItemFreePeriodDto
                {
                    StartAt = cursor,
                    EndAt = rangeEnd
                });
            }

            return free;
        }

        private static DateTime OccupancyStartDate(Rental rental) =>
            RentalDateRules.OccupancyStartDate(rental);

        private static IEnumerable<ItemBusyPeriodDto> BuildBusyPeriods(
            Rental rental,
            RentalItem rentalItem,
            DateTime rangeStart,
            DateTime rangeEnd)
        {
            var startAt = OccupancyStartDate(rental);
            var endDay = RentalDateRules.OccupancyEndDate(rental, rentalItem, rangeEnd);
            var endAt = RentalDateRules.EndOfBusinessDay(endDay);
            var expectedEndDay = RentalDateRules.ToBusinessDate(rental.ExpectedEndDate);
            var effectiveExpectedEndDay = RentalDateRules.EffectiveExpectedEndDate(rental);
            var expectedEndAt = RentalDateRules.EndOfBusinessDay(expectedEndDay);
            var renewalIntentEndAt = RentalDateRules.EndOfBusinessDay(effectiveExpectedEndDay);
            var returningStart = effectiveExpectedEndDay.AddDays(1);
            var isOpen = rentalItem.ReturnedAt == null
                && rental.Status != RentalStatus.Returned
                && rental.Status != RentalStatus.Renewed;

            if (startAt <= expectedEndAt && expectedEndAt >= rangeStart)
            {
                var scheduledEnd = endAt < expectedEndAt ? endAt : expectedEndAt;
                var period = BuildBusyPeriod(
                    rental,
                    startAt < rangeStart ? rangeStart : startAt,
                    scheduledEnd > rangeEnd ? rangeEnd : scheduledEnd,
                    isOpen,
                    ItemOccupancyStatus.Scheduled);
                if (period.EndAt >= period.StartAt)
                {
                    yield return period;
                }
            }

            if (rental.HasRenewalIntent
                && rental.RenewalIntentEndDate.HasValue
                && effectiveExpectedEndDay > expectedEndDay
                && endAt >= expectedEndDay.AddDays(1)
                && renewalIntentEndAt >= rangeStart)
            {
                var renewalIntentPeriodStart = new[] { startAt, expectedEndDay.AddDays(1), rangeStart }.Max();
                var renewalIntentPeriodEnd = new[] { endAt, renewalIntentEndAt, rangeEnd }.Min();
                var period = BuildBusyPeriod(
                    rental,
                    renewalIntentPeriodStart,
                    renewalIntentPeriodEnd,
                    isOpen,
                    ItemOccupancyStatus.RenewalIntent);
                if (period.EndAt >= period.StartAt)
                {
                    yield return period;
                }
            }

            if (endAt >= returningStart && endAt >= rangeStart)
            {
                var returningPeriodStart = new[] { startAt, returningStart, rangeStart }.Max();
                var returningPeriodEnd = endAt > rangeEnd ? rangeEnd : endAt;
                var period = BuildBusyPeriod(
                    rental,
                    returningPeriodStart,
                    returningPeriodEnd,
                    isOpen,
                    ItemOccupancyStatus.Returning);
                if (period.EndAt >= period.StartAt)
                {
                    yield return period;
                }
            }
        }

        private static ItemBusyPeriodDto BuildBusyPeriod(
            Rental rental,
            DateTime startAt,
            DateTime endAt,
            bool isOpen,
            ItemOccupancyStatus status) => new()
            {
                RentalId = rental.Id,
                RentalNumber = rental.RentalNumber,
                RentalStatus = rental.Status,
                RenterId = rental.RenterId,
                RenterName = rental.Renter?.Name,
                StartAt = startAt,
                EndAt = endAt,
                IsOpen = isOpen,
                IsUncertain = false,
                HasRenewalIntent = rental.HasRenewalIntent,
                RenewalIntentEndDate = rental.HasRenewalIntent && rental.RenewalIntentEndDate.HasValue
                    ? RentalDateRules.ToBusinessDate(rental.RenewalIntentEndDate.Value)
                    : null,
                OccupancyStatus = status
            };

        private static bool HasRentalStarted(Rental rental) =>
            RentalDateRules.HasRentalStarted(rental);

        private static bool ShouldUseReturnBuffer(Rental rental) =>
            RentalDateRules.ShouldUseReturnBuffer(rental);
    }
}

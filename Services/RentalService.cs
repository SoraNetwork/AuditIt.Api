using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AuditIt.Api.Services
{
    public class RentalService : IRentalService
    {
        private readonly ApplicationDbContext _context;
        private readonly IRenterService _renterService;
        private readonly IIdentityService _identityService;
        private readonly IEnumerable<INotificationChannel> _notificationChannels;

        public RentalService(
            ApplicationDbContext context,
            IRenterService renterService,
            IIdentityService identityService,
            IEnumerable<INotificationChannel> notificationChannels)
        {
            _context = context;
            _renterService = renterService;
            _identityService = identityService;
            _notificationChannels = notificationChannels;
        }

        public async Task<(IEnumerable<RentalDto> items, int total)> ListAsync(RentalQueryParameters query)
        {
            var q = _context.Rentals
                .Include(r => r.Renter)
                .Include(r => r.Items)
                .Include(r => r.Shipments)
                    .ThenInclude(s => s.OriginWarehouse)
                .AsQueryable();

            if (query.Status.HasValue)
            {
                q = q.Where(r => r.Status == query.Status.Value);
            }

            if (query.RenterId.HasValue)
            {
                q = q.Where(r => r.RenterId == query.RenterId.Value);
            }

            if (!string.IsNullOrWhiteSpace(query.RentalNumber))
            {
                var rentalNumber = query.RentalNumber.Trim();
                q = q.Where(r => r.RentalNumber.Contains(rentalNumber));
            }

            if (query.StartDateFrom.HasValue)
            {
                q = q.Where(r => r.StartDate >= query.StartDateFrom.Value);
            }

            if (query.StartDateTo.HasValue)
            {
                q = q.Where(r => r.StartDate <= query.StartDateTo.Value);
            }

            var total = await q.CountAsync();

            var page = Math.Max(1, query.Page);
            var pageSize = Math.Clamp(query.PageSize, 1, 200);

            var rows = await q.OrderByDescending(r => r.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return (rows.Select(ToDto), total);
        }

        public async Task<RentalDto?> GetByIdAsync(Guid id)
        {
            var rental = await _context.Rentals
                .Include(r => r.Renter)
                .Include(r => r.Items)
                .Include(r => r.Shipments)
                    .ThenInclude(s => s.OriginWarehouse)
                .FirstOrDefaultAsync(r => r.Id == id);

            return rental == null ? null : ToDto(rental);
        }

        public async Task<CreateRentalResult> CreateAsync(CreateRentalDto dto, string? currentUser)
        {
            if (dto.ItemIds == null || dto.ItemIds.Count == 0)
            {
                return new CreateRentalResult
                {
                    Error = "至少选择一件商品。"
                };
            }

            var distinctItemIds = new List<Guid>();
            foreach (var rawItemId in dto.ItemIds.Distinct())
            {
                if (!Guid.TryParse(rawItemId, out var parsedItemId))
                {
                    return new CreateRentalResult
                    {
                        Error = "存在无效的商品 ID。"
                    };
                }

                distinctItemIds.Add(parsedItemId);
            }

            var now = DateTime.UtcNow;
            var startDate = dto.StartDate ?? now;
            if (dto.ExpectedEndDate < startDate)
            {
                return new CreateRentalResult
                {
                    Error = "预计结束时间不能早于开始时间。"
                };
            }

            var items = await _context.Items
                .Include(i => i.ItemDefinition)
                .Include(i => i.Warehouse)
                .Include(i => i.Listings)
                .Where(i => distinctItemIds.Contains(i.Id))
                .ToListAsync();

            if (items.Count != distinctItemIds.Count)
            {
                return new CreateRentalResult
                {
                    Error = "部分商品不存在。"
                };
            }

            var disposedItems = items
                .Where(i => i.Status == ItemStatus.Disposed)
                .Select(i => i.ShortId)
                .ToList();
            if (disposedItems.Count > 0)
            {
                return new CreateRentalResult
                {
                    Error = $"以下商品已处置，不能创建租赁：{string.Join("，", disposedItems)}"
                };
            }

            var conflict = await ValidateCreateConflictsAsync(distinctItemIds, startDate, dto.ExpectedEndDate);
            if (conflict != null)
            {
                return new CreateRentalResult
                {
                    Conflict = conflict
                };
            }

            var renter = await _renterService.ResolveOrUpsertAsync(dto.Renter, currentUser);
            var rentalId = Guid.NewGuid();
            var rentalNumber = await GenerateRentalNumberAsync(now);

            var rental = new Rental
            {
                Id = rentalId,
                RentalNumber = rentalNumber,
                RenterId = renter.Id,
                Renter = renter,
                Status = RentalStatus.Pending,
                StartDate = startDate,
                ExpectedEndDate = dto.ExpectedEndDate,
                TotalPrice = dto.TotalPrice,
                Deposit = dto.Deposit,
                OtherFee = dto.OtherFee,
                ShippingAddress = NormalizeNullableText(dto.ShippingAddress) ?? renter.DefaultAddress,
                PlatformOrderNo = NormalizeNullableText(dto.PlatformOrderNo),
                Notes = NormalizeNullableText(dto.Notes),
                CreatedAt = now,
                CreatedBy = currentUser,
                UpdatedAt = now,
                UpdatedBy = currentUser,
                AssignedTo = NormalizeAssignedTo(dto.AssignedTo)
            };
            _context.Rentals.Add(rental);

            foreach (var item in items)
            {
                var listingRemarks = string.Join("; ", item.Listings
                    .Where(l => l.Status == ListingStatus.Listed)
                    .Select(l => $"{l.Platform}: {l.Remarks ?? "无备注"}"));

                _context.RentalItems.Add(new RentalItem
                {
                    RentalId = rentalId,
                    ItemId = item.Id,
                    ItemShortIdSnapshot = item.ShortId,
                    ItemNameSnapshot = item.ItemDefinition?.Name ?? string.Empty,
                    ListingRemarksSnapshot = string.IsNullOrWhiteSpace(listingRemarks) ? null : listingRemarks
                });

                LogAudit(item, AuditAction.RentalCreated, rentalNumber, currentUser);
            }

            await _context.SaveChangesAsync();

            await NotifyStatusChangeAsync(
                rental,
                "已创建",
                currentUser,
                $"租客：{renter.Name}，商品 {items.Count} 件");

            return new CreateRentalResult
            {
                Rental = await GetByIdAsync(rentalId)
            };
        }

        public async Task<(RentalDto? rental, string? error)> UpdateAsync(Guid id, UpdateRentalDto dto, string? currentUser)
        {
            var rental = await _context.Rentals
                .Include(r => r.Renter)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.ItemDefinition)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.Warehouse)
                .Include(r => r.Shipments)
                .FirstOrDefaultAsync(r => r.Id == id);

            if (rental == null)
            {
                return (null, "租赁单不存在。");
            }

            if (rental.Status == RentalStatus.Returned || rental.Status == RentalStatus.Cancelled)
            {
                return (null, "已结束的租赁单不可修改。");
            }

            var changes = new List<string>();
            var extended = false;
            var scheduleOrTargetChanged = false;
            var nextStartDate = dto.StartDate ?? rental.StartDate;
            var nextExpectedEndDate = dto.ExpectedEndDate ?? rental.ExpectedEndDate;

            if (nextExpectedEndDate < nextStartDate)
            {
                return (null, "预计结束时间不能早于开始时间。");
            }

            if ((dto.StartDate.HasValue && dto.StartDate.Value != rental.StartDate)
                || (dto.ExpectedEndDate.HasValue && dto.ExpectedEndDate.Value != rental.ExpectedEndDate))
            {
                var conflict = await ValidateCreateConflictsAsync(
                    rental.Items.Where(ri => ri.ReturnedAt == null).Select(ri => ri.ItemId).ToList(),
                    nextStartDate,
                    nextExpectedEndDate,
                    rental.Id);
                if (conflict != null)
                {
                    return (null, conflict.Message);
                }
            }

            if (dto.RenterId.HasValue && dto.RenterId.Value != rental.RenterId)
            {
                var nextRenter = await _context.Renters.FindAsync(dto.RenterId.Value);
                if (nextRenter == null)
                {
                    return (null, "租客不存在。");
                }

                changes.Add($"租客：{rental.Renter?.Name ?? rental.RenterId.ToString()} -> {nextRenter.Name}");
                rental.RenterId = nextRenter.Id;
                rental.Renter = nextRenter;
                scheduleOrTargetChanged = true;
            }

            if (dto.StartDate.HasValue && dto.StartDate.Value != rental.StartDate)
            {
                changes.Add($"开始：{rental.StartDate:yyyy-MM-dd} -> {dto.StartDate.Value:yyyy-MM-dd}");
                rental.StartDate = dto.StartDate.Value;
                scheduleOrTargetChanged = true;
            }

            if (dto.ExpectedEndDate.HasValue)
            {
                if (dto.ExpectedEndDate.Value != rental.ExpectedEndDate)
                {
                    extended = dto.ExpectedEndDate.Value > rental.ExpectedEndDate;
                    changes.Add($"预计结束：{rental.ExpectedEndDate:yyyy-MM-dd} -> {dto.ExpectedEndDate.Value:yyyy-MM-dd}");
                    rental.ExpectedEndDate = dto.ExpectedEndDate.Value;
                    scheduleOrTargetChanged = true;

                    if (rental.Status == RentalStatus.Overdue && rental.ExpectedEndDate > DateTime.UtcNow)
                    {
                        rental.Status = HasOutboundShipment(rental) ? RentalStatus.Active : RentalStatus.Pending;
                    }
                }
            }

            if (dto.TotalPrice.HasValue && dto.TotalPrice.Value != rental.TotalPrice)
            {
                changes.Add($"总价：{rental.TotalPrice:0.0} -> {dto.TotalPrice.Value:0.0}");
                rental.TotalPrice = dto.TotalPrice.Value;
            }

            if (dto.Deposit.HasValue && dto.Deposit.Value != rental.Deposit)
            {
                changes.Add($"押金：{rental.Deposit?.ToString("0.0") ?? "-"} -> {dto.Deposit.Value:0.0}");
                rental.Deposit = dto.Deposit.Value;
            }

            if (dto.OtherFee.HasValue && dto.OtherFee.Value != rental.OtherFee)
            {
                changes.Add($"其他费用：{rental.OtherFee:0.0} -> {dto.OtherFee.Value:0.0}");
                rental.OtherFee = dto.OtherFee.Value;
            }

            if (dto.ShippingAddress != null)
            {
                var shippingAddress = NormalizeNullableText(dto.ShippingAddress);
                if (shippingAddress != rental.ShippingAddress)
                {
                    changes.Add($"地址：{Truncate(rental.ShippingAddress)} -> {Truncate(shippingAddress)}");
                    rental.ShippingAddress = shippingAddress;
                }
            }

            if (dto.PlatformOrderNo != null)
            {
                var platformOrderNo = NormalizeNullableText(dto.PlatformOrderNo);
                if (platformOrderNo != rental.PlatformOrderNo)
                {
                    changes.Add($"平台订单号：{rental.PlatformOrderNo ?? "-"} -> {platformOrderNo ?? "-"}");
                    rental.PlatformOrderNo = platformOrderNo;
                }
            }

            if (dto.Notes != null)
            {
                var notes = NormalizeNullableText(dto.Notes);
                if (notes != rental.Notes)
                {
                    changes.Add($"备注：{Truncate(rental.Notes)} -> {Truncate(notes)}");
                    rental.Notes = notes;
                }
            }

            if (dto.AssignedTo != null)
            {
                var assignedTo = NormalizeAssignedTo(dto.AssignedTo);
                if (assignedTo != rental.AssignedTo)
                {
                    changes.Add($"负责人：{rental.AssignedTo ?? "-"} -> {assignedTo ?? "-"}");
                    rental.AssignedTo = assignedTo;
                    scheduleOrTargetChanged = true;
                }
            }

            if (changes.Count == 0)
            {
                return (await GetByIdAsync(id), null);
            }

            rental.UpdatedAt = DateTime.UtcNow;
            rental.UpdatedBy = currentUser;

            if (scheduleOrTargetChanged)
            {
                await DismissOpenRentalAutoRemindersAsync(rental.Id, currentUser);
            }

            var summary = string.Join("；", changes);
            foreach (var rentalItem in rental.Items)
            {
                if (rentalItem.Item == null)
                {
                    continue;
                }

                LogAudit(
                    rentalItem.Item,
                    extended ? AuditAction.RentalExtended : AuditAction.RentalUpdated,
                    rental.RentalNumber,
                    currentUser,
                    summary);
            }

            await _context.SaveChangesAsync();

            await NotifyStatusChangeAsync(rental, "信息已更新", currentUser, summary);

            return (await GetByIdAsync(id), null);
        }

        public async Task<(RentalDto? rental, string? error)> AddShipmentAsync(Guid rentalId, CreateShipmentDto dto, string? currentUser)
        {
            var rental = await _context.Rentals
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.ItemDefinition)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.Warehouse)
                .Include(r => r.Shipments)
                .FirstOrDefaultAsync(r => r.Id == rentalId);

            if (rental == null)
            {
                return (null, "租赁单不存在。");
            }

            if (rental.Status == RentalStatus.Cancelled || rental.Status == RentalStatus.Returned)
            {
                return (null, "租赁单已结束，不能继续登记物流。");
            }

            if (dto.Direction == ShipmentDirection.Inbound && !HasOutboundShipment(rental))
            {
                return (null, "租赁尚未发货，不能登记回货物流。");
            }

            var warehouse = await _context.Warehouses.FindAsync(dto.OriginWarehouseId);
            if (warehouse == null)
            {
                return (null, "发货仓库不存在。");
            }

            if (dto.Direction == ShipmentDirection.Outbound)
            {
                var blockingItems = await GetOtherShippedOpenItemsAsync(
                    rental.Id,
                    rental.Items.Where(ri => ri.ReturnedAt == null).Select(ri => ri.ItemId).ToList());

                if (blockingItems.Count > 0)
                {
                    return (null, $"以下商品仍存在未归还的已发货租赁，不能登记发货：{string.Join("，", blockingItems)}");
                }
            }

            var shippedAt = dto.ShippedAt ?? DateTime.UtcNow;
            var shipment = new RentalShipment
            {
                RentalId = rentalId,
                Direction = dto.Direction,
                OriginWarehouseId = dto.OriginWarehouseId,
                Carrier = dto.Carrier.Trim(),
                TrackingNumber = NormalizeNullableText(dto.TrackingNumber),
                ShippedAt = shippedAt,
                ShippingFee = dto.ShippingFee,
                Notes = NormalizeNullableText(dto.Notes),
                CreatedBy = currentUser
            };
            _context.RentalShipments.Add(shipment);

            var logisticsSummary = BuildShipmentSummary(dto.Carrier, shipment.TrackingNumber);
            if (dto.Direction == ShipmentDirection.Outbound)
            {
                rental.Status = rental.ExpectedEndDate < shippedAt ? RentalStatus.Overdue : RentalStatus.Active;

                foreach (var rentalItem in rental.Items.Where(ri => ri.ReturnedAt == null))
                {
                    if (rentalItem.Item == null)
                    {
                        continue;
                    }

                    rentalItem.Item.Status = ItemStatus.LoanedOut;
                    rentalItem.Item.CurrentDestination = $"租赁 {rental.RentalNumber}";
                    rentalItem.Item.LastUpdated = shippedAt;
                    LogAudit(rentalItem.Item, AuditAction.RentalShipped, rental.RentalNumber, currentUser, logisticsSummary);
                }
            }
            else
            {
                foreach (var rentalItem in rental.Items.Where(ri => ri.ReturnedAt == null))
                {
                    if (rentalItem.Item == null)
                    {
                        continue;
                    }

                    LogAudit(rentalItem.Item, AuditAction.RentalShipped, rental.RentalNumber, currentUser, $"回货 {logisticsSummary}");
                }
            }

            rental.UpdatedAt = DateTime.UtcNow;
            rental.UpdatedBy = currentUser;

            await _context.SaveChangesAsync();
            await _context.Entry(shipment).Reference(s => s.OriginWarehouse).LoadAsync();

            await NotifyStatusChangeAsync(
                rental,
                dto.Direction == ShipmentDirection.Outbound ? "已发货" : "已登记回货物流",
                currentUser,
                $"物流：{logisticsSummary}");

            return (await GetByIdAsync(rentalId), null);
        }

        public async Task<(RentalDto? rental, string? error)> MarkDeliveredAsync(Guid rentalId, int shipmentId, DeliverShipmentDto dto, string? currentUser)
        {
            var shipment = await _context.RentalShipments
                .Include(s => s.OriginWarehouse)
                .FirstOrDefaultAsync(s => s.Id == shipmentId && s.RentalId == rentalId);

            if (shipment == null)
            {
                return (null, "物流记录不存在。");
            }

            shipment.DeliveredAt = dto.DeliveredAt ?? DateTime.UtcNow;

            var rental = await _context.Rentals
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.ItemDefinition)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.Warehouse)
                .FirstAsync(r => r.Id == rentalId);

            foreach (var rentalItem in rental.Items.Where(ri => ri.ReturnedAt == null))
            {
                if (rentalItem.Item == null)
                {
                    continue;
                }

                LogAudit(rentalItem.Item, AuditAction.RentalDelivered, rental.RentalNumber, currentUser);
            }

            rental.UpdatedAt = DateTime.UtcNow;
            rental.UpdatedBy = currentUser;

            await _context.SaveChangesAsync();

            await NotifyStatusChangeAsync(
                rental,
                shipment.Direction == ShipmentDirection.Outbound ? "出库物流已签收" : "回库物流已签收",
                currentUser);

            return (await GetByIdAsync(rentalId), null);
        }

        public async Task<(RentalDto? rental, string? error)> ReturnAsync(Guid rentalId, ReturnRentalDto dto, string? currentUser)
        {
            var rental = await _context.Rentals
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.ItemDefinition)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.Warehouse)
                .Include(r => r.Shipments)
                .FirstOrDefaultAsync(r => r.Id == rentalId);

            if (rental == null)
            {
                return (null, "租赁单不存在。");
            }

            if (rental.Status == RentalStatus.Returned)
            {
                return (null, "租赁单已归还。");
            }

            if (rental.Status == RentalStatus.Cancelled)
            {
                return (null, "已取消的租赁单不能登记归还。");
            }

            if (!HasOutboundShipment(rental))
            {
                return (null, "租赁尚未发货，请直接取消租赁。");
            }

            var targets = (dto.RentalItemIds == null || dto.RentalItemIds.Count == 0)
                ? rental.Items.Where(ri => ri.ReturnedAt == null).ToList()
                : rental.Items.Where(ri => dto.RentalItemIds.Contains(ri.Id) && ri.ReturnedAt == null).ToList();

            if (targets.Count == 0)
            {
                return (null, "没有可归还的商品。");
            }

            var now = DateTime.UtcNow;
            foreach (var rentalItem in targets)
            {
                rentalItem.ReturnedAt = now;
                rentalItem.ReturnCondition = dto.Condition;
                rentalItem.ReturnNotes = NormalizeNullableText(dto.Notes);

                if (rentalItem.Item == null)
                {
                    continue;
                }

                rentalItem.Item.Status = dto.Condition == ReturnCondition.Lost
                    ? ItemStatus.SuspectedMissing
                    : ItemStatus.InStock;
                rentalItem.Item.CurrentDestination = null;
                rentalItem.Item.LastUpdated = now;
                LogAudit(rentalItem.Item, AuditAction.RentalReturned, rental.RentalNumber, currentUser, dto.Notes);
            }

            var allReturned = rental.Items.All(ri => ri.ReturnedAt != null);
            if (allReturned)
            {
                rental.Status = RentalStatus.Returned;
                rental.ActualEndDate = now;
            }
            else
            {
                rental.Status = rental.ExpectedEndDate < now ? RentalStatus.Overdue : RentalStatus.Active;
            }

            rental.UpdatedAt = now;
            rental.UpdatedBy = currentUser;

            await _context.SaveChangesAsync();

            await NotifyStatusChangeAsync(
                rental,
                allReturned ? "已全部归还" : "部分归还",
                currentUser,
                $"归还 {targets.Count} 件");

            return (await GetByIdAsync(rentalId), null);
        }

        public async Task<(RentalDto? rental, string? error)> CancelAsync(Guid rentalId, CancelRentalDto dto, string? currentUser)
        {
            var rental = await _context.Rentals
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.ItemDefinition)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.Warehouse)
                .Include(r => r.Shipments)
                .FirstOrDefaultAsync(r => r.Id == rentalId);

            if (rental == null)
            {
                return (null, "租赁单不存在。");
            }

            if (rental.Status == RentalStatus.Returned || rental.Status == RentalStatus.Cancelled)
            {
                return (null, "租赁单已结束。");
            }

            var now = DateTime.UtcNow;
            var hasOutboundShipment = HasOutboundShipment(rental);
            foreach (var rentalItem in rental.Items.Where(ri => ri.ReturnedAt == null))
            {
                rentalItem.ReturnedAt = now;
                rentalItem.ReturnNotes = NormalizeNullableText(dto.Reason);

                if (!hasOutboundShipment || rentalItem.Item == null)
                {
                    continue;
                }

                rentalItem.Item.Status = ItemStatus.InStock;
                rentalItem.Item.CurrentDestination = null;
                rentalItem.Item.LastUpdated = now;
                LogAudit(rentalItem.Item, AuditAction.RentalCancelled, rental.RentalNumber, currentUser, dto.Reason);
            }

            rental.Status = RentalStatus.Cancelled;
            rental.ActualEndDate = now;

            var reason = NormalizeNullableText(dto.Reason);
            if (!string.IsNullOrWhiteSpace(reason))
            {
                rental.Notes = string.IsNullOrWhiteSpace(rental.Notes)
                    ? $"取消原因：{reason}"
                    : $"{rental.Notes}\n取消原因：{reason}";
            }

            rental.UpdatedAt = now;
            rental.UpdatedBy = currentUser;

            await _context.SaveChangesAsync();

            await NotifyStatusChangeAsync(
                rental,
                "已取消",
                currentUser,
                string.IsNullOrWhiteSpace(reason) ? null : $"原因：{reason}");

            return (await GetByIdAsync(rentalId), null);
        }

        public async Task<(RentalDto? rental, string? error)> BulkUpdateItemsAsync(Guid rentalId, BulkUpdateRentalItemsDto dto, string? currentUser)
        {
            var rental = await _context.Rentals
                .Include(r => r.Items)
                .FirstOrDefaultAsync(r => r.Id == rentalId);

            if (rental == null)
            {
                return (null, "租赁单不存在。");
            }

            if (rental.Status == RentalStatus.Cancelled)
            {
                return (null, "已取消的租赁单不可修改商品信息。");
            }

            var itemMap = rental.Items.ToDictionary(ri => ri.Id);
            var missing = dto.Items
                .Where(update => !itemMap.ContainsKey(update.RentalItemId))
                .Select(update => update.RentalItemId)
                .ToList();

            if (missing.Count > 0)
            {
                return (null, $"以下 RentalItemId 不属于当前租赁单：{string.Join(", ", missing)}");
            }

            foreach (var update in dto.Items)
            {
                var rentalItem = itemMap[update.RentalItemId];
                if (update.ListingRemarks != null)
                {
                    rentalItem.ListingRemarksSnapshot = NormalizeNullableText(update.ListingRemarks);
                }

                if (update.PerItemPrice.HasValue)
                {
                    rentalItem.PerItemPrice = update.PerItemPrice.Value;
                }
            }

            rental.UpdatedAt = DateTime.UtcNow;
            rental.UpdatedBy = currentUser;

            await _context.SaveChangesAsync();

            return (await GetByIdAsync(rentalId), null);
        }

        private async Task<RentalCreateConflictDto?> ValidateCreateConflictsAsync(
            IReadOnlyCollection<Guid> itemIds,
            DateTime startDate,
            DateTime expectedEndDate,
            Guid? excludeRentalId = null)
        {
            var overlappingRentals = await _context.Rentals
                .Include(r => r.Items)
                .Include(r => r.Shipments)
                .Where(r => r.Status != RentalStatus.Returned && r.Status != RentalStatus.Cancelled)
                .Where(r => excludeRentalId == null || r.Id != excludeRentalId.Value)
                .Where(r => r.StartDate <= expectedEndDate && startDate <= r.ExpectedEndDate)
                .Where(r => r.Items.Any(ri => itemIds.Contains(ri.ItemId) && ri.ReturnedAt == null))
                .ToListAsync();

            var pendingShipmentConflicts = new List<RentalScheduleConflictDto>();
            var shippedConflicts = new List<RentalScheduleConflictDto>();

            foreach (var rental in overlappingRentals)
            {
                var hasOutboundShipment = HasOutboundShipment(rental);

                foreach (var rentalItem in rental.Items.Where(ri => itemIds.Contains(ri.ItemId) && ri.ReturnedAt == null))
                {
                    var conflict = new RentalScheduleConflictDto
                    {
                        RentalId = rental.Id,
                        RentalNumber = rental.RentalNumber,
                        RentalStatus = rental.Status,
                        ItemId = rentalItem.ItemId,
                        ItemShortId = rentalItem.ItemShortIdSnapshot,
                        ItemName = rentalItem.ItemNameSnapshot,
                        StartDate = rental.StartDate,
                        ExpectedEndDate = rental.ExpectedEndDate,
                        HasOutboundShipment = hasOutboundShipment
                    };

                    if (hasOutboundShipment)
                    {
                        shippedConflicts.Add(conflict);
                    }
                    else
                    {
                        pendingShipmentConflicts.Add(conflict);
                    }
                }
            }

            if (pendingShipmentConflicts.Count == 0 && shippedConflicts.Count == 0)
            {
                return null;
            }

            return new RentalCreateConflictDto
            {
                Message = BuildCreateConflictMessage(pendingShipmentConflicts, shippedConflicts),
                PendingShipmentConflicts = pendingShipmentConflicts
                    .OrderBy(c => c.ItemShortId)
                    .ThenBy(c => c.StartDate)
                    .ToList(),
                ShippedConflicts = shippedConflicts
                    .OrderBy(c => c.ItemShortId)
                    .ThenBy(c => c.StartDate)
                    .ToList()
            };
        }

        private static string BuildCreateConflictMessage(
            IReadOnlyCollection<RentalScheduleConflictDto> pendingShipmentConflicts,
            IReadOnlyCollection<RentalScheduleConflictDto> shippedConflicts)
        {
            var parts = new List<string>();

            if (pendingShipmentConflicts.Count > 0)
            {
                parts.Add($"未发货订单冲突 {pendingShipmentConflicts.Count} 条");
            }

            if (shippedConflicts.Count > 0)
            {
                parts.Add($"已发货订单冲突 {shippedConflicts.Count} 条");
            }

            return $"所选商品存在租赁时间冲突：{string.Join("，", parts)}。";
        }

        private async Task<List<string>> GetOtherShippedOpenItemsAsync(Guid rentalId, IReadOnlyCollection<Guid> itemIds)
        {
            if (itemIds.Count == 0)
            {
                return new List<string>();
            }

            return await _context.Rentals
                .Where(r => r.Id != rentalId)
                .Where(r => r.Status != RentalStatus.Returned && r.Status != RentalStatus.Cancelled)
                .Where(r => r.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound))
                .SelectMany(r => r.Items
                    .Where(ri => itemIds.Contains(ri.ItemId) && ri.ReturnedAt == null)
                    .Select(ri => ri.ItemShortIdSnapshot))
                .Distinct()
                .OrderBy(shortId => shortId)
                .ToListAsync();
        }

        private async Task NotifyStatusChangeAsync(Rental rental, string action, string? currentUser, string? extra = null)
        {
            try
            {
                var title = $"租赁单 {rental.RentalNumber} | {action}";
                var content = extra == null
                    ? $"状态：{rental.Status}"
                    : $"状态：{rental.Status}；{extra}";

                var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(rental.CreatedBy))
                {
                    targets.Add(rental.CreatedBy.Trim());
                }

                foreach (var assignedUser in SplitUsers(rental.AssignedTo))
                {
                    targets.Add(assignedUser);
                }

                var admins = await _identityService.GetUsersInRoleAsync(BuiltInRoles.Admin);
                foreach (var admin in admins)
                {
                    targets.Add(admin);
                }

                if (targets.Count == 0)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                var level = action.Contains("取消", StringComparison.OrdinalIgnoreCase)
                    || action.Contains("逾期", StringComparison.OrdinalIgnoreCase)
                    ? ReminderLevel.Warning
                    : ReminderLevel.Info;

                var reminders = targets.Select(target => new Reminder
                {
                    Type = ReminderType.Manual,
                    Level = level,
                    RelatedEntityType = "Rental",
                    RelatedEntityId = rental.Id.ToString(),
                    Title = title,
                    Message = content,
                    TargetUser = target,
                    DueAt = now,
                    CreatedAt = now
                }).ToList();

                _context.Reminders.AddRange(reminders);
                await _context.SaveChangesAsync();

                foreach (var reminder in reminders)
                {
                    foreach (var channel in _notificationChannels)
                    {
                        try
                        {
                            await channel.DeliverAsync(reminder, default);
                        }
                        catch
                        {
                            // Reminder side-channel failures should not block the main workflow.
                        }
                    }
                }
            }
            catch
            {
                // Reminder failures should not block the main workflow.
            }
        }

        private async Task DismissOpenRentalAutoRemindersAsync(Guid rentalId, string? currentUser)
        {
            var openAutoReminders = await _context.Reminders
                .Where(r => r.RelatedEntityType == "Rental"
                    && r.RelatedEntityId == rentalId.ToString()
                    && r.DismissedAt == null
                    && (r.Type == ReminderType.RentalShipmentSoon
                        || r.Type == ReminderType.RentalDueSoon
                        || r.Type == ReminderType.RentalOverdue))
                .ToListAsync();

            if (openAutoReminders.Count == 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            foreach (var reminder in openAutoReminders)
            {
                reminder.DismissedAt = now;
                reminder.DismissedBy = currentUser;
            }
        }

        private async Task<string> GenerateRentalNumberAsync(DateTime at)
        {
            var datePart = at.ToString("yyyyMMdd");
            var prefix = $"R{datePart}-";
            var todayCount = await _context.Rentals.CountAsync(r => r.RentalNumber.StartsWith(prefix));
            return $"{prefix}{(todayCount + 1):D4}";
        }

        private void LogAudit(Item item, AuditAction action, string rentalNumber, string? user, string? extra = null)
        {
            var destination = string.IsNullOrEmpty(extra)
                ? $"租赁 {rentalNumber}"
                : $"租赁 {rentalNumber} - {extra}";

            _context.AuditLogs.Add(new AuditLog
            {
                Timestamp = DateTime.UtcNow,
                Action = action,
                ItemId = item.Id,
                ItemShortId = item.ShortId,
                ItemName = item.ItemDefinition?.Name ?? string.Empty,
                WarehouseId = item.WarehouseId,
                WarehouseName = item.Warehouse?.Name ?? string.Empty,
                User = user ?? "Unknown User",
                Destination = destination
            });
        }

        private static bool HasOutboundShipment(Rental rental) =>
            rental.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound);

        private static IEnumerable<string> SplitUsers(string? users) =>
            string.IsNullOrWhiteSpace(users)
                ? Array.Empty<string>()
                : users.Split(new[] { ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static string? NormalizeNullableText(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string? NormalizeAssignedTo(string? assignedTo)
        {
            var normalized = SplitUsers(assignedTo)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return normalized.Count == 0 ? null : string.Join(",", normalized);
        }

        private static string BuildShipmentSummary(string carrier, string? trackingNumber) =>
            string.IsNullOrWhiteSpace(trackingNumber)
                ? carrier.Trim()
                : $"{carrier.Trim()} {trackingNumber}";

        private static string Truncate(string? value, int max = 40)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "-";
            }

            return value.Length <= max ? value : value[..max] + "...";
        }

        private static RentalDto ToDto(Rental rental) => new()
        {
            Id = rental.Id,
            RentalNumber = rental.RentalNumber,
            Status = rental.Status,
            RenterId = rental.RenterId,
            Renter = rental.Renter == null ? null : RenterService.ToDto(rental.Renter),
            StartDate = rental.StartDate,
            ExpectedEndDate = rental.ExpectedEndDate,
            ActualEndDate = rental.ActualEndDate,
            TotalPrice = rental.TotalPrice,
            Deposit = rental.Deposit,
            OtherFee = rental.OtherFee,
            TotalShippingFee = rental.Shipments.Sum(s => s.ShippingFee ?? 0),
            AccountedAmount = rental.TotalPrice - rental.OtherFee - rental.Shipments.Sum(s => s.ShippingFee ?? 0),
            ShippingAddress = rental.ShippingAddress,
            PlatformOrderNo = rental.PlatformOrderNo,
            Notes = rental.Notes,
            CreatedAt = rental.CreatedAt,
            CreatedBy = rental.CreatedBy,
            UpdatedAt = rental.UpdatedAt,
            UpdatedBy = rental.UpdatedBy,
            AssignedTo = rental.AssignedTo,
            Items = rental.Items.Select(ToItemDto).ToList(),
            Shipments = rental.Shipments.Select(ToShipmentDto).ToList()
        };

        private static RentalItemDto ToItemDto(RentalItem rentalItem) => new()
        {
            Id = rentalItem.Id,
            ItemId = rentalItem.ItemId,
            ItemShortIdSnapshot = rentalItem.ItemShortIdSnapshot,
            ItemNameSnapshot = rentalItem.ItemNameSnapshot,
            PerItemPrice = rentalItem.PerItemPrice,
            ReturnedAt = rentalItem.ReturnedAt,
            ReturnCondition = rentalItem.ReturnCondition,
            ReturnNotes = rentalItem.ReturnNotes,
            ListingRemarks = rentalItem.ListingRemarksSnapshot
        };

        private static RentalShipmentDto ToShipmentDto(RentalShipment shipment) => new()
        {
            Id = shipment.Id,
            Direction = shipment.Direction,
            OriginWarehouseId = shipment.OriginWarehouseId,
            OriginWarehouseName = shipment.OriginWarehouse?.Name,
            Carrier = shipment.Carrier,
            TrackingNumber = shipment.TrackingNumber,
            ShippedAt = shipment.ShippedAt,
            DeliveredAt = shipment.DeliveredAt,
            ShippingFee = shipment.ShippingFee,
            Notes = shipment.Notes,
            CreatedBy = shipment.CreatedBy
        };
    }
}

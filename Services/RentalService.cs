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
                .Include(r => r.Shipments).ThenInclude(s => s.OriginWarehouse)
                .AsQueryable();

            if (query.Status.HasValue) q = q.Where(r => r.Status == query.Status.Value);
            if (query.RenterId.HasValue) q = q.Where(r => r.RenterId == query.RenterId.Value);
            if (!string.IsNullOrWhiteSpace(query.RentalNumber))
            {
                var n = query.RentalNumber.Trim();
                q = q.Where(r => r.RentalNumber.Contains(n));
            }
            if (query.StartDateFrom.HasValue) q = q.Where(r => r.StartDate >= query.StartDateFrom.Value);
            if (query.StartDateTo.HasValue) q = q.Where(r => r.StartDate <= query.StartDateTo.Value);

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
                .Include(r => r.Shipments).ThenInclude(s => s.OriginWarehouse)
                .FirstOrDefaultAsync(r => r.Id == id);
            return rental == null ? null : ToDto(rental);
        }

        public async Task<(RentalDto? rental, string? error)> CreateAsync(CreateRentalDto dto, string? currentUser)
        {
            if (dto.ItemIds == null || dto.ItemIds.Count == 0)
                return (null, "至少选择一件物品。");

            var distinctItemIds = dto.ItemIds.Select(id => Guid.Parse(id)).Distinct().ToList();

            var items = await _context.Items
                .Include(i => i.ItemDefinition)
                .Include(i => i.Warehouse)
                .Include(i => i.Listings)
                .Where(i => distinctItemIds.Contains(i.Id))
                .ToListAsync();

            if (items.Count != distinctItemIds.Count)
                return (null, "部分物品不存在。");

            var notInStock = items.Where(i => i.Status != ItemStatus.InStock).ToList();
            if (notInStock.Count > 0)
                return (null, $"以下物品不可租：{string.Join(", ", notInStock.Select(i => i.ShortId))}");

            var renter = await _renterService.ResolveOrUpsertAsync(dto.Renter, currentUser);

            var rentalId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var rentalNumber = await GenerateRentalNumberAsync(now);

            var rental = new Rental
            {
                Id = rentalId,
                RentalNumber = rentalNumber,
                RenterId = renter.Id,
                Renter = renter,
                Status = RentalStatus.Pending,
                StartDate = dto.StartDate ?? now,
                ExpectedEndDate = dto.ExpectedEndDate,
                TotalPrice = dto.TotalPrice,
                Deposit = dto.Deposit,
                ShippingAddress = dto.ShippingAddress ?? renter.DefaultAddress,
                Notes = dto.Notes,
                CreatedAt = now,
                CreatedBy = currentUser,
                UpdatedAt = now,
                UpdatedBy = currentUser,
                AssignedTo = string.IsNullOrWhiteSpace(dto.AssignedTo) ? null : dto.AssignedTo!.Trim()
            };
            _context.Rentals.Add(rental);

            foreach (var item in items)
            {
                var listingRemarks = string.Join("; ", item.Listings
                    .Where(l => l.Status == ListingStatus.Listed)
                    .Select(l => $"{l.Platform}: {l.Remarks ?? "无备注"}"));

                var ri = new RentalItem
                {
                    RentalId = rentalId,
                    ItemId = item.Id,
                    ItemShortIdSnapshot = item.ShortId,
                    ItemNameSnapshot = item.ItemDefinition?.Name ?? string.Empty,
                    ListingRemarksSnapshot = string.IsNullOrEmpty(listingRemarks) ? null : listingRemarks
                };
                _context.RentalItems.Add(ri);

                item.Status = ItemStatus.LoanedOut;
                item.CurrentDestination = $"租赁 {rentalNumber}";
                item.LastUpdated = now;

                LogAudit(item, AuditAction.RentalCreated, rentalNumber, currentUser);
            }

            await _context.SaveChangesAsync();

            await NotifyStatusChangeAsync(rental, "已创建", currentUser,
                $"租客：{renter.Name}，物品 {items.Count} 件");

            return (await GetByIdAsync(rentalId), null);
        }

        public async Task<(RentalDto? rental, string? error)> UpdateAsync(Guid id, UpdateRentalDto dto, string? currentUser)
        {
            var rental = await _context.Rentals
                .Include(r => r.Items).ThenInclude(ri => ri.Item)
                .FirstOrDefaultAsync(r => r.Id == id);
            if (rental == null) return (null, "租赁单不存在。");

            if (rental.Status == RentalStatus.Returned || rental.Status == RentalStatus.Cancelled)
                return (null, "已结束的租赁单不可修改。");

            var changes = new List<string>();
            var extended = false;

            if (dto.ExpectedEndDate.HasValue && dto.ExpectedEndDate.Value != rental.ExpectedEndDate)
            {
                extended = dto.ExpectedEndDate.Value > rental.ExpectedEndDate;
                changes.Add($"预计结束 {rental.ExpectedEndDate:yyyy-MM-dd} → {dto.ExpectedEndDate.Value:yyyy-MM-dd}");
                rental.ExpectedEndDate = dto.ExpectedEndDate.Value;
                // If was Overdue and extension pushes expiry into the future, demote to Active.
                if (rental.Status == RentalStatus.Overdue && rental.ExpectedEndDate > DateTime.UtcNow)
                    rental.Status = RentalStatus.Active;
            }
            if (dto.TotalPrice.HasValue && dto.TotalPrice.Value != rental.TotalPrice)
            {
                changes.Add($"总价 {rental.TotalPrice:0.0} → {dto.TotalPrice.Value:0.0}");
                rental.TotalPrice = dto.TotalPrice.Value;
            }
            if (dto.Deposit.HasValue && dto.Deposit.Value != rental.Deposit)
            {
                changes.Add($"押金 {rental.Deposit?.ToString("0.0") ?? "-"} → {dto.Deposit.Value:0.0}");
                rental.Deposit = dto.Deposit.Value;
            }
            if (dto.ShippingAddress != null && dto.ShippingAddress != rental.ShippingAddress)
            {
                changes.Add($"地址 {Truncate(rental.ShippingAddress)} → {Truncate(dto.ShippingAddress)}");
                rental.ShippingAddress = dto.ShippingAddress;
            }
            if (dto.Notes != null && dto.Notes != rental.Notes)
            {
                changes.Add($"备注 {Truncate(rental.Notes)} → {Truncate(dto.Notes)}");
                rental.Notes = dto.Notes;
            }
            if (dto.AssignedTo != null)
            {
                var next = string.IsNullOrWhiteSpace(dto.AssignedTo) ? null : dto.AssignedTo.Trim();
                if (next != rental.AssignedTo)
                {
                    changes.Add($"负责人 {rental.AssignedTo ?? "-"} → {next ?? "-"}");
                    rental.AssignedTo = next;
                }
            }

            if (changes.Count == 0)
                return (await GetByIdAsync(id), null);

            rental.UpdatedAt = DateTime.UtcNow;
            rental.UpdatedBy = currentUser;

            var summary = string.Join("；", changes);
            foreach (var ri in rental.Items)
            {
                if (ri.Item == null) continue;
                if (extended)
                    LogAudit(ri.Item, AuditAction.RentalExtended, rental.RentalNumber, currentUser, summary);
                else
                    LogAudit(ri.Item, AuditAction.RentalUpdated, rental.RentalNumber, currentUser, summary);
            }

            await _context.SaveChangesAsync();

            await NotifyStatusChangeAsync(rental, "信息已更新", currentUser, summary);

            return (await GetByIdAsync(id), null);
        }

        private static string Truncate(string? s, int max = 40)
        {
            if (string.IsNullOrEmpty(s)) return "-";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        public async Task<(RentalDto? rental, string? error)> AddShipmentAsync(Guid rentalId, CreateShipmentDto dto, string? currentUser)
        {
            var rental = await _context.Rentals
                .Include(r => r.Items).ThenInclude(ri => ri.Item)
                .FirstOrDefaultAsync(r => r.Id == rentalId);
            if (rental == null) return (null, "租赁单不存在。");

            if (rental.Status == RentalStatus.Cancelled || rental.Status == RentalStatus.Returned)
                return (null, "租赁单已结束，不能再登记物流。");

            var warehouse = await _context.Warehouses.FindAsync(dto.OriginWarehouseId);
            if (warehouse == null) return (null, "发货仓库不存在。");

            var shipment = new RentalShipment
            {
                RentalId = rentalId,
                Direction = dto.Direction,
                OriginWarehouseId = dto.OriginWarehouseId,
                Carrier = dto.Carrier,
                TrackingNumber = dto.TrackingNumber,
                ShippedAt = dto.ShippedAt ?? DateTime.UtcNow,
                ShippingFee = dto.ShippingFee,
                Notes = dto.Notes,
                CreatedBy = currentUser
            };
            _context.RentalShipments.Add(shipment);

            if (dto.Direction == ShipmentDirection.Outbound && rental.Status == RentalStatus.Pending)
            {
                rental.Status = RentalStatus.Active;
            }

            foreach (var ri in rental.Items)
            {
                if (ri.Item != null)
                    LogAudit(ri.Item, AuditAction.RentalShipped, rental.RentalNumber, currentUser, $"{dto.Carrier} {dto.TrackingNumber}");
            }

            rental.UpdatedAt = DateTime.UtcNow;
            rental.UpdatedBy = currentUser;

            await _context.SaveChangesAsync();

            await _context.Entry(shipment).Reference(s => s.OriginWarehouse).LoadAsync();

            var shipAction = dto.Direction == ShipmentDirection.Outbound ? "已发货" : "已收货";
            await NotifyStatusChangeAsync(rental, shipAction, currentUser,
                $"物流：{dto.Carrier}{(string.IsNullOrWhiteSpace(dto.TrackingNumber) ? "" : " " + dto.TrackingNumber)}");

            return (await GetByIdAsync(rentalId), null);
        }

        public async Task<(RentalDto? rental, string? error)> MarkDeliveredAsync(Guid rentalId, int shipmentId, DeliverShipmentDto dto, string? currentUser)
        {
            var shipment = await _context.RentalShipments
                .Include(s => s.OriginWarehouse)
                .FirstOrDefaultAsync(s => s.Id == shipmentId && s.RentalId == rentalId);
            if (shipment == null) return (null, "物流记录不存在。");

            shipment.DeliveredAt = dto.DeliveredAt ?? DateTime.UtcNow;

            var rental = await _context.Rentals
                .Include(r => r.Items).ThenInclude(ri => ri.Item)
                .FirstAsync(r => r.Id == rentalId);
            foreach (var ri in rental.Items)
            {
                if (ri.Item != null)
                    LogAudit(ri.Item, AuditAction.RentalDelivered, rental.RentalNumber, currentUser);
            }
            rental.UpdatedAt = DateTime.UtcNow;
            rental.UpdatedBy = currentUser;

            await _context.SaveChangesAsync();

            var direction = shipment.Direction == ShipmentDirection.Outbound ? "出库物流已签收" : "回库物流已签收";
            await NotifyStatusChangeAsync(rental, direction, currentUser);

            return (await GetByIdAsync(rentalId), null);
        }

        public async Task<(RentalDto? rental, string? error)> ReturnAsync(Guid rentalId, ReturnRentalDto dto, string? currentUser)
        {
            var rental = await _context.Rentals
                .Include(r => r.Items).ThenInclude(ri => ri.Item)
                .FirstOrDefaultAsync(r => r.Id == rentalId);
            if (rental == null) return (null, "租赁单不存在。");
            if (rental.Status == RentalStatus.Returned) return (null, "租赁单已归还。");
            if (rental.Status == RentalStatus.Cancelled) return (null, "已取消的租赁单不可归还。");

            var targets = (dto.RentalItemIds == null || dto.RentalItemIds.Count == 0)
                ? rental.Items.Where(ri => ri.ReturnedAt == null).ToList()
                : rental.Items.Where(ri => dto.RentalItemIds.Contains(ri.Id) && ri.ReturnedAt == null).ToList();

            if (targets.Count == 0) return (null, "没有可归还的物品。");

            var now = DateTime.UtcNow;
            foreach (var ri in targets)
            {
                ri.ReturnedAt = now;
                ri.ReturnCondition = dto.Condition;
                ri.ReturnNotes = dto.Notes;
                if (ri.Item != null)
                {
                    ri.Item.Status = dto.Condition == ReturnCondition.Lost ? ItemStatus.SuspectedMissing : ItemStatus.InStock;
                    ri.Item.CurrentDestination = null;
                    ri.Item.LastUpdated = now;
                    LogAudit(ri.Item, AuditAction.RentalReturned, rental.RentalNumber, currentUser, dto.Notes);
                }
            }

            var allReturned = rental.Items.All(ri => ri.ReturnedAt != null);
            if (allReturned)
            {
                rental.Status = RentalStatus.Returned;
                rental.ActualEndDate = now;
            }
            rental.UpdatedAt = now;
            rental.UpdatedBy = currentUser;

            await _context.SaveChangesAsync();

            await NotifyStatusChangeAsync(rental,
                allReturned ? "已全部归还" : "部分归还",
                currentUser,
                $"归还 {targets.Count} 件");

            return (await GetByIdAsync(rentalId), null);
        }

        public async Task<(RentalDto? rental, string? error)> CancelAsync(Guid rentalId, CancelRentalDto dto, string? currentUser)
        {
            var rental = await _context.Rentals
                .Include(r => r.Items).ThenInclude(ri => ri.Item)
                .FirstOrDefaultAsync(r => r.Id == rentalId);
            if (rental == null) return (null, "租赁单不存在。");
            if (rental.Status == RentalStatus.Returned || rental.Status == RentalStatus.Cancelled)
                return (null, "租赁单已结束。");

            var now = DateTime.UtcNow;
            foreach (var ri in rental.Items.Where(ri => ri.ReturnedAt == null))
            {
                ri.ReturnedAt = now;
                ri.ReturnNotes = dto.Reason;
                if (ri.Item != null)
                {
                    ri.Item.Status = ItemStatus.InStock;
                    ri.Item.CurrentDestination = null;
                    ri.Item.LastUpdated = now;
                    LogAudit(ri.Item, AuditAction.RentalCancelled, rental.RentalNumber, currentUser, dto.Reason);
                }
            }
            rental.Status = RentalStatus.Cancelled;
            rental.ActualEndDate = now;
            if (!string.IsNullOrWhiteSpace(dto.Reason))
            {
                rental.Notes = string.IsNullOrWhiteSpace(rental.Notes)
                    ? $"取消原因：{dto.Reason}"
                    : $"{rental.Notes}\n取消原因：{dto.Reason}";
            }
            rental.UpdatedAt = now;
            rental.UpdatedBy = currentUser;

            await _context.SaveChangesAsync();

            await NotifyStatusChangeAsync(rental, "已取消", currentUser,
                string.IsNullOrWhiteSpace(dto.Reason) ? null : $"原因：{dto.Reason}");

            return (await GetByIdAsync(rentalId), null);
        }

        public async Task<(RentalDto? rental, string? error)> BulkUpdateItemsAsync(Guid rentalId, BulkUpdateRentalItemsDto dto, string? currentUser)
        {
            var rental = await _context.Rentals
                .Include(r => r.Items)
                .FirstOrDefaultAsync(r => r.Id == rentalId);
            if (rental == null) return (null, "租赁单不存在。");
            if (rental.Status == RentalStatus.Cancelled)
                return (null, "已取消的租赁单不可修改物品信息。");

            var itemMap = rental.Items.ToDictionary(ri => ri.Id);
            var missing = dto.Items.Where(u => !itemMap.ContainsKey(u.RentalItemId)).Select(u => u.RentalItemId).ToList();
            if (missing.Count > 0)
                return (null, $"以下 RentalItemId 不属于本租赁单：{string.Join(", ", missing)}");

            foreach (var u in dto.Items)
            {
                var ri = itemMap[u.RentalItemId];
                if (u.ListingRemarks != null) ri.ListingRemarksSnapshot = string.IsNullOrWhiteSpace(u.ListingRemarks) ? null : u.ListingRemarks.Trim();
                if (u.PerItemPrice.HasValue) ri.PerItemPrice = u.PerItemPrice.Value;
            }

            rental.UpdatedAt = DateTime.UtcNow;
            rental.UpdatedBy = currentUser;
            await _context.SaveChangesAsync();
            return (await GetByIdAsync(rentalId), null);
        }

        private async Task NotifyStatusChangeAsync(Rental rental, string action, string? currentUser, string? extra = null)
        {
            try
            {
                var title = $"租赁单 {rental.RentalNumber} · {action}";
                var message = extra == null
                    ? $"状态：{rental.Status}"
                    : $"状态：{rental.Status}，{extra}";

                var targets = new HashSet<string>();
                if (!string.IsNullOrWhiteSpace(rental.CreatedBy)) targets.Add(rental.CreatedBy.Trim());
                if (!string.IsNullOrWhiteSpace(rental.AssignedTo))
                {
                    foreach (var name in rental.AssignedTo.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        targets.Add(name);
                    }
                }
                // 管理员默认全量接收
                var admins = await _identityService.GetUsersInRoleAsync(BuiltInRoles.Admin);
                foreach (var a in admins) targets.Add(a);

                if (targets.Count == 0) return;

                var now = DateTime.UtcNow;
                var level = action.Contains("取消") || action.Contains("逾期")
                    ? ReminderLevel.Warning
                    : ReminderLevel.Info;

                var reminders = targets.Select(t => new Reminder
                {
                    Type = ReminderType.Manual,
                    Level = level,
                    RelatedEntityType = "Rental",
                    RelatedEntityId = rental.Id.ToString(),
                    Title = title,
                    Message = message,
                    TargetUser = t,
                    DueAt = now,
                    CreatedAt = now
                }).ToList();

                _context.Reminders.AddRange(reminders);
                await _context.SaveChangesAsync();

                foreach (var r in reminders)
                foreach (var ch in _notificationChannels)
                {
                    try { await ch.DeliverAsync(r, default); } catch { /* ignore channel failures */ }
                }
            }
            catch
            {
                // 提醒通道失败不影响主业务流程
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
            var destination = string.IsNullOrEmpty(extra) ? $"租赁 {rentalNumber}" : $"租赁 {rentalNumber} - {extra}";
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

        private static RentalDto ToDto(Rental r) => new()
        {
            Id = r.Id,
            RentalNumber = r.RentalNumber,
            Status = r.Status,
            RenterId = r.RenterId,
            Renter = r.Renter == null ? null : RenterService.ToDto(r.Renter),
            StartDate = r.StartDate,
            ExpectedEndDate = r.ExpectedEndDate,
            ActualEndDate = r.ActualEndDate,
            TotalPrice = r.TotalPrice,
            Deposit = r.Deposit,
            ShippingAddress = r.ShippingAddress,
            Notes = r.Notes,
            CreatedAt = r.CreatedAt,
            CreatedBy = r.CreatedBy,
            UpdatedAt = r.UpdatedAt,
            UpdatedBy = r.UpdatedBy,
            AssignedTo = r.AssignedTo,
            Items = r.Items.Select(ToItemDto).ToList(),
            Shipments = r.Shipments.Select(ToShipmentDto).ToList()
        };

        private static RentalItemDto ToItemDto(RentalItem ri) => new()
        {
            Id = ri.Id,
            ItemId = ri.ItemId,
            ItemShortIdSnapshot = ri.ItemShortIdSnapshot,
            ItemNameSnapshot = ri.ItemNameSnapshot,
            PerItemPrice = ri.PerItemPrice,
            ReturnedAt = ri.ReturnedAt,
            ReturnCondition = ri.ReturnCondition,
            ReturnNotes = ri.ReturnNotes,
            ListingRemarks = ri.ListingRemarksSnapshot
        };

        private static RentalShipmentDto ToShipmentDto(RentalShipment s) => new()
        {
            Id = s.Id,
            Direction = s.Direction,
            OriginWarehouseId = s.OriginWarehouseId,
            OriginWarehouseName = s.OriginWarehouse?.Name,
            Carrier = s.Carrier,
            TrackingNumber = s.TrackingNumber,
            ShippedAt = s.ShippedAt,
            DeliveredAt = s.DeliveredAt,
            ShippingFee = s.ShippingFee,
            Notes = s.Notes,
            CreatedBy = s.CreatedBy
        };
    }
}

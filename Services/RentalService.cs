using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AuditIt.Api.Services
{
    public class RentalService : IRentalService
    {
        private readonly ApplicationDbContext _context;
        private readonly IRenterService _renterService;

        public RentalService(ApplicationDbContext context, IRenterService renterService)
        {
            _context = context;
            _renterService = renterService;
        }

        public async Task<IEnumerable<RentalDto>> ListAsync(RentalQueryParameters query)
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

            var page = Math.Max(1, query.Page);
            var pageSize = Math.Clamp(query.PageSize, 1, 200);

            var rows = await q.OrderByDescending(r => r.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return rows.Select(ToDto).ToList();
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

            var distinctItemIds = dto.ItemIds.Distinct().ToList();

            var items = await _context.Items
                .Include(i => i.ItemDefinition)
                .Include(i => i.Warehouse)
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
                var ri = new RentalItem
                {
                    RentalId = rentalId,
                    ItemId = item.Id,
                    ItemShortIdSnapshot = item.ShortId,
                    ItemNameSnapshot = item.ItemDefinition?.Name ?? string.Empty
                };
                _context.RentalItems.Add(ri);

                item.Status = ItemStatus.LoanedOut;
                item.CurrentDestination = $"租赁 {rentalNumber}";
                item.LastUpdated = now;

                LogAudit(item, AuditAction.RentalCreated, rentalNumber, currentUser);
            }

            await _context.SaveChangesAsync();

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

            var extended = false;
            if (dto.ExpectedEndDate.HasValue && dto.ExpectedEndDate.Value != rental.ExpectedEndDate)
            {
                extended = dto.ExpectedEndDate.Value > rental.ExpectedEndDate;
                rental.ExpectedEndDate = dto.ExpectedEndDate.Value;
                // If was Overdue and extension pushes expiry into the future, demote to Active.
                if (rental.Status == RentalStatus.Overdue && rental.ExpectedEndDate > DateTime.UtcNow)
                    rental.Status = RentalStatus.Active;
            }
            if (dto.TotalPrice.HasValue) rental.TotalPrice = dto.TotalPrice.Value;
            if (dto.Deposit.HasValue) rental.Deposit = dto.Deposit.Value;
            if (dto.ShippingAddress != null) rental.ShippingAddress = dto.ShippingAddress;
            if (dto.Notes != null) rental.Notes = dto.Notes;
            if (dto.AssignedTo != null) rental.AssignedTo = string.IsNullOrWhiteSpace(dto.AssignedTo) ? null : dto.AssignedTo.Trim();

            rental.UpdatedAt = DateTime.UtcNow;
            rental.UpdatedBy = currentUser;

            if (extended)
            {
                foreach (var ri in rental.Items)
                {
                    if (ri.Item != null)
                        LogAudit(ri.Item, AuditAction.RentalExtended, rental.RentalNumber, currentUser);
                }
            }

            await _context.SaveChangesAsync();
            return (await GetByIdAsync(id), null);
        }

        public async Task<(RentalShipmentDto? shipment, string? error)> AddShipmentAsync(Guid rentalId, CreateShipmentDto dto, string? currentUser)
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
            return (ToShipmentDto(shipment), null);
        }

        public async Task<(RentalShipmentDto? shipment, string? error)> MarkDeliveredAsync(Guid rentalId, int shipmentId, DeliverShipmentDto dto, string? currentUser)
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
            return (ToShipmentDto(shipment), null);
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
            return (await GetByIdAsync(rentalId), null);
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
            ReturnNotes = ri.ReturnNotes
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

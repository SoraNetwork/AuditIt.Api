using System.Globalization;
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
        private readonly ISfExpressService _sfExpressService;
        private readonly ISettlementService _settlementService;

        public RentalService(
            ApplicationDbContext context,
            IRenterService renterService,
            IIdentityService identityService,
            IEnumerable<INotificationChannel> notificationChannels,
            ISfExpressService sfExpressService,
            ISettlementService settlementService)
        {
            _context = context;
            _renterService = renterService;
            _identityService = identityService;
            _notificationChannels = notificationChannels;
            _sfExpressService = sfExpressService;
            _settlementService = settlementService;
        }

        public async Task<(IEnumerable<RentalDto> items, int total)> ListAsync(RentalQueryParameters query)
        {
            var q = _context.Rentals
                .Include(r => r.Renter)
                .Include(r => r.Items)
                .Include(r => r.Shipments)
                    .ThenInclude(s => s.OriginWarehouse)
                .Include(r => r.Shipments)
                    .ThenInclude(s => s.RentalItems)
                .Include(r => r.Shipments)
                    .ThenInclude(s => s.RentalItems)
                        .ThenInclude(link => link.RentalItem)
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

            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var search = query.Search.Trim();
                q = q.Where(r =>
                    r.RentalNumber.Contains(search)
                    || (r.PlatformOrderNo != null && r.PlatformOrderNo.Contains(search))
                    || (r.Renter != null && (
                        r.Renter.Name.Contains(search)
                        || (r.Renter.Phone != null && r.Renter.Phone.Contains(search))
                        || (r.Renter.XianyuId != null && r.Renter.XianyuId.Contains(search))
                        || (r.Renter.TaobaoId != null && r.Renter.TaobaoId.Contains(search))
                        || (r.Renter.XiaohongshuId != null && r.Renter.XiaohongshuId.Contains(search))))
                    || r.Items.Any(item =>
                        item.ItemShortIdSnapshot.Contains(search)
                        || item.ItemNameSnapshot.Contains(search)));
            }

            if (query.PendingSettlement)
            {
                q = q.Where(r =>
                    (r.Status == RentalStatus.Returned || r.Status == RentalStatus.Renewed)
                    && r.SettlementNotifiedAt == null);
            }

            var startDateFrom = query.StartDateFrom.HasValue
                ? RentalDateRules.ToBusinessDate(query.StartDateFrom.Value)
                : (DateTime?)null;
            var startDateTo = query.StartDateTo.HasValue
                ? RentalDateRules.ToBusinessDate(query.StartDateTo.Value)
                : (DateTime?)null;

            if (startDateFrom.HasValue)
            {
                q = q.Where(r => r.StartDate >= startDateFrom.Value.AddDays(-1));
            }

            if (startDateTo.HasValue)
            {
                q = q.Where(r => r.StartDate <= startDateTo.Value.AddDays(1).AddTicks(-1));
            }

            var page = Math.Max(1, query.Page);
            var pageSize = Math.Clamp(query.PageSize, 1, 200);
            var ordered = ApplyListSorting(q, query);

            List<Rental> rows;
            int total;
            if (startDateFrom.HasValue || startDateTo.HasValue)
            {
                var allRows = await ordered.ToListAsync();
                var filtered = allRows
                    .Where(r => !startDateFrom.HasValue || RentalDateRules.ToBusinessDate(r.StartDate) >= startDateFrom.Value)
                    .Where(r => !startDateTo.HasValue || RentalDateRules.ToBusinessDate(r.StartDate) <= startDateTo.Value)
                    .ToList();
                total = filtered.Count;
                rows = filtered
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();
            }
            else
            {
                total = await q.CountAsync();
                rows = await ordered
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();
            }

            return (rows.Select(ToDto), total);
        }

        private static IOrderedQueryable<Rental> ApplyListSorting(IQueryable<Rental> query, RentalQueryParameters parameters)
        {
            var sortField = parameters.SortField?.Trim();
            var descending = string.Equals(parameters.SortOrder, "descend", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parameters.SortOrder, "desc", StringComparison.OrdinalIgnoreCase);

            return sortField?.ToLowerInvariant() switch
            {
                "expectedshipdate" => ApplyNonNullableDateSorting(query, r => r.ExpectedShipDate, descending),
                "startdate" => ApplyNonNullableDateSorting(query, r => r.StartDate, descending),
                "expectedenddate" => ApplyNonNullableDateSorting(query, r => r.ExpectedEndDate, descending),
                "expectedreturndate" => ApplyNullableDateSorting(query, descending),
                _ => query
                    .OrderByDescending(r => r.CreatedAt)
                    .ThenByDescending(r => r.Id)
            };
        }

        private static IOrderedQueryable<Rental> ApplyNonNullableDateSorting(
            IQueryable<Rental> query,
            System.Linq.Expressions.Expression<Func<Rental, DateTime>> selector,
            bool descending)
        {
            return descending
                ? query
                    .OrderByDescending(selector)
                    .ThenByDescending(r => r.CreatedAt)
                    .ThenByDescending(r => r.Id)
                : query
                    .OrderBy(selector)
                    .ThenByDescending(r => r.CreatedAt)
                    .ThenByDescending(r => r.Id);
        }

        private static IOrderedQueryable<Rental> ApplyNullableDateSorting(IQueryable<Rental> query, bool descending)
        {
            return descending
                ? query
                    .OrderBy(r => !r.ExpectedReturnDate.HasValue)
                    .ThenByDescending(r => r.ExpectedReturnDate)
                    .ThenByDescending(r => r.CreatedAt)
                    .ThenByDescending(r => r.Id)
                : query
                    .OrderBy(r => !r.ExpectedReturnDate.HasValue)
                    .ThenBy(r => r.ExpectedReturnDate)
                    .ThenByDescending(r => r.CreatedAt)
                    .ThenByDescending(r => r.Id);
        }

        public async Task<IReadOnlyList<RentalCalendarEventDto>> GetCalendarAsync(
            RentalCalendarQueryParameters query,
            string? currentUser,
            bool includeReminders,
            bool canSeeAllReminders)
        {
            var today = RentalDateRules.Today(DateTime.UtcNow);
            var from = (query.From ?? today.AddDays(-7)).Date;
            var to = (query.To ?? today.AddDays(45)).Date.AddDays(1).AddTicks(-1);
            if (to < from)
            {
                (from, to) = (to.Date, from.Date.AddDays(1).AddTicks(-1));
            }

            if ((to - from).TotalDays > 120)
            {
                to = from.AddDays(120).AddTicks(-1);
            }

            var targetUser = ResolveCalendarTarget(query.TargetUser, currentUser, canSeeAllReminders);
            var showAllUsers = string.Equals(targetUser, "all", StringComparison.OrdinalIgnoreCase);

            var queryFrom = from.AddDays(-1);
            var queryTo = to.AddDays(1);
            var rentals = await _context.Rentals
                .Include(r => r.Renter)
                .Include(r => r.Items)
                .Include(r => r.Shipments)
                    .ThenInclude(s => s.OriginWarehouse)
                .Where(r => r.Status != RentalStatus.Cancelled)
                .Where(r => (r.StartDate <= queryTo
                        && (r.ExpectedEndDate >= queryFrom.AddDays(-2)
                            || r.ExpectedReturnDate >= queryFrom
                            || (r.HasRenewalIntent
                                && r.RenewalIntentEndDate.HasValue
                                && r.RenewalIntentEndDate.Value >= queryFrom)))
                    || (r.ExpectedShipDate <= queryTo
                        && r.Status == RentalStatus.Pending
                        && r.RenewedFromRentalId == null
                        && !r.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound))
                    || (r.ExpectedEndDate <= queryTo
                        && r.Status != RentalStatus.Returned
                        && r.Status != RentalStatus.Renewed
                        && r.RenewedToRentalId == null
                        && (r.RenewedFromRentalId != null || r.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound))
                        && !r.Shipments.Any(s => s.Direction == ShipmentDirection.Inbound)))
                .ToListAsync();

            var events = new List<RentalCalendarEventDto>();
            foreach (var rental in rentals.Where(r => showAllUsers || IsRentalForUser(r, targetUser)))
            {
                var startDate = RentalDateRules.ToBusinessDate(rental.StartDate);
                var expectedShipDate = RentalDateRules.ToBusinessDate(rental.ExpectedShipDate);
                var expectedEndDate = RentalDateRules.ToBusinessDate(rental.ExpectedEndDate);
                var effectiveExpectedEndDate = RentalDateRules.EffectiveExpectedEndDate(rental);
                var effectiveExpectedReturnDate = RentalDateRules.EffectiveExpectedReturnDate(rental);
                var hasOutboundShipment = rental.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound);
                var hasInboundShipment = rental.Shipments.Any(s => s.Direction == ShipmentDirection.Inbound);
                var hasRentalStarted = HasRentalStarted(rental);
                var hasOpenItems = !IsClosedStatus(rental.Status)
                    && rental.Items.Any(i => i.ReturnedAt == null);
                var rentalPeriodOverlaps = RentalDateRules.Overlaps(rental.StartDate, effectiveExpectedEndDate, from, to);

                if (rentalPeriodOverlaps
                    && rental.Status != RentalStatus.Renewed
                    && !rental.RenewedToRentalId.HasValue)
                {
                    events.Add(new RentalCalendarEventDto
                    {
                        Id = $"rental-period-{rental.Id}",
                        Kind = RentalCalendarEventKind.RentalPeriod,
                        Level = rental.Status == RentalStatus.Overdue ? ReminderLevel.Critical : ReminderLevel.Info,
                        RentalId = rental.Id,
                        RentalNumber = rental.RentalNumber,
                        RenterId = rental.RenterId,
                        RenterName = rental.Renter?.Name,
                        RentalStatus = rental.Status,
                        HasRenewalIntent = rental.HasRenewalIntent,
                        RenewalIntentEndDate = NormalizeRenewalIntentEndDate(rental.HasRenewalIntent, rental.RenewalIntentEndDate),
                        Title = $"租期 {rental.RentalNumber}",
                        Description = rental.HasRenewalIntent && rental.RenewalIntentEndDate.HasValue
                            ? $"{rental.Renter?.Name ?? "-"} | {rental.Items.Count} 件物品 | 续租意愿至 {RentalDateRules.Format(rental.RenewalIntentEndDate.Value)}"
                            : $"{rental.Renter?.Name ?? "-"} | {rental.Items.Count} 件物品",
                        StartAt = startDate,
                        EndAt = effectiveExpectedEndDate,
                        AllDay = true,
                        IsOpen = !IsClosedStatus(rental.Status)
                    });
                }

                if (!hasOutboundShipment
                    && !IsRenewal(rental)
                    && rental.Status == RentalStatus.Pending)
                {
                    var shipmentRequiredEnd = expectedShipDate < today
                        ? (today > to.Date ? to.Date : today)
                        : expectedShipDate;
                    if (RentalDateRules.Overlaps(expectedShipDate, shipmentRequiredEnd, from, to))
                    {
                        events.Add(new RentalCalendarEventDto
                        {
                            Id = $"shipment-required-{rental.Id}",
                            Kind = RentalCalendarEventKind.ShipmentRequired,
                            Level = expectedShipDate < today ? ReminderLevel.Critical : ReminderLevel.Warning,
                            RentalId = rental.Id,
                            RentalNumber = rental.RentalNumber,
                            RenterId = rental.RenterId,
                            RenterName = rental.Renter?.Name,
                            RentalStatus = rental.Status,
                            HasRenewalIntent = rental.HasRenewalIntent,
                            RenewalIntentEndDate = NormalizeRenewalIntentEndDate(rental.HasRenewalIntent, rental.RenewalIntentEndDate),
                            Title = $"需要发货 {rental.RentalNumber}",
                            Description = $"{rental.Renter?.Name ?? "-"} | 预计发货",
                            StartAt = expectedShipDate,
                            EndAt = shipmentRequiredEnd,
                            AllDay = true,
                            IsOpen = true
                        });
                    }
                }

                if (hasRentalStarted
                    && !hasInboundShipment
                    && !rental.RenewedToRentalId.HasValue
                    && hasOpenItems)
                {
                    var returnRequiredStart = effectiveExpectedReturnDate;
                    var returnRequiredEnd = returnRequiredStart < today
                        ? (today > to.Date ? to.Date : today)
                        : returnRequiredStart;
                    if (RentalDateRules.Overlaps(returnRequiredStart, returnRequiredEnd, from, to))
                    {
                        events.Add(new RentalCalendarEventDto
                        {
                            Id = $"return-required-{rental.Id}",
                            Kind = RentalCalendarEventKind.ReturnRequired,
                            Level = RentalDateRules.IsOverdue(rental.ExpectedEndDate, DateTime.UtcNow) || rental.Status == RentalStatus.Overdue
                                ? ReminderLevel.Critical
                                : ReminderLevel.Warning,
                            RentalId = rental.Id,
                            RentalNumber = rental.RentalNumber,
                            RenterId = rental.RenterId,
                            RenterName = rental.Renter?.Name,
                            RentalStatus = rental.Status,
                            HasRenewalIntent = rental.HasRenewalIntent,
                            RenewalIntentEndDate = NormalizeRenewalIntentEndDate(rental.HasRenewalIntent, rental.RenewalIntentEndDate),
                            Title = $"需要收货 {rental.RentalNumber}",
                            Description = $"{rental.Renter?.Name ?? "-"} | 租期结束，登记回货物流后消除",
                            StartAt = returnRequiredStart,
                            EndAt = returnRequiredEnd,
                            AllDay = true,
                            IsOpen = true
                        });
                    }
                }

                foreach (var shipment in rental.Shipments.Where(s => IsWithin(s.ShippedAt, from, to)))
                {
                    events.Add(new RentalCalendarEventDto
                    {
                        Id = $"shipment-{shipment.Id}",
                        Kind = shipment.Direction == ShipmentDirection.Outbound
                            ? RentalCalendarEventKind.OutboundShipment
                            : RentalCalendarEventKind.InboundShipment,
                        Level = ReminderLevel.Info,
                        RentalId = rental.Id,
                        RentalNumber = rental.RentalNumber,
                        RenterId = rental.RenterId,
                        RenterName = rental.Renter?.Name,
                        RentalStatus = rental.Status,
                        HasRenewalIntent = rental.HasRenewalIntent,
                        RenewalIntentEndDate = NormalizeRenewalIntentEndDate(rental.HasRenewalIntent, rental.RenewalIntentEndDate),
                        Title = shipment.Direction == ShipmentDirection.Outbound
                            ? $"已发货 {rental.RentalNumber}"
                            : $"回货物流 {rental.RentalNumber}",
                        Description = BuildCalendarShipmentDescription(shipment),
                        StartAt = shipment.ShippedAt,
                        EndAt = shipment.DeliveredAt ?? shipment.ShippedAt,
                        AllDay = false,
                        IsOpen = shipment.DeliveredAt == null
                    });
                }
            }

            if (includeReminders)
            {
                var autoReminderTypes = new[]
                {
                    ReminderType.RentalShipmentSoon,
                    ReminderType.RentalDueSoon,
                    ReminderType.RentalOverdue,
                    ReminderType.RentalDeliveryUnsigned,
                    ReminderType.RentalReturnUnsigned
                };
                var reminderQuery = _context.Reminders
                    .Where(r => (r.DueAt >= queryFrom && r.DueAt <= to)
                        || (r.DismissedAt == null && autoReminderTypes.Contains(r.Type) && r.DueAt <= to));

                if (!showAllUsers)
                {
                    reminderQuery = reminderQuery.Where(r => r.TargetUser == null || r.TargetUser == targetUser);
                }

                var reminders = await reminderQuery.ToListAsync();
                var reminderRentalIds = reminders
                    .Select(ParseRentalId)
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .Distinct()
                    .ToList();
                var reminderRentals = reminderRentalIds.Count == 0
                    ? new Dictionary<Guid, Rental>()
                    : await _context.Rentals
                        .Include(r => r.Items)
                        .Include(r => r.Renter)
                        .Include(r => r.Shipments)
                            .ThenInclude(s => s.RentalItems)
                        .Where(r => reminderRentalIds.Contains(r.Id))
                        .ToDictionaryAsync(r => r.Id);

                foreach (var reminder in reminders)
                {
                    var rentalId = ParseRentalId(reminder);
                    var reminderRental = rentalId.HasValue && reminderRentals.TryGetValue(rentalId.Value, out var rental)
                        ? rental
                        : null;
                    if (IsCompletedAutoReminder(reminder, rentalId, reminderRentals))
                    {
                        continue;
                    }

                    var isRentalAutoReminder = IsRentalAutoReminder(reminder.Type);
                    var startAt = isRentalAutoReminder
                        ? RentalDateRules.ToBusinessDate(reminder.DueAt)
                        : reminder.DueAt;
                    var endAt = ResolveReminderCalendarEnd(reminder, to);
                    if (startAt > to || endAt < from)
                    {
                        continue;
                    }

                    events.Add(new RentalCalendarEventDto
                    {
                        Id = $"reminder-{reminder.Id}",
                        Kind = RentalCalendarEventKind.Reminder,
                        ReminderType = reminder.Type,
                        Level = reminder.Level,
                        RentalId = rentalId,
                        RentalNumber = reminderRental?.RentalNumber,
                        RenterId = reminderRental?.RenterId,
                        RenterName = reminderRental?.Renter?.Name,
                        RentalStatus = reminderRental?.Status,
                        HasRenewalIntent = reminderRental?.HasRenewalIntent ?? false,
                        RenewalIntentEndDate = reminderRental == null
                            ? null
                            : NormalizeRenewalIntentEndDate(reminderRental.HasRenewalIntent, reminderRental.RenewalIntentEndDate),
                        ReminderId = reminder.Id,
                        Title = reminder.Title,
                        Description = reminder.Message,
                        StartAt = startAt,
                        EndAt = endAt,
                        AllDay = isRentalAutoReminder,
                        IsOpen = reminder.DismissedAt == null
                    });
                }
            }

            return DeduplicateCalendarEvents(events)
                .OrderBy(e => e.StartAt)
                .ThenBy(e => e.Kind)
                .ThenBy(e => e.Title)
                .ToList();
        }

        public async Task<RentalDto?> GetByIdAsync(Guid id)
        {
            var rental = await _context.Rentals
                .Include(r => r.Renter)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.ItemDefinition)
                        .ThenInclude(d => d!.Category)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.ItemDefinition)
                            .ThenInclude(d => d!.Category)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.Warehouse)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.Listings)
                .Include(r => r.Shipments)
                    .ThenInclude(s => s.OriginWarehouse)
                .Include(r => r.Shipments)
                    .ThenInclude(s => s.RentalItems)
                        .ThenInclude(link => link.RentalItem)
                .FirstOrDefaultAsync(r => r.Id == id);

            return rental == null ? null : ToDto(rental);
        }

        public async Task<CreateRentalResult> CreateAsync(CreateRentalDto dto, string? currentUser)
        {
            var hasItems = dto.ItemIds != null && dto.ItemIds.Count > 0;
            var hasDefs = dto.ItemDefinitionIds != null && dto.ItemDefinitionIds.Count > 0;
            if (!hasItems && !hasDefs)
            {
                return new CreateRentalResult
                {
                    Error = "必须选择至少一件商品或物品定义。"
                };
            }

            var distinctItemIds = new List<Guid>();
            if (hasItems)
            {
                foreach (var rawItemId in dto.ItemIds!.Distinct())
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
            }

            var definitionIds = dto.ItemDefinitionIds ?? new List<int>();
            var hasItemPrices = dto.ItemPrices != null && dto.ItemPrices.Count > 0;
            var itemPricesByItemId = new Dictionary<Guid, decimal>();
            var itemPricesByDefinitionId = new Dictionary<int, Queue<decimal>>();
            if (hasItemPrices)
            {
                var expectedItemIds = distinctItemIds.ToHashSet();
                var expectedDefinitionCounts = definitionIds
                    .GroupBy(definitionId => definitionId)
                    .ToDictionary(group => group.Key, group => group.Count());
                var expectedPriceCount = expectedItemIds.Count + definitionIds.Count;
                if (dto.ItemPrices!.Count != expectedPriceCount)
                {
                    return new CreateRentalResult { Error = "A rental price must be provided for every selected item." };
                }

                foreach (var price in dto.ItemPrices)
                {
                    var hasItemId = !string.IsNullOrWhiteSpace(price.ItemId);
                    if (hasItemId == price.ItemDefinitionId.HasValue)
                    {
                        return new CreateRentalResult { Error = "Each rental price must identify either an item or an item definition." };
                    }

                    if (hasItemId)
                    {
                        if (!Guid.TryParse(price.ItemId, out var itemId)
                            || !expectedItemIds.Contains(itemId)
                            || !itemPricesByItemId.TryAdd(itemId, price.PerItemPrice))
                        {
                            return new CreateRentalResult { Error = "The rental price items do not match the selected items." };
                        }
                        continue;
                    }

                    var definitionId = price.ItemDefinitionId!.Value;
                    if (!expectedDefinitionCounts.ContainsKey(definitionId))
                    {
                        return new CreateRentalResult { Error = "The rental price definitions do not match the selected item definitions." };
                    }

                    if (!itemPricesByDefinitionId.TryGetValue(definitionId, out var prices))
                    {
                        prices = new Queue<decimal>();
                        itemPricesByDefinitionId[definitionId] = prices;
                    }
                    prices.Enqueue(price.PerItemPrice);
                }

                if (itemPricesByItemId.Count != expectedItemIds.Count
                    || expectedDefinitionCounts.Any(expected => !itemPricesByDefinitionId.TryGetValue(expected.Key, out var prices)
                        || prices.Count != expected.Value))
                {
                    return new CreateRentalResult { Error = "A rental price must be provided for every selected item." };
                }
            }

            var totalPrice = hasItemPrices
                ? dto.ItemPrices!.Sum(price => price.PerItemPrice)
                : dto.TotalPrice;

            var now = DateTime.UtcNow;
            var startDate = RentalDateRules.ToBusinessDate(dto.StartDate ?? now);
            var expectedShipDate = dto.ExpectedShipDate.HasValue
                ? RentalDateRules.ToBusinessDate(dto.ExpectedShipDate.Value)
                : RentalDateRules.DefaultExpectedShipDate(startDate);
            var expectedEndDate = RentalDateRules.ToBusinessDate(dto.ExpectedEndDate);
            if (expectedEndDate < startDate)
            {
                return new CreateRentalResult
                {
                    Error = "预计结束日期不能早于开始日期。"
                };
            }

            var expectedReturnDate = dto.ExpectedReturnDate.HasValue
                ? RentalDateRules.ToBusinessDate(dto.ExpectedReturnDate.Value)
                : RentalDateRules.DefaultExpectedReturnDate(expectedEndDate);
            if (expectedReturnDate < expectedEndDate)
            {
                return new CreateRentalResult { Error = "预计回货时间不能早于预计结束日期。" };
            }

            var hasRenewalIntent = dto.HasRenewalIntent;
            var renewalIntentEndDate = dto.RenewalIntentEndDate.HasValue
                ? RentalDateRules.ToBusinessDate(dto.RenewalIntentEndDate.Value)
                : (DateTime?)null;
            var renewalIntentError = ValidateRenewalIntent(hasRenewalIntent, renewalIntentEndDate, expectedEndDate);
            if (renewalIntentError != null)
            {
                return new CreateRentalResult { Error = renewalIntentError };
            }
            var occupancyEndDate = RentalDateRules.EffectiveExpectedReturnDate(
                expectedEndDate,
                expectedReturnDate,
                hasRenewalIntent,
                renewalIntentEndDate);

            var items = new List<Item>();
            if (hasItems)
            {
                items = await _context.Items
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
            }

            var definitionDemand = items
                .Select(item => item.ItemDefinitionId)
                .Concat(dto.ItemDefinitionIds ?? new List<int>())
                .ToList();
            var itemConflicts = await ValidateCreateConflictsAsync(distinctItemIds, expectedShipDate, occupancyEndDate);
            var defConflicts = await ValidateItemDefinitionConflictsAsync(definitionDemand, expectedShipDate, occupancyEndDate);

            if ((itemConflicts != null || defConflicts.Count > 0) && !dto.AllowScheduleConflict)
            {
                var combined = itemConflicts ?? new RentalCreateConflictDto();
                if (defConflicts.Count > 0)
                {
                    combined.PendingShipmentConflicts.AddRange(defConflicts.Where(c => !c.HasOutboundShipment));
                    combined.ShippedConflicts.AddRange(defConflicts.Where(c => c.HasOutboundShipment));
                    combined.Message = "所选商品或物品定义存在租赁时间冲突。";
                }
                return new CreateRentalResult
                {
                    Conflict = combined
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
                ExpectedShipDate = expectedShipDate,
                ExpectedEndDate = expectedEndDate,
                ExpectedReturnDate = expectedReturnDate,
                HasRenewalIntent = hasRenewalIntent,
                RenewalIntentEndDate = hasRenewalIntent ? renewalIntentEndDate : null,
                TotalPrice = totalPrice,
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
                    ListingRemarksSnapshot = string.IsNullOrWhiteSpace(listingRemarks) ? null : listingRemarks,
                    PerItemPrice = hasItemPrices ? itemPricesByItemId[item.Id] : 0m
                });

                LogAudit(item, AuditAction.RentalCreated, rentalNumber, currentUser);
            }

            foreach (var defId in definitionIds)
            {
                var def = await _context.ItemDefinitions.FindAsync(defId);
                if (def == null) continue;

                _context.RentalItems.Add(new RentalItem
                {
                    RentalId = rentalId,
                    ItemId = null,
                    ItemDefinitionId = defId,
                    ItemShortIdSnapshot = "待选择",
                    ItemNameSnapshot = def.Name,
                    ListingRemarksSnapshot = null,
                    PerItemPrice = hasItemPrices ? itemPricesByDefinitionId[defId].Dequeue() : 0m
                });
            }

            await _context.SaveChangesAsync();

            await NotifyStatusChangeAsync(
                rental,
                "已创建",
                currentUser,
                $"租客：{renter.Name}，商品 {items.Count} 件，物品定义 {(dto.ItemDefinitionIds?.Count ?? 0)} 件");

            return new CreateRentalResult
            {
                Rental = await GetByIdAsync(rentalId)
            };
        }

        public async Task<RenewRentalResult> RenewAsync(Guid id, RenewRentalDto dto, string? currentUser)
        {
            var source = await _context.Rentals
                .Include(r => r.Renter)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.ItemDefinition)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.Warehouse)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.Listings)
                .Include(r => r.Shipments)
                .FirstOrDefaultAsync(r => r.Id == id);

            if (source == null)
            {
                return new RenewRentalResult { Error = "租赁单不存在。" };
            }

            if (IsClosedStatus(source.Status))
            {
                return new RenewRentalResult { Error = "已结束的租赁单不能续租。" };
            }

            if (source.RenewedToRentalId.HasValue)
            {
                return new RenewRentalResult { Error = $"该租赁单已续租到 {source.RenewedToRentalNumber}。" };
            }

            if (source.Status == RentalStatus.Pending && !HasRentalStarted(source))
            {
                return new RenewRentalResult { Error = "租赁尚未开始，不能续租。" };
            }

            var activeItems = source.Items.Where(ri => ri.ReturnedAt == null).ToList();
            if (activeItems.Count == 0)
            {
                return new RenewRentalResult { Error = "没有可续租的物品。" };
            }

            var missingItems = activeItems
                .Where(ri => ri.Item == null)
                .Select(ri => ri.ItemShortIdSnapshot)
                .ToList();
            if (missingItems.Count > 0)
            {
                return new RenewRentalResult { Error = $"部分续租物品不存在：{string.Join("，", missingItems)}" };
            }

            var disposedItems = activeItems
                .Where(ri => ri.Item?.Status == ItemStatus.Disposed)
                .Select(ri => ri.ItemShortIdSnapshot)
                .ToList();
            if (disposedItems.Count > 0)
            {
                return new RenewRentalResult { Error = $"以下物品已处置，不能续租：{string.Join("，", disposedItems)}" };
            }

            var sourceEndDate = RentalDateRules.ToBusinessDate(source.ExpectedEndDate);
            var startDate = dto.StartDate.HasValue
                ? RentalDateRules.ToBusinessDate(dto.StartDate.Value)
                : sourceEndDate.AddDays(1);
            var expectedEndDate = RentalDateRules.ToBusinessDate(dto.ExpectedEndDate);
            var expectedReturnDate = RentalDateRules.DefaultExpectedReturnDate(expectedEndDate);
            var hasRenewalIntent = false;
            DateTime? renewalIntentEndDate = null;
            var occupancyEndDate = RentalDateRules.EffectiveExpectedReturnDate(
                expectedEndDate,
                expectedReturnDate,
                hasRenewalIntent,
                renewalIntentEndDate);

            if (startDate <= sourceEndDate)
            {
                return new RenewRentalResult { Error = "续租开始日期必须晚于原租赁预计结束日期。" };
            }

            if (expectedEndDate < startDate)
            {
                return new RenewRentalResult { Error = "续租结束日期不能早于续租开始日期。" };
            }

            var itemIds = activeItems.Where(ri => ri.ItemId.HasValue).Select(ri => ri.ItemId!.Value).Distinct().ToList();
            var conflict = await ValidateCreateConflictsAsync(itemIds, startDate, occupancyEndDate, source.Id);
            var definitionDemand = activeItems
                .Select(ri => ri.Item?.ItemDefinitionId ?? ri.ItemDefinitionId)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToList();
            var defConflict = await ValidateItemDefinitionConflictsAsync(definitionDemand, startDate, occupancyEndDate, source.Id);

            if ((conflict != null || defConflict.Count > 0) && !dto.AllowScheduleConflict)
            {
                var combined = conflict ?? new RentalCreateConflictDto();
                if (defConflict.Count > 0)
                {
                    combined.PendingShipmentConflicts.AddRange(defConflict.Where(c => !c.HasOutboundShipment));
                    combined.ShippedConflicts.AddRange(defConflict.Where(c => c.HasOutboundShipment));
                    combined.Message = "续租商品或物品定义存在租赁时间冲突。";
                }
                return new RenewRentalResult { Conflict = combined };
            }

            var now = DateTime.UtcNow;
            var renewalId = Guid.NewGuid();
            var (renewalNumber, sequence) = await GenerateRenewalRentalNumberAsync(source);
            var notes = NormalizeNullableText(dto.Notes);
            var renewal = new Rental
            {
                Id = renewalId,
                RentalNumber = renewalNumber,
                RenterId = source.RenterId,
                Renter = source.Renter,
                Status = RentalStatus.Active,
                StartDate = startDate,
                ExpectedShipDate = startDate,
                ExpectedEndDate = expectedEndDate,
                ExpectedReturnDate = expectedReturnDate,
                HasRenewalIntent = hasRenewalIntent,
                RenewalIntentEndDate = renewalIntentEndDate,
                TotalPrice = dto.TotalPrice,
                Deposit = dto.Deposit ?? source.Deposit,
                OtherFee = dto.OtherFee,
                ShippingAddress = source.ShippingAddress,
                PlatformOrderNo = source.PlatformOrderNo,
                Notes = string.IsNullOrWhiteSpace(notes)
                    ? $"续租自 {source.RentalNumber}"
                    : $"续租自 {source.RentalNumber}\n{notes}",
                CreatedAt = now,
                CreatedBy = currentUser,
                UpdatedAt = now,
                UpdatedBy = currentUser,
                AssignedTo = source.AssignedTo,
                RenewedFromRentalId = source.Id,
                RenewedFromRentalNumber = source.RentalNumber,
                RenewalSequence = sequence
            };

            _context.Rentals.Add(renewal);

            foreach (var sourceItem in activeItems)
            {
                _context.RentalItems.Add(new RentalItem
                {
                    RentalId = renewalId,
                    ItemId = sourceItem.ItemId,
                    ItemDefinitionId = sourceItem.ItemDefinitionId,
                    ItemShortIdSnapshot = sourceItem.ItemShortIdSnapshot,
                    ItemNameSnapshot = sourceItem.ItemNameSnapshot,
                    ListingRemarksSnapshot = sourceItem.ListingRemarksSnapshot,
                    PerItemPrice = sourceItem.PerItemPrice ?? 0m
                });

                if (sourceItem.Item != null)
                {
                    sourceItem.Item.Status = ItemStatus.LoanedOut;
                    sourceItem.Item.CurrentDestination = $"租赁 {renewalNumber}";
                    sourceItem.Item.LastUpdated = now;
                    LogAudit(sourceItem.Item, AuditAction.RentalExtended, renewalNumber, currentUser, $"续租自 {source.RentalNumber}");
                }
            }

            source.Status = RentalStatus.Renewed;
            source.ActualEndDate = sourceEndDate;
            source.RenewedToRentalId = renewalId;
            source.RenewedToRentalNumber = renewalNumber;
            source.UpdatedAt = now;
            source.UpdatedBy = currentUser;

            source.HasRenewalIntent = false;
            source.RenewalIntentEndDate = null;
            source.ExpectedReturnDate = null;

            await DismissOpenRentalAutoRemindersAsync(source.Id, currentUser);

            await _context.SaveChangesAsync();

            await NotifyStatusChangeAsync(
                source,
                "已续租",
                currentUser,
                $"续租单：{renewalNumber}，续租至 {RentalDateRules.Format(expectedEndDate)}，金额 {dto.TotalPrice:F1}");

            await NotifyStatusChangeAsync(
                renewal,
                "续租已创建",
                currentUser,
                $"来源单：{source.RentalNumber}，租期 {RentalDateRules.Format(startDate)} - {RentalDateRules.Format(expectedEndDate)}");

            return new RenewRentalResult
            {
                OriginalRental = await GetByIdAsync(source.Id),
                RenewalRental = await GetByIdAsync(renewalId)
            };
        }

        public async Task<UpdateRentalResult> UpdateAsync(Guid id, UpdateRentalDto dto, string? currentUser)
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
                return new UpdateRentalResult { Error = "租赁单不存在。" };
            }

            if (IsClosedStatus(rental.Status))
            {
                return new UpdateRentalResult { Error = "已结束的租赁单不可修改。" };
            }

            var changes = new List<string>();
            var extended = false;
            var scheduleOrTargetChanged = false;
            var currentStartDate = RentalDateRules.ToBusinessDate(rental.StartDate);
            var currentExpectedShipDate = RentalDateRules.ToBusinessDate(rental.ExpectedShipDate);
            var currentExpectedEndDate = RentalDateRules.ToBusinessDate(rental.ExpectedEndDate);
            var currentExpectedReturnDate = rental.ExpectedReturnDate.HasValue
                ? RentalDateRules.ToBusinessDate(rental.ExpectedReturnDate.Value)
                : (DateTime?)null;
            var nextStartDate = dto.StartDate.HasValue
                ? RentalDateRules.ToBusinessDate(dto.StartDate.Value)
                : currentStartDate;
            var nextExpectedShipDate = dto.ExpectedShipDate.HasValue
                ? RentalDateRules.ToBusinessDate(dto.ExpectedShipDate.Value)
                : currentExpectedShipDate;
            var nextExpectedEndDate = dto.ExpectedEndDate.HasValue
                ? RentalDateRules.ToBusinessDate(dto.ExpectedEndDate.Value)
                : currentExpectedEndDate;
            var nextExpectedReturnDate = dto.ExpectedReturnDate.HasValue
                ? RentalDateRules.ToBusinessDate(dto.ExpectedReturnDate.Value)
                : currentExpectedReturnDate;
            if (dto.ExpectedEndDate.HasValue
                && !dto.ExpectedReturnDate.HasValue
                && (!currentExpectedReturnDate.HasValue
                    || currentExpectedReturnDate.Value == RentalDateRules.DefaultExpectedReturnDate(currentExpectedEndDate)))
            {
                nextExpectedReturnDate = RentalDateRules.DefaultExpectedReturnDate(nextExpectedEndDate);
            }

            if (nextExpectedEndDate < nextStartDate)
            {
                return new UpdateRentalResult { Error = "预计结束日期不能早于开始日期。" };
            }

            if (nextExpectedReturnDate.HasValue && nextExpectedReturnDate.Value < nextExpectedEndDate)
            {
                return new UpdateRentalResult { Error = "预计回货时间不能早于预计结束日期。" };
            }

            var nextHasRenewalIntent = dto.HasRenewalIntent ?? rental.HasRenewalIntent;
            var nextRenewalIntentEndDate = dto.HasRenewalIntent == false
                ? null
                : dto.RenewalIntentEndDate.HasValue
                    ? RentalDateRules.ToBusinessDate(dto.RenewalIntentEndDate.Value)
                    : rental.RenewalIntentEndDate.HasValue
                        ? RentalDateRules.ToBusinessDate(rental.RenewalIntentEndDate.Value)
                        : (DateTime?)null;
            var renewalIntentError = ValidateRenewalIntent(nextHasRenewalIntent, nextRenewalIntentEndDate, nextExpectedEndDate);
            if (renewalIntentError != null)
            {
                return new UpdateRentalResult { Error = renewalIntentError };
            }

            var currentOccupancyEndDate = RentalDateRules.EffectiveExpectedReturnDate(rental);
            var nextOccupancyEndDate = RentalDateRules.EffectiveExpectedReturnDate(
                nextExpectedEndDate,
                nextExpectedReturnDate,
                nextHasRenewalIntent,
                nextRenewalIntentEndDate);

            if ((dto.StartDate.HasValue && nextStartDate != currentStartDate)
                || (dto.ExpectedShipDate.HasValue && nextExpectedShipDate != currentExpectedShipDate)
                || (dto.ExpectedEndDate.HasValue && nextExpectedEndDate != currentExpectedEndDate)
                || nextExpectedReturnDate != currentExpectedReturnDate
                || nextOccupancyEndDate != currentOccupancyEndDate)
            {
                var itemIds = rental.Items.Where(ri => ri.ReturnedAt == null && ri.ItemId.HasValue).Select(ri => ri.ItemId!.Value).ToList();
                var conflict = await ValidateCreateConflictsAsync(
                    itemIds,
                    nextExpectedShipDate,
                    nextOccupancyEndDate,
                    rental.Id);
                if (conflict != null && !dto.AllowScheduleConflict)
                {
                    return new UpdateRentalResult { Conflict = conflict };
                }

                var definitionDemand = rental.Items
                    .Where(ri => ri.ReturnedAt == null)
                    .Select(ri => ri.Item?.ItemDefinitionId ?? ri.ItemDefinitionId)
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .ToList();
                var defConflict = await ValidateItemDefinitionConflictsAsync(definitionDemand, nextExpectedShipDate, nextOccupancyEndDate, rental.Id);
                if (defConflict.Count > 0 && !dto.AllowScheduleConflict)
                {
                    return new UpdateRentalResult
                    {
                        Conflict = new RentalCreateConflictDto
                        {
                            Message = "租赁时间或续租意愿存在冲突。",
                            PendingShipmentConflicts = defConflict.Where(c => !c.HasOutboundShipment).ToList(),
                            ShippedConflicts = defConflict.Where(c => c.HasOutboundShipment).ToList()
                        }
                    };
                }
            }

            if (dto.RenterId.HasValue && dto.RenterId.Value != rental.RenterId)
            {
                var nextRenter = await _context.Renters.FindAsync(dto.RenterId.Value);
                if (nextRenter == null)
                {
                    return new UpdateRentalResult { Error = "租客不存在。" };
                }

                changes.Add($"租客：{rental.Renter?.Name ?? rental.RenterId.ToString()} -> {nextRenter.Name}");
                rental.RenterId = nextRenter.Id;
                rental.Renter = nextRenter;
                scheduleOrTargetChanged = true;
            }

            if (dto.StartDate.HasValue && nextStartDate != currentStartDate)
            {
                changes.Add($"开始：{RentalDateRules.Format(rental.StartDate)} -> {nextStartDate:yyyy-MM-dd}");
                rental.StartDate = nextStartDate;
                scheduleOrTargetChanged = true;
            }

            if (dto.ExpectedShipDate.HasValue && nextExpectedShipDate != currentExpectedShipDate)
            {
                changes.Add($"预计发货：{RentalDateRules.Format(rental.ExpectedShipDate)} -> {nextExpectedShipDate:yyyy-MM-dd}");
                rental.ExpectedShipDate = nextExpectedShipDate;
                scheduleOrTargetChanged = true;
            }

            if (dto.ExpectedEndDate.HasValue)
            {
                if (nextExpectedEndDate != currentExpectedEndDate)
                {
                    extended = nextExpectedEndDate > currentExpectedEndDate;
                    changes.Add($"预计结束：{RentalDateRules.Format(rental.ExpectedEndDate)} -> {nextExpectedEndDate:yyyy-MM-dd}");
                    rental.ExpectedEndDate = nextExpectedEndDate;
                    scheduleOrTargetChanged = true;

                    if (rental.Status == RentalStatus.Overdue
                        && !RentalDateRules.IsOverdue(rental.ExpectedEndDate, DateTime.UtcNow))
                    {
                        rental.Status = HasRentalStarted(rental) ? RentalStatus.Active : RentalStatus.Pending;
                    }
                }
            }

            if (nextExpectedReturnDate != currentExpectedReturnDate)
            {
                var before = currentExpectedReturnDate.HasValue ? currentExpectedReturnDate.Value.ToString("yyyy-MM-dd") : "-";
                var after = nextExpectedReturnDate.HasValue ? nextExpectedReturnDate.Value.ToString("yyyy-MM-dd") : "-";
                changes.Add($"预计回货：{before} -> {after}");
                rental.ExpectedReturnDate = nextExpectedReturnDate;
                scheduleOrTargetChanged = true;
            }

            var nextNormalizedRenewalIntentEndDate = NormalizeRenewalIntentEndDate(nextHasRenewalIntent, nextRenewalIntentEndDate);
            var currentNormalizedRenewalIntentEndDate = NormalizeRenewalIntentEndDate(rental.HasRenewalIntent, rental.RenewalIntentEndDate);
            if (nextHasRenewalIntent != rental.HasRenewalIntent
                || nextNormalizedRenewalIntentEndDate != currentNormalizedRenewalIntentEndDate)
            {
                changes.Add($"续租意愿：{FormatRenewalIntent(rental.HasRenewalIntent, rental.RenewalIntentEndDate)} -> {FormatRenewalIntent(nextHasRenewalIntent, nextRenewalIntentEndDate)}");
                rental.HasRenewalIntent = nextHasRenewalIntent;
                rental.RenewalIntentEndDate = nextNormalizedRenewalIntentEndDate;
                scheduleOrTargetChanged = true;
                extended = nextOccupancyEndDate > currentOccupancyEndDate || extended;
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

            if (dto.CreatedBy != null)
            {
                var createdBy = NormalizeNullableText(dto.CreatedBy);
                if (createdBy != rental.CreatedBy)
                {
                    changes.Add($"建单人：{rental.CreatedBy ?? "-"} -> {createdBy ?? "-"}");
                    rental.CreatedBy = createdBy;
                }
            }

            if (dto.SenderName != null)
            {
                var senderName = NormalizeNullableText(dto.SenderName);
                if (senderName != rental.SenderName)
                {
                    changes.Add($"发货人：{rental.SenderName ?? "-"} -> {senderName ?? "-"}");
                    rental.SenderName = senderName;
                }
            }

            if (changes.Count == 0)
            {
                return new UpdateRentalResult { Rental = await GetByIdAsync(id) };
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

            return new UpdateRentalResult { Rental = await GetByIdAsync(id) };
        }

        public async Task<RentalShipmentResult> AddShipmentAsync(Guid rentalId, CreateShipmentDto dto, string? currentUser)
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
                return new RentalShipmentResult { Error = "租赁单不存在。" };
            }

            if (IsClosedStatus(rental.Status))
            {
                return new RentalShipmentResult { Error = "租赁单已结束，不能继续登记物流。" };
            }

            if (dto.Direction == ShipmentDirection.Inbound && !HasRentalStarted(rental))
            {
                return new RentalShipmentResult { Error = "租赁尚未发货，不能登记回货物流。" };
            }

            var warehouse = await _context.Warehouses.FindAsync(dto.OriginWarehouseId);
            if (warehouse == null)
            {
                return new RentalShipmentResult { Error = "发货仓库不存在。" };
            }

            if (dto.Direction == ShipmentDirection.Outbound)
            {
                if (dto.ItemSelections != null && dto.ItemSelections.Count > 0)
                {
                    foreach (var selection in dto.ItemSelections)
                    {
                        var rentalItem = rental.Items.FirstOrDefault(ri => ri.Id == selection.RentalItemId);
                        if (rentalItem == null)
                        {
                            return new RentalShipmentResult { Error = $"未找到租赁项 ID {selection.RentalItemId}。" };
                        }

                        if (rentalItem.ItemId == null)
                        {
                            var item = await _context.Items
                                .Include(i => i.ItemDefinition)
                                .Include(i => i.Listings)
                                .FirstOrDefaultAsync(i => i.Id == selection.ItemId);

                            if (item == null)
                            {
                                return new RentalShipmentResult { Error = $"所选库存商品不存在。" };
                            }

                            if (item.ItemDefinitionId != rentalItem.ItemDefinitionId)
                            {
                                return new RentalShipmentResult { Error = $"所选商品 {item.ShortId} 的分类不匹配。" };
                            }

                            if (item.Status == ItemStatus.Disposed)
                            {
                                return new RentalShipmentResult { Error = $"所选商品 {item.ShortId} 已被处置。" };
                            }

                            var listingRemarks = string.Join("; ", item.Listings
                                .Where(l => l.Status == ListingStatus.Listed)
                                .Select(l => $"{l.Platform}: {l.Remarks ?? "无备注"}"));

                            rentalItem.ItemId = item.Id;
                            rentalItem.ItemShortIdSnapshot = item.ShortId;
                            rentalItem.ItemNameSnapshot = item.ItemDefinition?.Name ?? string.Empty;
                            rentalItem.ListingRemarksSnapshot = string.IsNullOrWhiteSpace(listingRemarks) ? null : listingRemarks;
                            rentalItem.Item = item;
                        }
                    }
                }

                var remainingUncertain = rental.Items.Any(ri => ri.ItemId == null && ri.ReturnedAt == null);
                if (remainingUncertain)
                {
                    return new RentalShipmentResult { Error = "发货时必须为所有物品选择具体的库存。" };
                }

                var conflict = await ValidateOutboundShipmentConflictsAsync(
                    rental.Id,
                    rental.Items.Where(ri => ri.ReturnedAt == null).Select(ri => ri.ItemId!.Value).ToList());

                if (conflict != null && !dto.AllowOpenItemConflict)
                {
                    return new RentalShipmentResult { Conflict = conflict };
                }
            }

            var selectedInboundItemIds = new HashSet<int>();
            if (dto.Direction == ShipmentDirection.Inbound && dto.ItemSelections != null)
            {
                foreach (var selection in dto.ItemSelections)
                {
                    if (!selectedInboundItemIds.Add(selection.RentalItemId))
                    {
                        return new RentalShipmentResult { Error = "The same rental item cannot be selected more than once." };
                    }

                    var rentalItem = rental.Items.FirstOrDefault(ri => ri.Id == selection.RentalItemId);
                    if (rentalItem == null)
                    {
                        return new RentalShipmentResult { Error = $"Rental item {selection.RentalItemId} was not found." };
                    }

                    if (!rentalItem.ItemId.HasValue || rentalItem.ItemId.Value != selection.ItemId)
                    {
                        return new RentalShipmentResult { Error = "The selected return-shipment item does not match the rental item." };
                    }

                    if (rentalItem.ReturnedAt.HasValue)
                    {
                        return new RentalShipmentResult { Error = $"Item {rentalItem.ItemShortIdSnapshot} has already been returned and cannot be linked to a new return shipment." };
                    }
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
            var linkedRentalItems = dto.Direction == ShipmentDirection.Outbound
                ? rental.Items.Where(ri => ri.ReturnedAt == null).ToList()
                : rental.Items.Where(ri => selectedInboundItemIds.Contains(ri.Id)).ToList();
            foreach (var rentalItem in linkedRentalItems)
            {
                shipment.RentalItems.Add(new RentalShipmentItem { RentalItemId = rentalItem.Id });
            }

            _context.RentalShipments.Add(shipment);

            var logisticsSummary = BuildShipmentSummary(dto.Carrier, shipment.TrackingNumber);
            if (dto.Direction == ShipmentDirection.Outbound)
            {
                rental.Status = RentalDateRules.IsOverdue(rental.ExpectedEndDate, shippedAt)
                    ? RentalStatus.Overdue
                    : RentalStatus.Active;

                foreach (var rentalItem in linkedRentalItems)
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

                await DismissOpenRentalAutoRemindersAsync(rental.Id, currentUser, ReminderType.RentalShipmentSoon);
            }
            else
            {
                if (rental.Status == RentalStatus.Overdue)
                {
                    rental.Status = RentalStatus.Active;
                }

                await DismissOpenRentalAutoRemindersAsync(
                    rental.Id,
                    currentUser,
                    ReminderType.RentalDueSoon,
                    ReminderType.RentalOverdue);

                foreach (var rentalItem in linkedRentalItems)
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

            return new RentalShipmentResult { Rental = await GetByIdAsync(rentalId) };
        }

        public async Task<(RentalDto? rental, string? error)> MarkDeliveredAsync(Guid rentalId, int shipmentId, DeliverShipmentDto dto, string? currentUser)
        {
            var shipment = await _context.RentalShipments
                .Include(s => s.OriginWarehouse)
                .Include(s => s.RentalItems)
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
                .Include(r => r.Shipments)
                    .ThenInclude(s => s.RentalItems)
                .FirstAsync(r => r.Id == rentalId);

            var linkedRentalItemIds = shipment.RentalItems
                .Select(link => link.RentalItemId)
                .ToHashSet();
            var deliveredItems = linkedRentalItemIds.Count > 0
                ? rental.Items.Where(ri => linkedRentalItemIds.Contains(ri.Id) && ri.ReturnedAt == null)
                : rental.Items.Where(ri => ri.ReturnedAt == null);

            foreach (var rentalItem in deliveredItems)
            {
                if (rentalItem.Item == null)
                {
                    continue;
                }

                LogAudit(rentalItem.Item, AuditAction.RentalDelivered, rental.RentalNumber, currentUser);
            }

            rental.UpdatedAt = DateTime.UtcNow;
            rental.UpdatedBy = currentUser;

            if (shipment.Direction == ShipmentDirection.Outbound)
            {
                await DismissOpenRentalAutoRemindersAsync(rental.Id, currentUser, ReminderType.RentalDeliveryUnsigned);
            }
            else
            {
                if (HasDeliveredInboundShipmentForAllOpenItems(rental))
                {
                    await DismissOpenRentalAutoRemindersAsync(rental.Id, currentUser, ReminderType.RentalReturnUnsigned);
                }
            }

            await _context.SaveChangesAsync();

            await NotifyStatusChangeAsync(
                rental,
                shipment.Direction == ShipmentDirection.Outbound ? "出库物流已签收" : "回库物流已签收",
                currentUser);

            return (await GetByIdAsync(rentalId), null);
        }

        public async Task<(RentalDto? rental, string? error)> UpdateShipmentAsync(Guid rentalId, int shipmentId, UpdateShipmentDto dto, string? currentUser)
        {
            var shipment = await _context.RentalShipments
                .FirstOrDefaultAsync(s => s.Id == shipmentId && s.RentalId == rentalId);
            if (shipment == null)
            {
                return (null, "物流记录不存在。");
            }

            var rental = await _context.Rentals.FirstAsync(r => r.Id == rentalId);
            shipment.ShippingFee = dto.ShippingFee;
            rental.UpdatedAt = DateTime.UtcNow;
            rental.UpdatedBy = currentUser;

            await _context.SaveChangesAsync();
            await NotifyStatusChangeAsync(rental, "物流运费已更新", currentUser);

            return (await GetByIdAsync(rentalId), null);
        }

        public async Task<(RentalDto? rental, string? error)> DeleteShipmentAsync(
            Guid rentalId,
            int shipmentId,
            string? currentUser)
        {
            var rental = await _context.Rentals
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.ItemDefinition)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.Warehouse)
                .Include(r => r.Shipments)
                    .ThenInclude(s => s.RentalItems)
                .FirstOrDefaultAsync(r => r.Id == rentalId);

            if (rental == null)
            {
                return (null, "租赁单不存在。");
            }

            var shipment = rental.Shipments.FirstOrDefault(s => s.Id == shipmentId);
            if (shipment == null)
            {
                return (null, "物流记录不存在。");
            }

            var remainingShipments = rental.Shipments
                .Where(s => s.Id != shipmentId)
                .ToList();
            var hasRemainingOutbound = remainingShipments.Any(s => s.Direction == ShipmentDirection.Outbound);
            var hasRemainingInbound = remainingShipments.Any(s => s.Direction == ShipmentDirection.Inbound);

            if (shipment.Direction == ShipmentDirection.Outbound
                && !IsRenewal(rental)
                && !hasRemainingOutbound
                && hasRemainingInbound
                && !IsClosedStatus(rental.Status))
            {
                return (null, "该发货物流之后仍有回货物流，请先删除相关回货物流。");
            }

            var now = DateTime.UtcNow;
            var shipmentSummary = BuildShipmentSummary(shipment.Carrier, shipment.TrackingNumber);
            var deletedShippingFee = shipment.ShippingFee ?? 0;
            var remainingShippingFee = remainingShipments.Sum(s => s.ShippingFee ?? 0);

            rental.Shipments.Remove(shipment);
            _context.RentalShipments.Remove(shipment);

            if (!IsClosedStatus(rental.Status))
            {
                if (!IsRenewal(rental) && !hasRemainingOutbound)
                {
                    rental.Status = RentalStatus.Pending;

                    foreach (var rentalItem in rental.Items.Where(item => !item.ReturnedAt.HasValue))
                    {
                        var item = rentalItem.Item;
                        if (item == null
                            || item.Status != ItemStatus.LoanedOut
                            || !string.Equals(
                                item.CurrentDestination,
                                $"租赁 {rental.RentalNumber}",
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        item.Status = ItemStatus.InStock;
                        item.CurrentDestination = null;
                        item.LastUpdated = now;
                        LogAudit(
                            item,
                            AuditAction.RentalUpdated,
                            rental.RentalNumber,
                            currentUser,
                            $"删除误挂发货物流 {shipmentSummary}，恢复在库");
                    }
                }
                else
                {
                    rental.Status = !hasRemainingInbound
                        && RentalDateRules.IsOverdue(rental.ExpectedEndDate, now)
                            ? RentalStatus.Overdue
                            : RentalStatus.Active;
                }
            }

            var shipmentReminders = await _context.Reminders
                .Where(reminder => reminder.RelatedEntityType == "RentalShipment"
                    && reminder.RelatedEntityId == shipmentId.ToString()
                    && reminder.DismissedAt == null)
                .ToListAsync();
            foreach (var reminder in shipmentReminders)
            {
                reminder.DismissedAt = now;
                reminder.DismissedBy = currentUser;
            }

            rental.UpdatedAt = now;
            rental.UpdatedBy = currentUser;

            await _context.SaveChangesAsync();
            await NotifyStatusChangeAsync(
                rental,
                shipment.Direction == ShipmentDirection.Outbound ? "已删除发货物流" : "已删除回货物流",
                currentUser,
                $"物流：{shipmentSummary}；删除运费：{FormatMoney(deletedShippingFee)}；运费合计已重算为 {FormatMoney(remainingShippingFee)}");

            return (await GetByIdAsync(rentalId), null);
        }

        public async Task<(SfRouteSyncResultDto? result, string? error)> SyncSfRoutesAsync(
            Guid rentalId,
            bool forceRefresh,
            string? currentUser,
            CancellationToken ct = default)
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
                    .ThenInclude(s => s.OriginWarehouse)
                .FirstOrDefaultAsync(r => r.Id == rentalId, ct);

            if (rental == null)
            {
                return (null, "租赁单不存在。");
            }

            var sfShipments = rental.Shipments
                .Where(s => IsSfTrackingNumber(s.TrackingNumber))
                .OrderByDescending(s => s.ShippedAt)
                .ToList();

            var result = new SfRouteSyncResultDto();
            if (sfShipments.Count == 0)
            {
                result.Rental = ToDto(rental);
                return (result, null);
            }

            var renterPhoneTail = ResolvePhoneTail(rental.Renter?.Phone);
            var creatorPhoneTail = await ResolveCreatorPhoneTailAsync(rental.CreatedBy, ct);
            var adminPhoneTails = await ResolveAdminPhoneTailsAsync(ct);
            var routeResults = new List<SfRouteQueryResult>();

            if (!string.IsNullOrWhiteSpace(renterPhoneTail))
            {
                var renterQueryItems = BuildSfRouteQueryItems(sfShipments, renterPhoneTail);
                routeResults.AddRange(await _sfExpressService.QueryRoutesAsync(renterQueryItems, forceRefresh, ct));
            }

            var fallbackPhoneTails = new[] { creatorPhoneTail }
                .Concat(adminPhoneTails)
                .Where(phoneTail => !string.IsNullOrWhiteSpace(phoneTail))
                .Select(phoneTail => phoneTail!)
                .Where(phoneTail => !string.Equals(phoneTail, renterPhoneTail, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var fallbackPhoneTail in fallbackPhoneTails)
            {
                var routeResultsByShipmentId = routeResults.ToDictionary(r => r.ShipmentId);
                var fallbackShipments = sfShipments
                    .Where(shipment => !routeResultsByShipmentId.TryGetValue(shipment.Id, out var route)
                        || ShouldFallbackToAlternativePhone(route))
                    .ToList();

                if (fallbackShipments.Count == 0)
                {
                    continue;
                }

                var fallbackIds = fallbackShipments.Select(s => s.Id).ToHashSet();
                routeResults.RemoveAll(route => fallbackIds.Contains(route.ShipmentId));

                var fallbackQueryItems = BuildSfRouteQueryItems(fallbackShipments, fallbackPhoneTail);
                routeResults.AddRange(await _sfExpressService.QueryRoutesAsync(fallbackQueryItems, forceRefresh, ct));
            }

            if (routeResults.Count == 0)
            {
                foreach (var shipment in sfShipments)
                {
                    result.Shipments.Add(new SfShipmentRouteDto
                    {
                        ShipmentId = shipment.Id,
                        TrackingNumber = shipment.TrackingNumber ?? string.Empty,
                        Queryable = false,
                        Error = "租客、建单人和 Admin 手机号均不足 4 位，无法按顺丰运单号+手机号后四位查询。"
                    });
                }
            }

            var changed = false;
            foreach (var route in routeResults)
            {
                var shipment = sfShipments.FirstOrDefault(s => s.Id == route.ShipmentId);
                if (shipment == null)
                {
                    continue;
                }

                var autoDelivered = false;
                if (route.DeliveredAt.HasValue
                    && shipment.DeliveredAt == null)
                {
                    shipment.DeliveredAt = route.DeliveredAt.Value;
                    rental.UpdatedAt = DateTime.UtcNow;
                    rental.UpdatedBy = currentUser;
                    changed = true;
                    autoDelivered = true;

                    var isOutbound = shipment.Direction == ShipmentDirection.Outbound;
                    var reminderType = isOutbound
                        ? ReminderType.RentalDeliveryUnsigned
                        : ReminderType.RentalReturnUnsigned;
                    var shipmentLabel = isOutbound ? "发货" : "回货";

                    foreach (var rentalItem in rental.Items.Where(ri => ri.ReturnedAt == null && ri.Item != null))
                    {
                        LogAudit(
                            rentalItem.Item!,
                            AuditAction.RentalDelivered,
                            rental.RentalNumber,
                            currentUser,
                            $"顺丰自动签收{shipmentLabel}物流 {route.TrackingNumber}");
                    }

                    await DismissOpenRentalAutoRemindersAsync(rental.Id, currentUser, reminderType);

                    await NotifyStatusChangeAsync(
                        rental,
                        $"顺丰{shipmentLabel}已签收，系统已自动签收",
                        currentUser,
                        $"运单：{route.TrackingNumber}，签收时间：{RentalDateRules.FormatDateTime(route.DeliveredAt.Value)}");
                }

                if (route.HasException)
                {
                    await NotifyShipmentExceptionAsync(rental, shipment, route, currentUser, ct);
                }

                result.Shipments.Add(ToSfShipmentRouteDto(route, autoDelivered));
            }

            if (changed)
            {
                await _context.SaveChangesAsync(ct);
            }

            result.Rental = ToDto(rental);
            return (result, null);
        }

        public async Task<SfPendingRouteRefreshResultDto> SyncPendingSfRoutesAsync(string? currentUser, CancellationToken ct = default)
        {
            var rentals = await _context.Rentals
                .Include(r => r.Shipments)
                .Where(r => r.Status == RentalStatus.Pending
                    || r.Status == RentalStatus.Active
                    || r.Status == RentalStatus.Overdue
                    || r.Status == RentalStatus.Returned)
                .ToListAsync(ct);

            var rentalIds = rentals
                .Where(r => r.Shipments.Any(s =>
                    s.DeliveredAt == null
                    && IsSfTrackingNumber(s.TrackingNumber)))
                .Select(r => r.Id)
                .Distinct()
                .ToList();

            var summary = new SfPendingRouteRefreshResultDto
            {
                RentalCount = rentalIds.Count
            };

            foreach (var id in rentalIds)
            {
                ct.ThrowIfCancellationRequested();
                var (result, error) = await SyncSfRoutesAsync(id, forceRefresh: true, currentUser, ct);
                if (error == null && result != null)
                {
                    summary.Synced += result.Shipments.Count(s => s.Queryable);
                    summary.AutoDelivered += result.Shipments.Count(s => s.AutoDelivered);
                    summary.ExceptionCount += result.Shipments.Count(s => s.HasException);
                    summary.ErrorCount += result.Shipments.Count(s => s.Queryable && !string.IsNullOrWhiteSpace(s.Error));
                    summary.SkippedCount += result.Shipments.Count(s => !s.Queryable);
                }
            }

            return summary;
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

            if (rental.Status == RentalStatus.Returned || rental.Status == RentalStatus.Renewed)
            {
                return (null, "租赁单已归还。");
            }

            if (rental.Status == RentalStatus.Cancelled)
            {
                return (null, "已取消的租赁单不能登记归还。");
            }

            if (!HasRentalStarted(rental))
            {
                return (null, "租赁尚未发货，请直接取消租赁。");
            }

            var perItemReturns = dto.Items ?? new List<ReturnRentalItemDto>();
            var conditionByRentalItemId = new Dictionary<int, ReturnCondition>();
            var hasPerItemConditions = perItemReturns.Count > 0;
            if (hasPerItemConditions)
            {
                foreach (var item in perItemReturns)
                {
                    if (!conditionByRentalItemId.TryAdd(item.RentalItemId, item.Condition ?? dto.Condition ?? ReturnCondition.Good))
                    {
                        return (null, "同一件商品不能重复登记归还状态。");
                    }
                }
            }

            var targetIds = hasPerItemConditions
                ? conditionByRentalItemId.Keys.ToHashSet()
                : dto.RentalItemIds?.ToHashSet();
            var targets = targetIds == null || targetIds.Count == 0
                ? rental.Items.Where(ri => ri.ReturnedAt == null).ToList()
                : rental.Items.Where(ri => targetIds.Contains(ri.Id) && ri.ReturnedAt == null).ToList();

            if (targets.Count == 0)
            {
                return (null, "没有可归还的商品。");
            }

            if (hasPerItemConditions && targets.Count != conditionByRentalItemId.Count)
            {
                return (null, "部分归还商品不存在或已归还。");
            }

            var now = DateTime.UtcNow;
            var shouldCreateRepairOccupancy = dto.RepairOccupancy;
            if (shouldCreateRepairOccupancy
                && !targets.Any(ri => (conditionByRentalItemId.TryGetValue(ri.Id, out var condition)
                    ? condition
                    : dto.Condition ?? ReturnCondition.Good) is ReturnCondition.MinorDamage or ReturnCondition.MajorDamage))
            {
                return (null, "仅损坏商品可以登记维修占用。");
            }

            DateTime? repairExpectedReturnDate = null;
            if (shouldCreateRepairOccupancy)
            {
                if (!dto.RepairExpectedReturnDate.HasValue)
                {
                    return (null, "请选择维修占用结束日期。");
                }

                repairExpectedReturnDate = RentalDateRules.ToBusinessDate(dto.RepairExpectedReturnDate.Value);
                if (repairExpectedReturnDate.Value < RentalDateRules.Today(now))
                {
                    return (null, "维修占用结束日期不能早于今天。");
                }
            }

            foreach (var rentalItem in targets)
            {
                var condition = conditionByRentalItemId.TryGetValue(rentalItem.Id, out var perItemCondition)
                    ? perItemCondition
                    : dto.Condition ?? ReturnCondition.Good;
                var itemNeedsRepairOccupancy = shouldCreateRepairOccupancy
                    && condition is ReturnCondition.MinorDamage or ReturnCondition.MajorDamage;

                rentalItem.ReturnedAt = now;
                rentalItem.ReturnCondition = condition;
                rentalItem.ReturnNotes = NormalizeNullableText(dto.Notes);

                if (rentalItem.Item == null)
                {
                    continue;
                }

                rentalItem.Item.Status = condition == ReturnCondition.Lost
                    ? ItemStatus.SuspectedMissing
                    : itemNeedsRepairOccupancy
                        ? ItemStatus.LoanedOut
                        : ItemStatus.InStock;
                rentalItem.Item.CurrentDestination = itemNeedsRepairOccupancy ? "损坏维修" : null;
                rentalItem.Item.ExpectedReturnDate = itemNeedsRepairOccupancy ? repairExpectedReturnDate : null;
                rentalItem.Item.LastUpdated = now;
                LogAudit(rentalItem.Item, AuditAction.RentalReturned, rental.RentalNumber, currentUser, dto.Notes);
                if (itemNeedsRepairOccupancy)
                {
                    LogManualLoanAudit(rentalItem.Item, currentUser, "损坏维修");
                }
            }

            var allReturned = rental.Items.All(ri => ri.ReturnedAt != null);
            if (allReturned)
            {
                rental.Status = RentalStatus.Returned;
                rental.ActualEndDate = now;
                rental.HasRenewalIntent = false;
                rental.RenewalIntentEndDate = null;
                await DismissOpenRentalAutoRemindersAsync(rental.Id, currentUser);
            }
            else
            {
                rental.Status = !HasInboundShipment(rental) && RentalDateRules.IsOverdue(rental.ExpectedEndDate, now)
                    ? RentalStatus.Overdue
                    : RentalStatus.Active;
            }

            rental.UpdatedAt = now;
            rental.UpdatedBy = currentUser;

            await _context.SaveChangesAsync();

            await NotifyStatusChangeAsync(
                rental,
                allReturned ? "已全部归还" : "部分归还",
                currentUser,
                shouldCreateRepairOccupancy
                    ? $"归还 {targets.Count} 件，其中 {targets.Count(ri => (conditionByRentalItemId.TryGetValue(ri.Id, out var condition) ? condition : dto.Condition ?? ReturnCondition.Good) is ReturnCondition.MinorDamage or ReturnCondition.MajorDamage)} 件已转为损坏维修占用至 {repairExpectedReturnDate:yyyy-MM-dd}"
                    : $"归还 {targets.Count} 件");

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

            if (IsClosedStatus(rental.Status))
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
            rental.HasRenewalIntent = false;
            rental.RenewalIntentEndDate = null;
            await DismissOpenRentalAutoRemindersAsync(rental.Id, currentUser);

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

        public async Task<RentalItemsUpdateResult> UpdateRentalItemsAsync(Guid rentalId, UpdateRentalItemsDto dto, string? currentUser)
        {
            var requestedItemIds = dto.ItemIds ?? new List<string>();
            var requestedDefinitionIds = dto.ItemDefinitionIds ?? new List<int>();

            if (requestedItemIds.Count == 0 && requestedDefinitionIds.Count == 0)
            {
                return new RentalItemsUpdateResult { Error = "至少保留一件租赁物品。" };
            }

            var desiredItemIds = new List<Guid>();
            foreach (var rawItemId in requestedItemIds)
            {
                if (!Guid.TryParse(rawItemId, out var parsedItemId))
                {
                    return new RentalItemsUpdateResult { Error = "存在无效的物品 ID。" };
                }

                if (!desiredItemIds.Contains(parsedItemId))
                {
                    desiredItemIds.Add(parsedItemId);
                }
            }

            var desiredDefinitionIds = requestedDefinitionIds.Where(id => id > 0).ToList();
            var distinctDefinitionIds = desiredDefinitionIds.Distinct().ToList();
            var requestedPrices = dto.ItemPrices ?? new List<CreateRentalItemPriceDto>();
            var hasItemPrices = requestedPrices.Count > 0;
            var itemPricesByItemId = new Dictionary<Guid, decimal>();
            var itemPricesByDefinitionId = new Dictionary<int, Queue<decimal>>();
            if (hasItemPrices)
            {
                var expectedItemIds = desiredItemIds.ToHashSet();
                var expectedDefinitionCounts = desiredDefinitionIds
                    .GroupBy(definitionId => definitionId)
                    .ToDictionary(group => group.Key, group => group.Count());
                if (requestedPrices.Count != expectedItemIds.Count + desiredDefinitionIds.Count)
                {
                    return new RentalItemsUpdateResult { Error = "每件租赁物品都必须填写对应金额。" };
                }

                foreach (var price in requestedPrices)
                {
                    var hasItemId = !string.IsNullOrWhiteSpace(price.ItemId);
                    if (hasItemId == price.ItemDefinitionId.HasValue)
                    {
                        return new RentalItemsUpdateResult { Error = "每条金额必须且只能对应具体物品或物品定义。" };
                    }

                    if (hasItemId)
                    {
                        if (!Guid.TryParse(price.ItemId, out var itemId)
                            || !expectedItemIds.Contains(itemId)
                            || !itemPricesByItemId.TryAdd(itemId, price.PerItemPrice))
                        {
                            return new RentalItemsUpdateResult { Error = "具体物品金额与当前选择不匹配。" };
                        }
                        continue;
                    }

                    var definitionId = price.ItemDefinitionId!.Value;
                    if (!expectedDefinitionCounts.ContainsKey(definitionId))
                    {
                        return new RentalItemsUpdateResult { Error = "物品定义金额与当前选择不匹配。" };
                    }

                    if (!itemPricesByDefinitionId.TryGetValue(definitionId, out var prices))
                    {
                        prices = new Queue<decimal>();
                        itemPricesByDefinitionId[definitionId] = prices;
                    }
                    prices.Enqueue(price.PerItemPrice);
                }

                if (itemPricesByItemId.Count != expectedItemIds.Count
                    || expectedDefinitionCounts.Any(expected =>
                        !itemPricesByDefinitionId.TryGetValue(expected.Key, out var prices)
                        || prices.Count != expected.Value))
                {
                    return new RentalItemsUpdateResult { Error = "每件租赁物品都必须填写对应金额。" };
                }
            }

            var rental = await _context.Rentals
                .Include(r => r.Renter)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.ItemDefinition)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.Warehouse)
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                        .ThenInclude(i => i!.Listings)
                .Include(r => r.Shipments)
                .FirstOrDefaultAsync(r => r.Id == rentalId);

            if (rental == null)
            {
                return new RentalItemsUpdateResult { Error = "租赁单不存在。" };
            }

            if (IsClosedStatus(rental.Status))
            {
                return new RentalItemsUpdateResult { Error = "已结束的租赁单不能修改租赁物品。" };
            }

            var hasRentalStarted = HasRentalStarted(rental);
            if (hasRentalStarted && desiredDefinitionIds.Count > 0)
            {
                return new RentalItemsUpdateResult { Error = "租赁已发货或已续租开始，修改物品时只能选择具体物品。" };
            }

            var desiredItems = await _context.Items
                .Include(i => i.ItemDefinition)
                .Include(i => i.Warehouse)
                .Include(i => i.Listings)
                .Where(i => desiredItemIds.Contains(i.Id))
                .ToListAsync();

            if (desiredItems.Count != desiredItemIds.Count)
            {
                return new RentalItemsUpdateResult { Error = "部分物品不存在。" };
            }

            var desiredDefinitions = distinctDefinitionIds.Count == 0
                ? new List<ItemDefinition>()
                : await _context.ItemDefinitions
                    .Where(def => distinctDefinitionIds.Contains(def.Id))
                    .ToListAsync();

            if (desiredDefinitions.Count != distinctDefinitionIds.Count)
            {
                return new RentalItemsUpdateResult { Error = "部分物品定义不存在。" };
            }

            var definitionMap = desiredDefinitions.ToDictionary(def => def.Id);

            var disposedItems = desiredItems
                .Where(i => i.Status == ItemStatus.Disposed)
                .Select(i => i.ShortId)
                .ToList();
            if (disposedItems.Count > 0)
            {
                return new RentalItemsUpdateResult
                {
                    Error = $"以下物品已处置，不能加入租赁单：{string.Join("，", disposedItems)}"
                };
            }

            var activeRentalItems = rental.Items
                .Where(ri => ri.ReturnedAt == null)
                .ToList();
            var currentItemIds = activeRentalItems
                .Where(ri => ri.ItemId.HasValue)
                .Select(ri => ri.ItemId!.Value)
                .ToHashSet();
            var desiredSet = desiredItemIds.ToHashSet();
            var addItemIds = desiredSet.Except(currentItemIds).ToList();
            var desiredDefinitionCounts = desiredDefinitionIds
                .GroupBy(id => id)
                .ToDictionary(group => group.Key, group => group.Count());
            var currentUncertainByDefinition = activeRentalItems
                .Where(ri => ri.ItemId == null && ri.ItemDefinitionId.HasValue)
                .GroupBy(ri => ri.ItemDefinitionId!.Value)
                .ToDictionary(group => group.Key, group => group.ToList());

            var addDefinitionIds = new List<int>();
            foreach (var (definitionId, desiredCount) in desiredDefinitionCounts)
            {
                var currentCount = currentUncertainByDefinition.TryGetValue(definitionId, out var current)
                    ? current.Count
                    : 0;
                for (var i = currentCount; i < desiredCount; i++)
                {
                    addDefinitionIds.Add(definitionId);
                }
            }

            var removeRentalItems = activeRentalItems
                .Where(ri => ri.ItemId.HasValue && !desiredSet.Contains(ri.ItemId.Value))
                .ToList();
            foreach (var (definitionId, currentItems) in currentUncertainByDefinition)
            {
                var keepCount = desiredDefinitionCounts.TryGetValue(definitionId, out var desiredCount)
                    ? desiredCount
                    : 0;
                removeRentalItems.AddRange(currentItems.Skip(keepCount));
            }

            if (addItemIds.Count == 0
                && addDefinitionIds.Count == 0
                && removeRentalItems.Count == 0
                && !hasItemPrices)
            {
                return new RentalItemsUpdateResult { Rental = await GetByIdAsync(rentalId) };
            }

            var definitionConflicts = new List<RentalScheduleConflictDto>();
            if (addItemIds.Count > 0 || addDefinitionIds.Count > 0)
            {
                var occupancyEndDate = RentalDateRules.EffectiveExpectedReturnDate(rental);
                var definitionDemand = desiredItems
                    .Select(item => item.ItemDefinitionId)
                    .Concat(desiredDefinitionIds)
                    .ToList();
                definitionConflicts = await ValidateItemDefinitionConflictsAsync(
                    definitionDemand,
                    rental.ExpectedShipDate,
                    occupancyEndDate,
                    rental.Id);

                if (addItemIds.Count > 0)
                {
                    var conflict = await ValidateCreateConflictsAsync(
                        addItemIds,
                        rental.ExpectedShipDate,
                        occupancyEndDate,
                        rental.Id);
                    if (conflict != null && !dto.AllowScheduleConflict)
                    {
                        return new RentalItemsUpdateResult { Conflict = conflict };
                    }
                }
            }

            if (definitionConflicts.Count > 0 && !dto.AllowScheduleConflict)
            {
                return new RentalItemsUpdateResult
                {
                    Conflict = new RentalCreateConflictDto
                    {
                        Message = "所选物品或物品定义存在租赁时间冲突。",
                        PendingShipmentConflicts = definitionConflicts.Where(c => !c.HasOutboundShipment).ToList(),
                        ShippedConflicts = definitionConflicts.Where(c => c.HasOutboundShipment).ToList()
                    }
                };
            }

            var now = DateTime.UtcNow;
            var addedItems = desiredItems
                .Where(i => addItemIds.Contains(i.Id))
                .ToList();

            if (hasItemPrices)
            {
                foreach (var rentalItem in activeRentalItems.Where(item =>
                    item.ItemId.HasValue && desiredSet.Contains(item.ItemId.Value)))
                {
                    rentalItem.PerItemPrice = itemPricesByItemId[rentalItem.ItemId!.Value];
                }

                foreach (var (definitionId, currentItems) in currentUncertainByDefinition)
                {
                    var keepCount = desiredDefinitionCounts.TryGetValue(definitionId, out var desiredCount)
                        ? desiredCount
                        : 0;
                    if (keepCount == 0)
                    {
                        continue;
                    }

                    var prices = itemPricesByDefinitionId[definitionId];
                    foreach (var rentalItem in currentItems.Take(keepCount))
                    {
                        rentalItem.PerItemPrice = prices.Dequeue();
                    }
                }
            }

            foreach (var rentalItem in removeRentalItems)
            {
                if (hasRentalStarted)
                {
                    rentalItem.ReturnedAt = now;
                    rentalItem.ReleasedFromRentalAt = now;
                    rentalItem.ReturnCondition = ReturnCondition.Good;
                    rentalItem.ReturnNotes = RentalDateRules.RemovedFromRentalReturnNote;

                    if (rentalItem.Item != null)
                    {
                        if (rentalItem.Item.Status == ItemStatus.LoanedOut)
                        {
                            rentalItem.Item.Status = ItemStatus.InStock;
                            rentalItem.Item.CurrentDestination = null;
                        }

                        rentalItem.Item.LastUpdated = now;
                        LogAudit(rentalItem.Item, AuditAction.RentalUpdated, rental.RentalNumber, currentUser, "Removed from rental");
                    }
                }
                else
                {
                    _context.RentalItems.Remove(rentalItem);
                    if (rentalItem.Item != null)
                    {
                        rentalItem.Item.LastUpdated = now;
                        LogAudit(rentalItem.Item, AuditAction.RentalUpdated, rental.RentalNumber, currentUser, "Removed from rental before start");
                    }
                }
            }

            foreach (var item in addedItems)
            {
                var listingRemarks = BuildListingRemarks(item);
                _context.RentalItems.Add(new RentalItem
                {
                    RentalId = rental.Id,
                    ItemId = item.Id,
                    ItemShortIdSnapshot = item.ShortId,
                    ItemNameSnapshot = item.ItemDefinition?.Name ?? string.Empty,
                    ListingRemarksSnapshot = string.IsNullOrWhiteSpace(listingRemarks) ? null : listingRemarks,
                    PerItemPrice = hasItemPrices ? itemPricesByItemId[item.Id] : 0m
                });

                if (hasRentalStarted)
                {
                    item.Status = ItemStatus.LoanedOut;
                    item.CurrentDestination = $"租赁 {rental.RentalNumber}";
                }

                item.LastUpdated = now;
                LogAudit(item, AuditAction.RentalUpdated, rental.RentalNumber, currentUser, "Added to rental");
            }

            foreach (var definitionId in addDefinitionIds)
            {
                var definition = definitionMap[definitionId];
                _context.RentalItems.Add(new RentalItem
                {
                    RentalId = rental.Id,
                    ItemId = null,
                    ItemDefinitionId = definitionId,
                    ItemShortIdSnapshot = "待选择",
                    ItemNameSnapshot = definition.Name,
                    ListingRemarksSnapshot = null,
                    PerItemPrice = hasItemPrices ? itemPricesByDefinitionId[definitionId].Dequeue() : 0m
                });
            }

            if (hasItemPrices)
            {
                rental.TotalPrice = requestedPrices.Sum(price => price.PerItemPrice);
            }

            rental.UpdatedAt = now;
            rental.UpdatedBy = currentUser;
            await DismissOpenRentalAutoRemindersAsync(rental.Id, currentUser);

            await _context.SaveChangesAsync();

            var summaryParts = new List<string>();
            if (addedItems.Count > 0)
            {
                summaryParts.Add($"新增 {addedItems.Count} 件");
            }

            if (addDefinitionIds.Count > 0)
            {
                summaryParts.Add($"新增物品定义占位 {addDefinitionIds.Count} 件");
            }

            if (removeRentalItems.Count > 0)
            {
                summaryParts.Add($"移出 {removeRentalItems.Count} 件");
            }

            if (hasItemPrices)
            {
                summaryParts.Add($"金额合计更新为 {FormatMoney(rental.TotalPrice)}");
            }

            await NotifyStatusChangeAsync(
                rental,
                "已修改租赁物品",
                currentUser,
                string.Join("，", summaryParts));

            return new RentalItemsUpdateResult { Rental = await GetByIdAsync(rentalId) };
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

            if (IsClosedStatus(rental.Status))
            {
                return (null, "已结束的租赁单不可修改商品信息。");
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

            var activeItems = rental.Items.Where(item => !item.ReturnedAt.HasValue).ToList();
            var recalculatedTotal = activeItems.Count > 0
                && activeItems.All(item => item.PerItemPrice.HasValue);
            if (recalculatedTotal)
            {
                rental.TotalPrice = activeItems.Sum(item => item.PerItemPrice!.Value);
            }

            rental.UpdatedAt = DateTime.UtcNow;
            rental.UpdatedBy = currentUser;

            await _context.SaveChangesAsync();
            await NotifyStatusChangeAsync(
                rental,
                "租赁物品信息已更新",
                currentUser,
                recalculatedTotal ? $"金额合计已重算为 {FormatMoney(rental.TotalPrice)}" : null);

            return (await GetByIdAsync(rentalId), null);
        }

        private async Task<RentalCreateConflictDto?> ValidateCreateConflictsAsync(
            IReadOnlyCollection<Guid> itemIds,
            DateTime requestedOccupancyStartDate,
            DateTime expectedEndDate,
            Guid? excludeRentalId = null)
        {
            var startDay = RentalDateRules.ToBusinessDate(requestedOccupancyStartDate);
            var expectedEndDay = RentalDateRules.ToBusinessDate(expectedEndDate);
            var candidateRentals = await _context.Rentals
                .Include(r => r.Items)
                .Include(r => r.Shipments)
                .Where(r => r.Status != RentalStatus.Returned && r.Status != RentalStatus.Cancelled && r.Status != RentalStatus.Renewed)
                .Where(r => excludeRentalId == null || r.Id != excludeRentalId.Value)
                .Where(r => r.Items.Any(ri => ri.ItemId.HasValue && itemIds.Contains(ri.ItemId.Value) && ri.ReturnedAt == null))
                .ToListAsync();
            var overlappingRentals = candidateRentals
                .Where(r => RentalDateRules.Overlaps(
                    RentalDateRules.OccupancyStartDate(r),
                    RentalDateRules.OccupancyEndDate(
                        RentalDateRules.EffectiveExpectedEndDate(r),
                        r.ActualEndDate,
                        openEndedUntil: RentalDateRules.OpenEndedUntil(
                            r.ActualEndDate,
                            null,
                            HasRentalStarted(r) && r.Items.Any(ri => ri.ReturnedAt == null),
                            expectedEndDay),
                        includeReturnBuffer: ShouldUseReturnBuffer(r),
                        expectedReturnDate: RentalDateRules.EffectiveExpectedReturnDate(r)),
                    startDay,
                    expectedEndDay))
                .ToList();

            var pendingShipmentConflicts = new List<RentalScheduleConflictDto>();
            var shippedConflicts = new List<RentalScheduleConflictDto>();

            foreach (var rental in overlappingRentals)
            {
                var hasRentalStarted = HasRentalStarted(rental);

                foreach (var rentalItem in rental.Items.Where(ri => ri.ItemId.HasValue && itemIds.Contains(ri.ItemId.Value) && ri.ReturnedAt == null))
                {
                    var conflict = new RentalScheduleConflictDto
                    {
                        RentalId = rental.Id,
                        RentalNumber = rental.RentalNumber,
                        RentalStatus = rental.Status,
                        ItemId = rentalItem.ItemId ?? Guid.Empty,
                        ItemShortId = rentalItem.ItemShortIdSnapshot,
                        ItemName = rentalItem.ItemNameSnapshot,
                        StartDate = RentalDateRules.ToBusinessDate(rental.StartDate),
                        ExpectedEndDate = RentalDateRules.EffectiveExpectedReturnDate(rental),
                        HasRenewalIntent = rental.HasRenewalIntent,
                        RenewalIntentEndDate = NormalizeRenewalIntentEndDate(rental.HasRenewalIntent, rental.RenewalIntentEndDate),
                        HasOutboundShipment = hasRentalStarted
                    };

                    if (hasRentalStarted)
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

        private async Task<List<RentalScheduleConflictDto>> ValidateItemDefinitionConflictsAsync(
            IReadOnlyCollection<int> itemDefIds,
            DateTime requestedOccupancyStartDate,
            DateTime expectedEndDate,
            Guid? excludeRentalId = null)
        {
            var conflicts = new List<RentalScheduleConflictDto>();
            if (itemDefIds == null || itemDefIds.Count == 0)
            {
                return conflicts;
            }

            var startDay = RentalDateRules.ToBusinessDate(requestedOccupancyStartDate);
            var expectedEndDay = RentalDateRules.ToBusinessDate(expectedEndDate);
            var distinctDefs = itemDefIds.Distinct().ToList();

            var manualLoanCandidates = await _context.Items
                .Where(i => distinctDefs.Contains(i.ItemDefinitionId) && i.Status == ItemStatus.LoanedOut)
                .Where(i => i.CurrentDestination == null || !i.CurrentDestination.StartsWith("租赁 "))
                .Select(i => new
                {
                    i.Id,
                    i.ItemDefinitionId,
                    i.LastUpdated,
                    i.ExpectedReturnDate,
                    OutboundAt = _context.AuditLogs
                        .Where(log => log.ItemId == i.Id && log.Action == AuditAction.Outbound)
                        .OrderByDescending(log => log.Timestamp)
                        .Select(log => (DateTime?)log.Timestamp)
                        .FirstOrDefault()
                })
                .ToListAsync();
            var manualLoanItems = manualLoanCandidates
                .Where(item => item.OutboundAt.HasValue)
                .ToList();

            var candidateRentals = await _context.Rentals
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                .Include(r => r.Shipments)
                .Where(r => r.Status != RentalStatus.Cancelled)
                .Where(r => excludeRentalId == null || r.Id != excludeRentalId.Value)
                .ToListAsync();

            var overlappingRentals = candidateRentals
                .Where(r => RentalDateRules.Overlaps(
                    RentalDateRules.OccupancyStartDate(r),
                    RentalDateRules.OccupancyEndDate(
                        RentalDateRules.EffectiveExpectedEndDate(r),
                        r.ActualEndDate,
                        openEndedUntil: RentalDateRules.OpenEndedUntil(
                            r.ActualEndDate,
                            null,
                            HasRentalStarted(r) && r.Items.Any(ri => ri.ReturnedAt == null),
                            expectedEndDay),
                        includeReturnBuffer: ShouldUseReturnBuffer(r),
                        expectedReturnDate: RentalDateRules.EffectiveExpectedReturnDate(r)),
                    startDay,
                    expectedEndDay))
                .ToList();
            var occupiedSpecificItemIds = overlappingRentals
                .SelectMany(r => r.Items.Select(ri => new { Rental = r, RentalItem = ri }))
                .Where(entry => entry.RentalItem.ItemId.HasValue
                    && RentalDateRules.Overlaps(
                        RentalDateRules.OccupancyStartDate(entry.Rental),
                        RentalDateRules.OccupancyEndDate(entry.Rental, entry.RentalItem, expectedEndDay),
                        startDay,
                        expectedEndDay))
                .Select(entry => entry.RentalItem.ItemId!.Value)
                .ToHashSet();

            foreach (var defId in distinctDefs)
            {
                var def = await _context.ItemDefinitions.FindAsync(defId);
                if (def == null) continue;

                var totalStock = await _context.Items.CountAsync(i => i.ItemDefinitionId == defId && i.Status != ItemStatus.Disposed);
                var requestedQty = itemDefIds.Count(id => id == defId);
                var dayCount = (expectedEndDay.Date - startDay.Date).Days + 1;
                var occupancyDiff = new int[dayCount + 1];
                var manualLoanDiff = new int[dayCount + 1];
                var dayWorstRentals = new Rental?[dayCount];

                void AddOccupancy(DateTime start, DateTime end, Rental? rental, bool isManualLoan)
                {
                    var clippedStart = start.Date < startDay.Date ? startDay.Date : start.Date;
                    var clippedEnd = end.Date > expectedEndDay.Date ? expectedEndDay.Date : end.Date;
                    if (clippedEnd < clippedStart)
                    {
                        return;
                    }

                    var startIndex = (clippedStart - startDay.Date).Days;
                    var endIndex = (clippedEnd - startDay.Date).Days;
                    occupancyDiff[startIndex]++;
                    if (endIndex + 1 < occupancyDiff.Length)
                    {
                        occupancyDiff[endIndex + 1]--;
                    }

                    if (isManualLoan)
                    {
                        manualLoanDiff[startIndex]++;
                        if (endIndex + 1 < manualLoanDiff.Length)
                        {
                            manualLoanDiff[endIndex + 1]--;
                        }
                    }

                    if (rental != null)
                    {
                        for (var dayIndex = startIndex; dayIndex <= endIndex; dayIndex++)
                        {
                            dayWorstRentals[dayIndex] ??= rental;
                        }
                    }
                }

                foreach (var rental in overlappingRentals)
                {
                    foreach (var rentalItem in rental.Items.Where(ri =>
                        (ri.ItemId != null && ri.Item != null && ri.Item.ItemDefinitionId == defId) ||
                        (ri.ItemId == null && ri.ItemDefinitionId == defId)))
                    {
                        AddOccupancy(
                            RentalDateRules.OccupancyStartDate(rental),
                            RentalDateRules.OccupancyEndDate(rental, rentalItem, expectedEndDay),
                            rental,
                            isManualLoan: false);
                    }
                }

                foreach (var manualLoan in manualLoanItems.Where(item =>
                    item.ItemDefinitionId == defId
                    && !occupiedSpecificItemIds.Contains(item.Id)))
                {
                    AddOccupancy(
                        RentalDateRules.ToBusinessDate(manualLoan.OutboundAt ?? manualLoan.LastUpdated),
                        RentalDateRules.ManualLoanOccupancyEndDate(
                            manualLoan.ExpectedReturnDate,
                            RentalDateRules.Today(DateTime.UtcNow)),
                        null,
                        isManualLoan: true);
                }

                var occupancy = 0;
                var manualLoanOccupancy = 0;
                var maxOccupancy = 0;
                var maxManualLoanOccupancy = 0;
                Rental? worstRental = null;
                var hasConflict = false;

                for (var dayIndex = 0; dayIndex < dayCount; dayIndex++)
                {
                    occupancy += occupancyDiff[dayIndex];
                    manualLoanOccupancy += manualLoanDiff[dayIndex];

                    if (totalStock - occupancy < requestedQty)
                    {
                        hasConflict = true;
                        if (occupancy > maxOccupancy)
                        {
                            maxOccupancy = occupancy;
                            maxManualLoanOccupancy = manualLoanOccupancy;
                            worstRental = dayWorstRentals[dayIndex];
                        }
                    }
                }

                if (hasConflict && (worstRental != null || maxManualLoanOccupancy > 0))
                {
                    conflicts.Add(new RentalScheduleConflictDto
                    {
                        RentalId = worstRental?.Id ?? Guid.Empty,
                        RentalNumber = worstRental?.RentalNumber ?? "普通借出",
                        RentalStatus = worstRental?.Status ?? RentalStatus.Active,
                        ItemId = Guid.Empty,
                        ItemShortId = $"[分类库存不足] {def.Name}",
                        ItemName = def.Name,
                        StartDate = worstRental != null ? RentalDateRules.ToBusinessDate(worstRental.StartDate) : startDay,
                        ExpectedEndDate = worstRental != null ? RentalDateRules.EffectiveExpectedReturnDate(worstRental) : expectedEndDay,
                        HasRenewalIntent = worstRental?.HasRenewalIntent ?? false,
                        RenewalIntentEndDate = worstRental == null
                            ? null
                            : NormalizeRenewalIntentEndDate(worstRental.HasRenewalIntent, worstRental.RenewalIntentEndDate),
                        HasOutboundShipment = worstRental != null ? HasRentalStarted(worstRental) : true,
                        ConflictReason = $"库存不足（库存: {totalStock}, 占用: {maxOccupancy}, 普通借出: {maxManualLoanOccupancy}, 本单需要: {requestedQty}）"
                    });
                }
            }

            return conflicts;

#if false
            foreach (var defId in distinctDefs)
            {
                var def = await _context.ItemDefinitions.FindAsync(defId);
                if (def == null) continue;

                var totalStock = await _context.Items.CountAsync(i => i.ItemDefinitionId == defId && i.Status != ItemStatus.Disposed);
                var requestedQty = itemDefIds.Count(id => id == defId);
                var definitionManualLoans = manualLoanItems
                    .Where(item => item.ItemDefinitionId == defId)
                    .ToList();

                var currentDay = startDay.Date;
                var maxOccupancy = 0;
                var maxManualLoanOccupancy = 0;
                Rental? worstRental = null;
                var hasConflict = false;

                while (currentDay <= expectedEndDay.Date)
                {
                    var dayRentals = overlappingRentals.ToList();

                    var dayOccupancy = 0;
                    Rental? dayWorstRental = null;

                    foreach (var r in dayRentals)
                    {
                        var count = r.Items.Count(ri =>
                            RentalDateRules.OccupiesBusinessDate(
                                r.ExpectedShipDate,
                                r.ExpectedEndDate,
                                r.Shipments,
                                currentDay,
                                r.ActualEndDate,
                                ri.ReturnedAt,
                                RentalDateRules.OpenEndedUntil(r.ActualEndDate, ri.ReturnedAt, HasRentalStarted(r), currentDay),
                                ShouldUseReturnBuffer(r),
                                RentalDateRules.EffectiveReleasedFromRentalAt(ri)) &&
                            ((ri.ItemId != null && ri.Item != null && ri.Item.ItemDefinitionId == defId) ||
                             (ri.ItemId == null && ri.ItemDefinitionId == defId)));
                        if (count > 0)
                        {
                            dayOccupancy += count;
                            dayWorstRental = r;
                        }
                    }

                    var manualLoanOccupancy = definitionManualLoans.Count(item =>
                        RentalDateRules.ToBusinessDate(item.OutboundAt ?? item.LastUpdated) <= currentDay);
                    dayOccupancy += manualLoanOccupancy;

                    if (totalStock - dayOccupancy < requestedQty)
                    {
                        hasConflict = true;
                        if (dayOccupancy > maxOccupancy)
                        {
                            maxOccupancy = dayOccupancy;
                            worstRental = dayWorstRental;
                            maxManualLoanOccupancy = manualLoanOccupancy;
                        }
                    }

                    currentDay = currentDay.AddDays(1);
                }

                if (hasConflict && (worstRental != null || maxManualLoanOccupancy > 0))
                {
                    conflicts.Add(new RentalScheduleConflictDto
                    {
                        RentalId = worstRental?.Id ?? Guid.Empty,
                        RentalNumber = worstRental?.RentalNumber ?? "普通借出",
                        RentalStatus = worstRental?.Status ?? RentalStatus.Active,
                        ItemId = Guid.Empty,
                        ItemShortId = $"[分类库存不足] {def.Name}",
                        ItemName = def.Name,
                        StartDate = worstRental != null ? RentalDateRules.ToBusinessDate(worstRental.StartDate) : startDay,
                        ExpectedEndDate = worstRental != null ? RentalDateRules.ToBusinessDate(worstRental.ExpectedEndDate) : expectedEndDay,
                        HasOutboundShipment = worstRental != null ? HasRentalStarted(worstRental) : true,
                        ConflictReason = $"库存不足（库存: {totalStock}, 占用: {maxOccupancy}, 普通借出: {maxManualLoanOccupancy}, 本单需要: {requestedQty}）"
                    });
                }
            }

            return conflicts;
#endif
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

        private async Task<RentalCreateConflictDto?> ValidateOutboundShipmentConflictsAsync(
            Guid rentalId,
            IReadOnlyCollection<Guid> itemIds)
        {
            if (itemIds.Count == 0)
            {
                return null;
            }

            var candidateRentals = await _context.Rentals
                .Include(r => r.Items)
                .Include(r => r.Shipments)
                .Where(r => r.Id != rentalId)
                .Where(r => r.Status != RentalStatus.Returned && r.Status != RentalStatus.Cancelled && r.Status != RentalStatus.Renewed)
                .Where(r => r.Items.Any(ri => ri.ItemId.HasValue && itemIds.Contains(ri.ItemId.Value)))
                .ToListAsync();

            var openReturnConflicts = new List<RentalScheduleConflictDto>();
            var returnPendingConflicts = new List<RentalScheduleConflictDto>();

            foreach (var otherRental in candidateRentals)
            {
                var hasStarted = HasRentalStarted(otherRental);
                var hasPendingInbound = HasPendingInboundShipment(otherRental);
                if (!hasStarted && !hasPendingInbound)
                {
                    continue;
                }

                foreach (var rentalItem in otherRental.Items.Where(ri => ri.ItemId.HasValue && itemIds.Contains(ri.ItemId.Value)))
                {
                    if (rentalItem.ReturnedAt == null && hasStarted)
                    {
                        openReturnConflicts.Add(BuildConflict(
                            otherRental,
                            rentalItem,
                            hasStarted,
                            "前一个租赁单尚未登记归还"));
                    }
                    else if (rentalItem.ReturnedAt != null && hasPendingInbound)
                    {
                        returnPendingConflicts.Add(BuildConflict(
                            otherRental,
                            rentalItem,
                            hasStarted,
                            "前一个租赁单回货物流尚未签收"));
                    }
                }
            }

            if (openReturnConflicts.Count == 0 && returnPendingConflicts.Count == 0)
            {
                return null;
            }

            return new RentalCreateConflictDto
            {
                Message = BuildOutboundShipmentConflictMessage(openReturnConflicts, returnPendingConflicts),
                ShippedConflicts = openReturnConflicts
                    .OrderBy(c => c.ItemShortId)
                    .ThenBy(c => c.StartDate)
                    .ToList(),
                ReturnPendingConflicts = returnPendingConflicts
                    .OrderBy(c => c.ItemShortId)
                    .ThenBy(c => c.StartDate)
                    .ToList()
            };
        }

        private static RentalScheduleConflictDto BuildConflict(
            Rental rental,
            RentalItem rentalItem,
            bool hasOutboundShipment,
            string reason) => new()
            {
                RentalId = rental.Id,
                RentalNumber = rental.RentalNumber,
                RentalStatus = rental.Status,
                ItemId = rentalItem.ItemId ?? Guid.Empty,
                ItemShortId = rentalItem.ItemShortIdSnapshot,
                ItemName = rentalItem.ItemNameSnapshot,
                StartDate = RentalDateRules.ToBusinessDate(rental.StartDate),
                ExpectedEndDate = RentalDateRules.EffectiveExpectedReturnDate(rental),
                HasRenewalIntent = rental.HasRenewalIntent,
                RenewalIntentEndDate = NormalizeRenewalIntentEndDate(rental.HasRenewalIntent, rental.RenewalIntentEndDate),
                HasOutboundShipment = hasOutboundShipment,
                ConflictReason = reason
            };

        private static string BuildOutboundShipmentConflictMessage(
            IReadOnlyCollection<RentalScheduleConflictDto> openReturnConflicts,
            IReadOnlyCollection<RentalScheduleConflictDto> returnPendingConflicts)
        {
            var parts = new List<string>();
            if (openReturnConflicts.Count > 0)
            {
                parts.Add($"未归还 {openReturnConflicts.Count} 条");
            }

            if (returnPendingConflicts.Count > 0)
            {
                parts.Add($"回货未签收 {returnPendingConflicts.Count} 条");
            }

            return $"发货物品仍被其他租赁单占用：{string.Join("，", parts)}。确认后仍可继续登记发货。";
        }

        private async Task NotifyStatusChangeAsync(Rental rental, string action, string? currentUser, string? extra = null)
        {
            try
            {
                var title = $"租赁单 {rental.RentalNumber} | {action}";
                var content = await BuildRentalNotificationContentAsync(rental, action, currentUser, extra);

                var targets = await BuildRentalNotificationTargetsAsync(rental, includeAdmins: true);

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

        private async Task<string> BuildRentalNotificationContentAsync(
            Rental rental,
            string action,
            string? currentUser,
            string? extra)
        {
            var snapshot = await _context.Rentals
                .AsNoTracking()
                .Include(r => r.Renter)
                .Include(r => r.Items)
                .Include(r => r.Shipments)
                .FirstOrDefaultAsync(r => r.Id == rental.Id);

            var source = snapshot ?? rental;
            var itemSummary = BuildRentalNotificationItemSummary(source);
            var accountedAmount = source.TotalPrice - source.OtherFee - source.Shipments.Sum(s => s.ShippingFee ?? 0);
            if (accountedAmount < 0)
            {
                accountedAmount = 0;
            }

            var lines = new List<string>
            {
                $"操作：{action}",
                $"状态：{FormatRentalStatus(source.Status)}",
                $"租赁单：{source.RentalNumber}",
                $"租客：{source.Renter?.Name ?? "-"}",
                $"租期：{RentalDateRules.Format(source.StartDate)} - {RentalDateRules.Format(source.ExpectedEndDate)}",
                $"总价：{FormatMoney(source.TotalPrice)}",
                $"核算：{FormatMoney(accountedAmount)}"
            };

            if (!string.IsNullOrWhiteSpace(source.PlatformOrderNo))
            {
                lines.Add($"平台单号：{source.PlatformOrderNo}");
            }

            if (!string.IsNullOrWhiteSpace(source.AssignedTo))
            {
                lines.Add($"负责人：{source.AssignedTo}");
            }

            if (!string.IsNullOrWhiteSpace(source.SenderName))
            {
                lines.Add($"发货人：{source.SenderName}");
            }

            if (!string.IsNullOrWhiteSpace(currentUser))
            {
                lines.Add($"操作人：{currentUser}");
            }

            if (!string.IsNullOrWhiteSpace(extra))
            {
                lines.Add($"备注：{extra}");
            }

            if (!string.IsNullOrWhiteSpace(itemSummary))
            {
                lines.Add($"物品：{itemSummary}");
            }

            return string.Join("\n", lines);
        }

        private static string? BuildRentalNotificationItemSummary(Rental rental)
        {
            var items = rental.Items
                .OrderBy(i => i.Id)
                .ToList();

            if (items.Count == 0)
            {
                return null;
            }

            return string.Join("；", items
                .Select(i => $"{i.ItemShortIdSnapshot} / {i.ItemNameSnapshot}".Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        }

        private static string FormatMoney(decimal value) =>
            $"￥{value.ToString("0.0", CultureInfo.InvariantCulture)}";

        private static string FormatRentalStatus(RentalStatus status) => status switch
        {
            RentalStatus.Pending => "待发货",
            RentalStatus.Active => "进行中",
            RentalStatus.Overdue => "逾期",
            RentalStatus.Returned => "已归还",
            RentalStatus.Cancelled => "已取消",
            RentalStatus.Renewed => "已续租",
            _ => status.ToString()
        };

        private async Task NotifyShipmentExceptionAsync(
            Rental rental,
            RentalShipment shipment,
            SfRouteQueryResult route,
            string? currentUser,
            CancellationToken ct)
        {
            var existingOpen = await _context.Reminders.AnyAsync(r =>
                r.RelatedEntityType == "RentalShipment"
                && r.RelatedEntityId == shipment.Id.ToString()
                && r.Type == ReminderType.Manual
                && r.DismissedAt == null
                && r.Title.Contains("物流异常"), ct);
            if (existingOpen)
            {
                return;
            }

            var targets = await BuildRentalNotificationTargetsAsync(rental, includeAdmins: true);

            if (targets.Count == 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var message = $"租赁单 {rental.RentalNumber} 的顺丰运单 {route.TrackingNumber} 出现异常：{route.ExceptionMessage ?? "请查看顺丰路由"}";
            var reminders = targets.Select(target => new Reminder
            {
                Type = ReminderType.Manual,
                Level = ReminderLevel.Warning,
                RelatedEntityType = "RentalShipment",
                RelatedEntityId = shipment.Id.ToString(),
                Title = $"租赁 {rental.RentalNumber} 顺丰物流异常",
                Message = message,
                TargetUser = target,
                DueAt = now,
                CreatedAt = now
            }).ToList();

            _context.Reminders.AddRange(reminders);
            await _context.SaveChangesAsync(ct);

            foreach (var reminder in reminders)
            {
                foreach (var channel in _notificationChannels)
                {
                    try
                    {
                        await channel.DeliverAsync(reminder, ct);
                    }
                    catch
                    {
                        // Reminder side-channel failures should not block route sync.
                    }
                }
            }
        }

        private async Task DismissOpenRentalAutoRemindersAsync(Guid rentalId, string? currentUser, params ReminderType[] types)
        {
            var targetTypes = types.Length == 0
                ? new[]
                {
                    ReminderType.RentalShipmentSoon,
                    ReminderType.RentalDueSoon,
                    ReminderType.RentalOverdue,
                    ReminderType.RentalDeliveryUnsigned,
                    ReminderType.RentalReturnUnsigned
                }
                : types;

            var openAutoReminders = await _context.Reminders
                .Where(r => r.RelatedEntityType == "Rental"
                    && r.RelatedEntityId == rentalId.ToString()
                    && r.DismissedAt == null
                    && targetTypes.Contains(r.Type))
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

        private async Task<(string rentalNumber, int sequence)> GenerateRenewalRentalNumberAsync(Rental source)
        {
            var rootNumber = GetRenewalRootNumber(source);
            var prefix = $"{rootNumber}-";
            var relatedNumbers = await _context.Rentals
                .Where(r => r.RentalNumber == rootNumber || r.RentalNumber.StartsWith(prefix))
                .Select(r => r.RentalNumber)
                .ToListAsync();

            var maxSequence = relatedNumbers
                .Select(number => TryReadRenewalSequence(rootNumber, number))
                .DefaultIfEmpty(0)
                .Max();
            var nextSequence = maxSequence + 1;
            return ($"{rootNumber}-{nextSequence:D2}", nextSequence);
        }

        private static int TryReadRenewalSequence(string rootNumber, string rentalNumber)
        {
            if (!rentalNumber.StartsWith($"{rootNumber}-", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            var suffix = rentalNumber[(rootNumber.Length + 1)..];
            return int.TryParse(suffix, out var sequence) ? sequence : 0;
        }

        private static string GetRenewalRootNumber(Rental source)
        {
            if (source.RenewalSequence is > 0)
            {
                var suffix = $"-{source.RenewalSequence.Value:D2}";
                if (source.RentalNumber.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return source.RentalNumber[..^suffix.Length];
                }
            }

            if (!string.IsNullOrWhiteSpace(source.RenewedFromRentalNumber))
            {
                return TrimRenewalSuffix(source.RenewedFromRentalNumber);
            }

            return TrimRenewalSuffix(source.RentalNumber);
        }

        private static string TrimRenewalSuffix(string rentalNumber)
        {
            var index = rentalNumber.LastIndexOf('-');
            if (index <= 0 || rentalNumber.Length - index - 1 != 2)
            {
                return rentalNumber;
            }

            var suffix = rentalNumber[(index + 1)..];
            return int.TryParse(suffix, out _) ? rentalNumber[..index] : rentalNumber;
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

        private void LogManualLoanAudit(Item item, string? user, string destination)
        {
            _context.AuditLogs.Add(new AuditLog
            {
                Timestamp = DateTime.UtcNow,
                Action = AuditAction.Outbound,
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

        private static bool IsRenewal(Rental rental) =>
            rental.RenewedFromRentalId.HasValue;

        private static bool HasRentalStarted(Rental rental) =>
            IsRenewal(rental) || HasOutboundShipment(rental);

        private static bool ShouldUseReturnBuffer(Rental rental) =>
            rental.Status != RentalStatus.Renewed
            && !rental.RenewedToRentalId.HasValue;

        private static bool IsClosedStatus(RentalStatus status) =>
            status is RentalStatus.Returned or RentalStatus.Cancelled or RentalStatus.Renewed;

        private static bool HasInboundShipment(Rental rental) =>
            rental.Shipments.Any(s => s.Direction == ShipmentDirection.Inbound);

        private static bool HasDeliveredOutboundShipment(Rental rental) =>
            rental.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound && s.DeliveredAt.HasValue);

        private static bool HasDeliveredInboundShipmentForAllOpenItems(Rental rental)
        {
            var openItems = rental.Items
                .Where(item => !item.ReturnedAt.HasValue)
                .ToList();
            if (openItems.Count == 0)
            {
                return true;
            }

            var deliveredInboundShipments = rental.Shipments
                .Where(shipment => shipment.Direction == ShipmentDirection.Inbound && shipment.DeliveredAt.HasValue)
                .ToList();

            // An empty link set is the legacy/whole-rental representation. Keep it
            // covering the whole rental so existing shipments retain their meaning.
            if (deliveredInboundShipments.Any(shipment => shipment.RentalItems.Count == 0))
            {
                return true;
            }

            var coveredItemIds = deliveredInboundShipments
                .SelectMany(shipment => shipment.RentalItems.Select(link => link.RentalItemId))
                .ToHashSet();

            return openItems.All(item => coveredItemIds.Contains(item.Id));
        }

        private static bool HasPendingInboundShipment(Rental rental) =>
            rental.Shipments.Any(s => s.Direction == ShipmentDirection.Inbound && !s.DeliveredAt.HasValue);

        private static bool IsSfTrackingNumber(string? trackingNumber) =>
            !string.IsNullOrWhiteSpace(trackingNumber)
            && trackingNumber.Trim().StartsWith("SF", StringComparison.OrdinalIgnoreCase);

        private async Task<string?> ResolveCreatorPhoneTailAsync(string? createdBy, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(createdBy))
            {
                return null;
            }

            var creatorName = createdBy.Trim();
            var mobile = await _context.Users
                .AsNoTracking()
                .Where(u => u.Name == creatorName)
                .Select(u => u.Mobile)
                .FirstOrDefaultAsync(ct);

            return ResolvePhoneTail(mobile);
        }

        private async Task<IReadOnlyList<string>> ResolveAdminPhoneTailsAsync(CancellationToken ct)
        {
            var mobiles = await _context.UserRoles
                .AsNoTracking()
                .Where(userRole => userRole.Role != null
                    && userRole.Role.Name == BuiltInRoles.Admin
                    && userRole.User != null
                    && userRole.User.Status == UserStatus.Active)
                .OrderBy(userRole => userRole.User!.Name)
                .Select(userRole => userRole.User!.Mobile)
                .ToListAsync(ct);

            return mobiles
                .Select(ResolvePhoneTail)
                .Where(phoneTail => !string.IsNullOrWhiteSpace(phoneTail))
                .Select(phoneTail => phoneTail!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static List<SfRouteQueryItem> BuildSfRouteQueryItems(
            IEnumerable<RentalShipment> shipments,
            string phoneTail) =>
            shipments.Select(shipment => new SfRouteQueryItem
                {
                    ShipmentId = shipment.Id,
                    TrackingNumber = shipment.TrackingNumber!,
                    CheckPhoneNo = phoneTail
                })
                .ToList();

        private static bool ShouldFallbackToAlternativePhone(SfRouteQueryResult route) =>
            route.Routes.Count == 0;

        private static string? ResolvePhoneTail(string? phone)
        {
            var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());
            return digits.Length < 4 ? null : digits[^4..];
        }

        private static bool IsWithin(DateTime value, DateTime from, DateTime to) =>
            value >= from && value <= to;

        private static IEnumerable<RentalCalendarEventDto> DeduplicateCalendarEvents(
            IEnumerable<RentalCalendarEventDto> events)
        {
            var list = events.ToList();
            var syntheticKeys = list
                .Where(e => e.Kind != RentalCalendarEventKind.Reminder)
                .Select(BuildCalendarDedupeKey)
                .Where(key => key != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in list)
            {
                var key = BuildCalendarDedupeKey(item);
                if (key == null)
                {
                    yield return item;
                    continue;
                }

                if (item.Kind == RentalCalendarEventKind.Reminder
                    && IsRentalAutoReminder(item)
                    && syntheticKeys.Contains(key))
                {
                    continue;
                }

                if (!seen.Add(key))
                {
                    continue;
                }

                yield return item;
            }
        }

        private static string? BuildCalendarDedupeKey(RentalCalendarEventDto item)
        {
            if (item.Kind == RentalCalendarEventKind.Reminder && IsRentalAutoReminder(item))
            {
                if (!item.RentalId.HasValue)
                {
                    return null;
                }

                if (item.ReminderType == ReminderType.RentalDeliveryUnsigned)
                {
                    return $"rental:{item.RentalId}:DeliveryUnsigned:{item.StartAt.Date:O}";
                }

                if (item.ReminderType == ReminderType.RentalReturnUnsigned)
                {
                    return $"rental:{item.RentalId}:ReturnUnsigned:{item.StartAt.Date:O}";
                }

                var syntheticKind = item.ReminderType == ReminderType.RentalShipmentSoon
                    ? RentalCalendarEventKind.ShipmentRequired
                    : RentalCalendarEventKind.ReturnRequired;

                return $"rental:{item.RentalId}:{syntheticKind}:{item.StartAt.Date:O}";
            }

            return item.Kind switch
            {
                RentalCalendarEventKind.ShipmentRequired or RentalCalendarEventKind.ReturnRequired =>
                    item.RentalId.HasValue ? $"rental:{item.RentalId}:{item.Kind}:{item.StartAt.Date:O}" : null,
                RentalCalendarEventKind.RentalPeriod =>
                    item.RentalId.HasValue
                        ? $"rental:{item.RentalId}:{item.Kind}:{item.StartAt.Date:O}:{item.EndAt.Date:O}"
                        : null,
                RentalCalendarEventKind.OutboundShipment or RentalCalendarEventKind.InboundShipment =>
                    item.Id,
                _ => item.Id
            };
        }

        private static bool IsRentalAutoReminder(RentalCalendarEventDto item) =>
            item.ReminderType == ReminderType.RentalShipmentSoon
            || item.ReminderType == ReminderType.RentalDueSoon
            || item.ReminderType == ReminderType.RentalOverdue
            || item.ReminderType == ReminderType.RentalDeliveryUnsigned
            || item.ReminderType == ReminderType.RentalReturnUnsigned;

        private static bool IsCompletedAutoReminder(
            Reminder reminder,
            Guid? rentalId,
            IReadOnlyDictionary<Guid, Rental> rentals)
        {
            if (!IsRentalAutoReminder(reminder.Type))
            {
                return false;
            }

            if (!rentalId.HasValue || !rentals.TryGetValue(rentalId.Value, out var rental))
            {
                return false;
            }

            if (rental.Status is RentalStatus.Cancelled or RentalStatus.Renewed
                || rental.RenewedToRentalId.HasValue)
            {
                return true;
            }

            if (rental.Status == RentalStatus.Returned && reminder.Type != ReminderType.RentalReturnUnsigned)
            {
                return true;
            }

            return reminder.Type switch
            {
                ReminderType.RentalShipmentSoon => HasOutboundShipment(rental) || IsRenewal(rental),
                ReminderType.RentalDeliveryUnsigned => HasDeliveredOutboundShipment(rental),
                ReminderType.RentalDueSoon or ReminderType.RentalOverdue =>
                    HasInboundShipment(rental) || !rental.Items.Any(i => i.ReturnedAt == null),
                ReminderType.RentalReturnUnsigned =>
                    HasDeliveredInboundShipmentForAllOpenItems(rental)
                    || (rental.Status == RentalStatus.Returned && !HasInboundShipment(rental)),
                _ => false
            };
        }

        private static DateTime ResolveReminderCalendarEnd(Reminder reminder, DateTime rangeEnd)
        {
            if (!IsRentalAutoReminder(reminder.Type))
            {
                return reminder.DueAt;
            }

            var dueDate = RentalDateRules.ToBusinessDate(reminder.DueAt);
            var today = RentalDateRules.Today(DateTime.UtcNow);
            if (reminder.DismissedAt != null || dueDate >= today)
            {
                return dueDate;
            }

            return today > rangeEnd ? rangeEnd.Date : today;
        }

        private static bool IsRentalAutoReminder(ReminderType type) =>
            type == ReminderType.RentalShipmentSoon
            || type == ReminderType.RentalDueSoon
            || type == ReminderType.RentalOverdue
            || type == ReminderType.RentalDeliveryUnsigned
            || type == ReminderType.RentalReturnUnsigned;

        private static string ResolveCalendarTarget(string? requestedTarget, string? currentUser, bool canSeeAll)
        {
            var requested = requestedTarget?.Trim();
            if (canSeeAll && !string.IsNullOrWhiteSpace(requested))
            {
                return requested;
            }

            return string.IsNullOrWhiteSpace(currentUser) ? "all" : currentUser.Trim();
        }

        private static bool IsRentalForUser(Rental rental, string targetUser)
        {
            if (string.IsNullOrWhiteSpace(targetUser)
                || string.Equals(targetUser, "all", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(rental.CreatedBy, targetUser, StringComparison.OrdinalIgnoreCase)
                || SplitUsers(rental.AssignedTo).Any(user => string.Equals(user, targetUser, StringComparison.OrdinalIgnoreCase))
                || SplitUsers(rental.SenderName).Any(user => string.Equals(user, targetUser, StringComparison.OrdinalIgnoreCase))
                || rental.Shipments.Any(shipment =>
                    shipment.Direction == ShipmentDirection.Outbound
                    && string.Equals(shipment.CreatedBy, targetUser, StringComparison.OrdinalIgnoreCase));
        }

        private static Guid? ParseRentalId(Reminder reminder)
        {
            if (string.Equals(reminder.RelatedEntityType, "Rental", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(reminder.RelatedEntityId, out var rentalId))
            {
                return rentalId;
            }

            return null;
        }

        private static string BuildCalendarShipmentDescription(RentalShipment shipment)
        {
            var parts = new List<string> { shipment.Carrier };
            if (!string.IsNullOrWhiteSpace(shipment.TrackingNumber))
            {
                parts.Add(shipment.TrackingNumber);
            }

            if (shipment.DeliveredAt.HasValue)
            {
                parts.Add("已签收");
            }

            return string.Join(" | ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        private static string BuildListingRemarks(Item item) =>
            string.Join("; ", item.Listings
                .Where(l => l.Status == ListingStatus.Listed)
                .Select(l => $"{l.Platform}: {l.Remarks ?? "无备注"}"));

        private static IEnumerable<string> SplitUsers(string? users) =>
            string.IsNullOrWhiteSpace(users)
                ? Array.Empty<string>()
                : users.Split(new[] { ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private async Task<HashSet<string>> BuildRentalNotificationTargetsAsync(Rental rental, bool includeAdmins)
        {
            var snapshot = await _context.Rentals
                .AsNoTracking()
                .Include(r => r.Shipments)
                .FirstOrDefaultAsync(r => r.Id == rental.Id);
            var source = snapshot ?? rental;

            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddNotificationTarget(targets, source.CreatedBy);
            foreach (var assignedUser in SplitUsers(source.AssignedTo))
            {
                AddNotificationTarget(targets, assignedUser);
            }

            foreach (var sender in SplitUsers(source.SenderName))
            {
                AddNotificationTarget(targets, sender);
            }

            foreach (var shipper in source.Shipments
                .Where(shipment => shipment.Direction == ShipmentDirection.Outbound)
                .Select(shipment => shipment.CreatedBy))
            {
                AddNotificationTarget(targets, shipper);
            }

            if (includeAdmins)
            {
                var admins = await _identityService.GetUsersInRoleAsync(BuiltInRoles.Admin);
                foreach (var admin in admins)
                {
                    AddNotificationTarget(targets, admin);
                }
            }

            return targets;
        }

        private static void AddNotificationTarget(HashSet<string> targets, string? user)
        {
            if (!string.IsNullOrWhiteSpace(user))
            {
                targets.Add(user.Trim());
            }
        }

        private static string? NormalizeNullableText(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static DateTime? NormalizeRenewalIntentEndDate(bool hasRenewalIntent, DateTime? renewalIntentEndDate) =>
            hasRenewalIntent && renewalIntentEndDate.HasValue
                ? RentalDateRules.ToBusinessDate(renewalIntentEndDate.Value)
                : null;

        private static string? ValidateRenewalIntent(bool hasRenewalIntent, DateTime? renewalIntentEndDate, DateTime expectedEndDate)
        {
            if (!hasRenewalIntent)
            {
                return null;
            }

            if (!renewalIntentEndDate.HasValue)
            {
                return "选择续租意愿为是时，必须填写续租意愿日期。";
            }

            if (RentalDateRules.ToBusinessDate(renewalIntentEndDate.Value) < RentalDateRules.ToBusinessDate(expectedEndDate))
            {
                return "续租意愿日期不能早于预计结束日期。";
            }

            return null;
        }

        private static string FormatRenewalIntent(bool hasRenewalIntent, DateTime? renewalIntentEndDate) =>
            hasRenewalIntent && renewalIntentEndDate.HasValue
                ? $"是，至 {RentalDateRules.Format(renewalIntentEndDate.Value)}"
                : "否";

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

        private static SfShipmentRouteDto ToSfShipmentRouteDto(SfRouteQueryResult result, bool autoDelivered) => new()
        {
            ShipmentId = result.ShipmentId,
            TrackingNumber = result.TrackingNumber,
            CheckPhoneNo = result.CheckPhoneNo,
            Queryable = result.Queryable,
            FromCache = result.FromCache,
            QueriedAt = result.QueriedAt,
            ServiceCode = result.ServiceCode,
            TrackingType = result.TrackingType,
            MethodType = result.MethodType,
            Error = result.Error,
            DeliveredAt = result.DeliveredAt,
            AutoDelivered = autoDelivered,
            HasException = result.HasException,
            ExceptionMessage = result.ExceptionMessage,
            Routes = result.Routes
        };

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
            StartDate = RentalDateRules.ToBusinessDate(rental.StartDate),
            ExpectedShipDate = RentalDateRules.ToBusinessDate(rental.ExpectedShipDate),
            ExpectedEndDate = RentalDateRules.ToBusinessDate(rental.ExpectedEndDate),
            ExpectedReturnDate = rental.ExpectedReturnDate.HasValue
                ? RentalDateRules.ToBusinessDate(rental.ExpectedReturnDate.Value)
                : null,
            ActualEndDate = rental.ActualEndDate,
            HasRenewalIntent = rental.HasRenewalIntent,
            RenewalIntentEndDate = NormalizeRenewalIntentEndDate(rental.HasRenewalIntent, rental.RenewalIntentEndDate),
            TotalPrice = rental.TotalPrice,
            Deposit = rental.Deposit,
            OtherFee = rental.OtherFee,
            TotalShippingFee = rental.Shipments.Sum(s => s.ShippingFee ?? 0),
            AccountedAmount = rental.TotalPrice - rental.OtherFee - rental.Shipments.Sum(s => s.ShippingFee ?? 0),
            ShippingAddress = rental.ShippingAddress,
            PlatformOrderNo = rental.PlatformOrderNo,
            RenewedFromRentalId = rental.RenewedFromRentalId,
            RenewedFromRentalNumber = rental.RenewedFromRentalNumber,
            RenewedToRentalId = rental.RenewedToRentalId,
            RenewedToRentalNumber = rental.RenewedToRentalNumber,
            RenewalSequence = rental.RenewalSequence,
            IsRenewal = rental.RenewedFromRentalId.HasValue,
            Notes = rental.Notes,
            CreatedAt = rental.CreatedAt,
            CreatedBy = rental.CreatedBy,
            UpdatedAt = rental.UpdatedAt,
            UpdatedBy = rental.UpdatedBy,
            SettlementNotifiedAt = rental.SettlementNotifiedAt,
            SettlementNotifiedStatus = rental.SettlementNotifiedStatus,
            AssignedTo = rental.AssignedTo,
            SenderName = rental.SenderName,
            Items = rental.Items.Select(ToItemDto).ToList(),
            Shipments = rental.Shipments.Select(ToShipmentDto).ToList()
        };

        private static RentalItemDto ToItemDto(RentalItem rentalItem)
        {
            var definition = rentalItem.ItemDefinition ?? rentalItem.Item?.ItemDefinition;

            return new RentalItemDto
            {
                Id = rentalItem.Id,
                ItemId = rentalItem.ItemId,
                ItemDefinitionId = rentalItem.ItemDefinitionId,
                CategoryId = definition?.CategoryId,
                CategoryName = definition?.Category?.Name ?? string.Empty,
                ItemShortIdSnapshot = rentalItem.ItemShortIdSnapshot,
                ItemNameSnapshot = rentalItem.ItemNameSnapshot,
                PerItemPrice = rentalItem.PerItemPrice ?? 0m,
                ReturnedAt = rentalItem.ReturnedAt,
                ReturnCondition = rentalItem.ReturnCondition,
                ReturnNotes = rentalItem.ReturnNotes,
                ListingRemarks = rentalItem.ListingRemarksSnapshot
            };
        }

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
            CreatedBy = shipment.CreatedBy,
            Items = shipment.RentalItems.Select(link => new RentalShipmentItemDto
            {
                RentalItemId = link.RentalItemId,
                ItemId = link.RentalItem?.ItemId,
                ItemShortIdSnapshot = link.RentalItem?.ItemShortIdSnapshot ?? string.Empty,
                ItemNameSnapshot = link.RentalItem?.ItemNameSnapshot ?? string.Empty,
            }).ToList()
        };
    }
}

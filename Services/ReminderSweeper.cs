using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuditIt.Api.Services
{
    public class ReminderSweeper : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IOptionsMonitor<ReminderOptions> _options;
        private readonly ILogger<ReminderSweeper> _logger;

        public ReminderSweeper(
            IServiceScopeFactory scopeFactory,
            IOptionsMonitor<ReminderOptions> options,
            ILogger<ReminderSweeper> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepOnceAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ReminderSweeper sweep failed.");
                }

                var interval = Math.Max(1, _options.CurrentValue.SweepIntervalMinutes);
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(interval), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private async Task SweepOnceAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var identity = scope.ServiceProvider.GetRequiredService<IIdentityService>();
            var channels = scope.ServiceProvider.GetServices<INotificationChannel>().ToList();

            var now = DateTime.UtcNow;
            var today = RentalDateRules.Today(now);
            var leadHours = Math.Max(1, _options.CurrentValue.DueSoonLeadHours);
            var leadUntil = today.AddDays(RentalDateRules.LeadDaysFromHours(leadHours));

            var newlyOverdue = await db.Rentals
                .Include(r => r.Shipments)
                .Where(r => r.Status == RentalStatus.Active)
                .ToListAsync(ct);
            newlyOverdue = newlyOverdue
                .Where(r => !r.Shipments.Any(s => s.Direction == ShipmentDirection.Inbound)
                    && !r.RenewedToRentalId.HasValue
                    && HasRentalStarted(r)
                    && RentalDateRules.IsOverdue(r.ExpectedEndDate, now))
                .ToList();

            foreach (var rental in newlyOverdue)
            {
                rental.Status = RentalStatus.Overdue;
            }

            if (newlyOverdue.Count > 0)
            {
                await db.SaveChangesAsync(ct);
            }

            await DismissCompletedAutoRemindersAsync(db, ct);

            var candidates = await db.Rentals
                .Include(r => r.Renter)
                .Include(r => r.Shipments)
                .Include(r => r.Items)
                .Where(r => r.Status == RentalStatus.Pending
                         || r.Status == RentalStatus.Active
                         || r.Status == RentalStatus.Overdue
                         || r.Status == RentalStatus.Returned)
                .ToListAsync(ct);

            List<string>? managers = null;
            var created = new List<Reminder>();

            foreach (var rental in candidates)
            {
                var startDate = RentalDateRules.ToBusinessDate(rental.StartDate);
                var expectedShipDate = RentalDateRules.ToBusinessDate(rental.ExpectedShipDate);
                var expectedEndDate = RentalDateRules.ToBusinessDate(rental.ExpectedEndDate);
                var hasOutboundShipment = rental.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound);
                var hasInboundShipment = rental.Shipments.Any(s => s.Direction == ShipmentDirection.Inbound);
                var hasDeliveredOutboundShipment = rental.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound && s.DeliveredAt.HasValue);
                var hasDeliveredInboundShipment = rental.Shipments.Any(s => s.Direction == ShipmentDirection.Inbound && s.DeliveredAt.HasValue);
                var isRenewal = rental.RenewedFromRentalId.HasValue;
                var hasRentalStarted = hasOutboundShipment || isRenewal;
                var isRenewedForward = rental.RenewedToRentalId.HasValue;
                ReminderType? type = null;

                if (rental.Status == RentalStatus.Returned
                    && hasInboundShipment
                    && !hasDeliveredInboundShipment)
                {
                    type = ReminderType.RentalReturnUnsigned;
                }
                else if (!hasOutboundShipment
                    && !isRenewal
                    && rental.Status == RentalStatus.Pending
                    && expectedShipDate <= leadUntil)
                {
                    type = ReminderType.RentalShipmentSoon;
                }
                else if (hasOutboundShipment
                         && !hasDeliveredOutboundShipment
                         && startDate.AddDays(1) <= today)
                {
                    type = ReminderType.RentalDeliveryUnsigned;
                }
                else if (hasRentalStarted
                         && !isRenewedForward
                         && !hasInboundShipment
                         && expectedEndDate.AddDays(1) < today)
                {
                    type = ReminderType.RentalOverdue;
                }
                else if (hasRentalStarted
                         && !isRenewedForward
                         && !hasInboundShipment
                         && expectedEndDate >= today
                         && expectedEndDate <= leadUntil)
                {
                    type = ReminderType.RentalDueSoon;
                }

                if (!type.HasValue)
                {
                    continue;
                }

                var targets = new HashSet<string?>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(rental.CreatedBy))
                {
                    targets.Add(rental.CreatedBy.Trim());
                }

                if (type != ReminderType.RentalReturnUnsigned && !string.IsNullOrWhiteSpace(rental.AssignedTo))
                {
                    foreach (var name in rental.AssignedTo.Split(new[] { ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        targets.Add(name);
                    }
                }

                if (type == ReminderType.RentalOverdue)
                {
                    managers ??= (await identity.GetUsersWithPermissionAsync(PermissionCodes.RentalCancel)).ToList();
                    foreach (var manager in managers)
                    {
                        targets.Add(manager);
                    }
                }

                if (targets.Count == 0)
                {
                    targets.Add(null);
                }

                foreach (var target in targets)
                {
                    var reminderDate = type == ReminderType.RentalReturnUnsigned
                        ? today
                        : (DateTime?)null;
                    if (await HasOpenReminderAsync(db, rental.Id.ToString(), type.Value, target, reminderDate, ct))
                    {
                        continue;
                    }

                    created.Add(BuildReminder(rental, type.Value, target, now));
                }
            }

            if (created.Count == 0)
            {
                return;
            }

            db.Reminders.AddRange(created);
            await db.SaveChangesAsync(ct);

            foreach (var reminder in created)
            {
                foreach (var channel in channels)
                {
                    try
                    {
                        await channel.DeliverAsync(reminder, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Notification channel {Channel} failed for reminder {Id}", channel.Name, reminder.Id);
                    }
                }
            }
        }

        private static Task<bool> HasOpenReminderAsync(
            ApplicationDbContext db,
            string entityId,
            ReminderType type,
            string? target,
            DateTime? dueDate,
            CancellationToken ct)
        {
            return db.Reminders.AnyAsync(r =>
                r.RelatedEntityType == "Rental"
                && r.RelatedEntityId == entityId
                && r.Type == type
                && r.TargetUser == target
                && (dueDate.HasValue
                    ? r.DueAt.Date == dueDate.Value.Date
                    : r.DismissedAt == null), ct);
        }

        private static Reminder BuildReminder(Rental rental, ReminderType type, string? target, DateTime now)
        {
            var renterName = rental.Renter?.Name ?? "租客";

            return type switch
            {
                ReminderType.RentalShipmentSoon => new Reminder
                {
                    Type = type,
                    Level = ReminderLevel.Warning,
                    RelatedEntityType = "Rental",
                    RelatedEntityId = rental.Id.ToString(),
                    TargetUser = target,
                    Title = $"租赁 {rental.RentalNumber} 到达预计发货日",
                    Message = $"{renterName} 的订单预计 {RentalDateRules.Format(rental.ExpectedShipDate)} 发货，租期 {RentalDateRules.Format(rental.StartDate)} 开始，请及时确认物流信息。",
                    DueAt = RentalDateRules.ToBusinessDate(rental.ExpectedShipDate),
                    CreatedAt = now
                },
                ReminderType.RentalDeliveryUnsigned => new Reminder
                {
                    Type = type,
                    Level = ReminderLevel.Warning,
                    RelatedEntityType = "Rental",
                    RelatedEntityId = rental.Id.ToString(),
                    TargetUser = target,
                    Title = $"租赁 {rental.RentalNumber} 发货物流未签收",
                    Message = $"{renterName} 的订单租期已于 {RentalDateRules.Format(rental.StartDate)} 开始，发货物流仍未登记签收，请及时确认。",
                    DueAt = RentalDateRules.ToBusinessDate(rental.StartDate).AddDays(1),
                    CreatedAt = now
                },
                ReminderType.RentalOverdue => new Reminder
                {
                    Type = type,
                    Level = ReminderLevel.Critical,
                    RelatedEntityType = "Rental",
                    RelatedEntityId = rental.Id.ToString(),
                    TargetUser = target,
                    Title = $"租赁 {rental.RentalNumber} 已逾期",
                    Message = $"{renterName} 的订单应于 {RentalDateRules.Format(rental.ExpectedEndDate)} 结束，目前已逾期。",
                    DueAt = RentalDateRules.ToBusinessDate(rental.ExpectedEndDate),
                    CreatedAt = now
                },
                ReminderType.RentalReturnUnsigned => new Reminder
                {
                    Type = type,
                    Level = ReminderLevel.Warning,
                    RelatedEntityType = "Rental",
                    RelatedEntityId = rental.Id.ToString(),
                    TargetUser = target,
                    Title = $"租赁 {rental.RentalNumber} 回货物流未签收",
                    Message = $"{renterName} 的订单已登记归还，但回货物流还没有确认签收，请今天跟进。",
                    DueAt = RentalDateRules.Today(now),
                    CreatedAt = now
                },
                _ => new Reminder
                {
                    Type = ReminderType.RentalDueSoon,
                    Level = ReminderLevel.Warning,
                    RelatedEntityType = "Rental",
                    RelatedEntityId = rental.Id.ToString(),
                    TargetUser = target,
                    Title = $"租赁 {rental.RentalNumber} 即将到期",
                    Message = $"{renterName} 的订单将于 {RentalDateRules.Format(rental.ExpectedEndDate)} 到期，请提前跟进。",
                    DueAt = RentalDateRules.ToBusinessDate(rental.ExpectedEndDate),
                    CreatedAt = now
                }
            };
        }

        private static async Task DismissCompletedAutoRemindersAsync(ApplicationDbContext db, CancellationToken ct)
        {
            var reminders = await db.Reminders
                .Where(r => r.RelatedEntityType == "Rental"
                    && r.DismissedAt == null
                    && (r.Type == ReminderType.RentalShipmentSoon
                        || r.Type == ReminderType.RentalDueSoon
                        || r.Type == ReminderType.RentalOverdue
                        || r.Type == ReminderType.RentalDeliveryUnsigned
                        || r.Type == ReminderType.RentalReturnUnsigned))
                .ToListAsync(ct);

            if (reminders.Count == 0)
            {
                return;
            }

            var rentalIds = reminders
                .Select(r => Guid.TryParse(r.RelatedEntityId, out var id) ? id : (Guid?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();

            var rentals = await db.Rentals
                .Include(r => r.Items)
                .Include(r => r.Shipments)
                .Where(r => rentalIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, ct);

            var now = DateTime.UtcNow;
            var changed = false;
            foreach (var reminder in reminders)
            {
                if (!Guid.TryParse(reminder.RelatedEntityId, out var rentalId)
                    || !rentals.TryGetValue(rentalId, out var rental)
                    || !IsCompleted(reminder.Type, rental))
                {
                    continue;
                }

                reminder.DismissedAt = now;
                reminder.DismissedBy = "system";
                changed = true;
            }

            if (changed)
            {
                await db.SaveChangesAsync(ct);
            }
        }

        private static bool IsCompleted(ReminderType type, Rental rental)
        {
            if (rental.Status == RentalStatus.Cancelled
                || rental.Status == RentalStatus.Renewed
                || rental.RenewedToRentalId.HasValue)
            {
                return true;
            }

            if (rental.Status == RentalStatus.Returned && type != ReminderType.RentalReturnUnsigned)
            {
                return true;
            }

            return type switch
            {
                ReminderType.RentalShipmentSoon =>
                    rental.RenewedFromRentalId.HasValue
                    || rental.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound),
                ReminderType.RentalDeliveryUnsigned =>
                    rental.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound && s.DeliveredAt.HasValue),
                ReminderType.RentalDueSoon or ReminderType.RentalOverdue =>
                    rental.Shipments.Any(s => s.Direction == ShipmentDirection.Inbound)
                    || !rental.Items.Any(i => i.ReturnedAt == null),
                ReminderType.RentalReturnUnsigned =>
                    rental.Shipments.Any(s => s.Direction == ShipmentDirection.Inbound && s.DeliveredAt.HasValue)
                    || (rental.Status == RentalStatus.Returned
                        && !rental.Shipments.Any(s => s.Direction == ShipmentDirection.Inbound)),
                _ => false
            };
        }

        private static bool HasRentalStarted(Rental rental) =>
            rental.RenewedFromRentalId.HasValue
            || rental.Shipments.Any(s => s.Direction == ShipmentDirection.Outbound);
    }
}

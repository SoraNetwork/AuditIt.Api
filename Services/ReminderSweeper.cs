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
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) { return; }

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
                try { await Task.Delay(TimeSpan.FromMinutes(interval), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task SweepOnceAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var identity = scope.ServiceProvider.GetRequiredService<IIdentityService>();
            var channels = scope.ServiceProvider.GetServices<INotificationChannel>().ToList();

            var now = DateTime.UtcNow;
            var leadHours = Math.Max(1, _options.CurrentValue.DueSoonLeadHours);
            var dueSoonCutoff = now.AddHours(leadHours);

            var newlyOverdue = await db.Rentals
                .Where(r => (r.Status == RentalStatus.Active || r.Status == RentalStatus.Pending)
                            && r.ExpectedEndDate < now)
                .ToListAsync(ct);
            foreach (var r in newlyOverdue) r.Status = RentalStatus.Overdue;
            if (newlyOverdue.Count > 0) await db.SaveChangesAsync(ct);

            var candidates = await db.Rentals
                .Include(r => r.Renter)
                .Where(r => r.Status == RentalStatus.Active
                         || r.Status == RentalStatus.Pending
                         || r.Status == RentalStatus.Overdue)
                .ToListAsync(ct);

            // 升级提醒时需要的 Manager 名单（拥有 RentalCancel 权限的活跃用户视作 Manager）。延迟解析，仅当真有逾期时才查。
            List<string>? managers = null;

            var created = new List<Reminder>();

            foreach (var r in candidates)
            {
                var entityId = r.Id.ToString();
                ReminderType? type = null;
                if (r.ExpectedEndDate < now) type = ReminderType.RentalOverdue;
                else if (r.ExpectedEndDate <= dueSoonCutoff) type = ReminderType.RentalDueSoon;
                if (type == null) continue;

                var targets = new HashSet<string?>();
                if (!string.IsNullOrWhiteSpace(r.CreatedBy)) targets.Add(r.CreatedBy);
                if (!string.IsNullOrWhiteSpace(r.AssignedTo)) targets.Add(r.AssignedTo);

                if (type == ReminderType.RentalOverdue)
                {
                    managers ??= (await identity.GetUsersWithPermissionAsync(PermissionCodes.RentalCancel)).ToList();
                    foreach (var m in managers) targets.Add(m);
                }

                // 如果没有任何具体目标，回退为广播（TargetUser=null）。
                if (targets.Count == 0) targets.Add(null);

                foreach (var target in targets)
                {
                    if (await HasOpenReminderAsync(db, entityId, type.Value, target, ct)) continue;
                    created.Add(BuildReminder(r, type.Value, target, now));
                }
            }

            if (created.Count > 0)
            {
                db.Reminders.AddRange(created);
                await db.SaveChangesAsync(ct);

                foreach (var reminder in created)
                {
                    foreach (var ch in channels)
                    {
                        try { await ch.DeliverAsync(reminder, ct); }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Notification channel {Channel} failed for reminder {Id}", ch.Name, reminder.Id);
                        }
                    }
                }
            }
        }

        private static Task<bool> HasOpenReminderAsync(ApplicationDbContext db, string entityId, ReminderType type, string? target, CancellationToken ct)
        {
            return db.Reminders.AnyAsync(x =>
                x.RelatedEntityType == "Rental"
                && x.RelatedEntityId == entityId
                && x.Type == type
                && x.TargetUser == target
                && x.DismissedAt == null, ct);
        }

        private static Reminder BuildReminder(Rental rental, ReminderType type, string? target, DateTime now)
        {
            var renterName = rental.Renter?.Name ?? "租客";
            return type switch
            {
                ReminderType.RentalOverdue => new Reminder
                {
                    Type = type,
                    Level = ReminderLevel.Critical,
                    RelatedEntityType = "Rental",
                    RelatedEntityId = rental.Id.ToString(),
                    TargetUser = target,
                    Title = $"租赁 {rental.RentalNumber} 已逾期",
                    Message = $"{renterName} 的订单应于 {rental.ExpectedEndDate:yyyy-MM-dd HH:mm} 归还，已逾期。",
                    DueAt = rental.ExpectedEndDate,
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
                    Message = $"{renterName} 的订单将于 {rental.ExpectedEndDate:yyyy-MM-dd HH:mm} 到期。",
                    DueAt = rental.ExpectedEndDate,
                    CreatedAt = now
                }
            };
        }
    }
}

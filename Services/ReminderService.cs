using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AuditIt.Api.Services
{
    public class ReminderService : IReminderService
    {
        private readonly ApplicationDbContext _context;
        private readonly IEnumerable<INotificationChannel> _channels;

        public ReminderService(ApplicationDbContext context, IEnumerable<INotificationChannel> channels)
        {
            _context = context;
            _channels = channels;
        }

        public async Task<IEnumerable<ReminderDto>> ListAsync(ReminderQueryParameters query, string? currentUser, bool canSeeAll)
        {
            var q = _context.Reminders.AsQueryable();

            if (query.UnreadOnly)
                q = q.Where(r => r.DismissedAt == null);

            if (query.Type.HasValue)
                q = q.Where(r => r.Type == query.Type.Value);

            var target = query.TargetUser?.Trim();
            if (string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
            {
                if (!canSeeAll) return Array.Empty<ReminderDto>();
                // 不再过滤 TargetUser
            }
            else if (!string.IsNullOrEmpty(target))
            {
                if (!canSeeAll && target != currentUser) return Array.Empty<ReminderDto>();
                q = q.Where(r => r.TargetUser == null || r.TargetUser == target);
            }
            else
            {
                var me = currentUser ?? string.Empty;
                q = q.Where(r => r.TargetUser == null || r.TargetUser == me);
            }

            var limit = Math.Clamp(query.Limit, 1, 500);
            return await q.OrderByDescending(r => r.DueAt)
                .ThenByDescending(r => r.CreatedAt)
                .Take(limit)
                .Select(r => ToDto(r))
                .ToListAsync();
        }

        public async Task<bool> DismissAsync(int id, string? currentUser, bool canDismissAny)
        {
            var reminder = await _context.Reminders.FindAsync(id);
            if (reminder == null) return false;

            // 非广播且非当前用户的消息，需要 reminder.dismiss.any
            if (!canDismissAny && reminder.TargetUser != null && reminder.TargetUser != currentUser)
                return false;

            if (reminder.DismissedAt != null) return true;
            reminder.DismissedAt = DateTime.UtcNow;
            reminder.DismissedBy = currentUser;
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<int> DismissAllAsync(string? targetUser, string? currentUser, bool canDismissAny)
        {
            var q = _context.Reminders.Where(r => r.DismissedAt == null);
            var target = targetUser?.Trim();

            if (canDismissAny && string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
            {
                // 所有未读
            }
            else
            {
                var scope = string.IsNullOrEmpty(target) ? currentUser : target;
                if (!canDismissAny && scope != currentUser) return 0;
                q = q.Where(r => r.TargetUser == null || r.TargetUser == scope);
            }

            var pending = await q.ToListAsync();
            var now = DateTime.UtcNow;
            foreach (var r in pending)
            {
                r.DismissedAt = now;
                r.DismissedBy = currentUser;
            }
            await _context.SaveChangesAsync();
            return pending.Count;
        }

        public async Task<IEnumerable<ReminderDto>> CreateManualAsync(CreateReminderDto dto, string? currentUser)
        {
            var due = dto.DueAt ?? DateTime.UtcNow;
            var targets = (dto.TargetUsers ?? new List<string>())
                .Select(u => u?.Trim())
                .Where(u => !string.IsNullOrEmpty(u))
                .Distinct()
                .ToList();

            var created = new List<Reminder>();

            if (targets.Count == 0)
            {
                created.Add(BuildManual(dto, null, due, currentUser));
            }
            else
            {
                foreach (var t in targets)
                    created.Add(BuildManual(dto, t, due, currentUser));
            }

            _context.Reminders.AddRange(created);
            await _context.SaveChangesAsync();

            foreach (var ch in _channels)
            {
                try { await ch.DeliverBatchAsync(created, default); } catch { /* ignore */ }
            }

            return created.Select(ToDto).ToList();
        }

        private static Reminder BuildManual(CreateReminderDto dto, string? target, DateTime due, string? creator)
        {
            return new Reminder
            {
                Type = ReminderType.Manual,
                Level = dto.Level,
                RelatedEntityType = dto.RelatedEntityType ?? "Manual",
                RelatedEntityId = dto.RelatedEntityId ?? (creator ?? string.Empty),
                Title = dto.Title,
                Message = dto.Message,
                TargetUser = target,
                DueAt = due,
                CreatedAt = DateTime.UtcNow
            };
        }

        internal static ReminderDto ToDto(Reminder r) => new()
        {
            Id = r.Id,
            Type = r.Type,
            Level = r.Level,
            RelatedEntityType = r.RelatedEntityType,
            RelatedEntityId = r.RelatedEntityId,
            Title = r.Title,
            Message = r.Message,
            TargetUser = r.TargetUser,
            DueAt = r.DueAt,
            CreatedAt = r.CreatedAt,
            DismissedAt = r.DismissedAt,
            DismissedBy = r.DismissedBy
        };
    }
}

using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AuditIt.Api.Services
{
    public static class ManualLoanReminderService
    {
        public const string RelatedEntityType = "ManualLoan";

        public static async Task<IReadOnlyList<Reminder>> BuildOverdueRemindersAsync(
            ApplicationDbContext db,
            DateTime utcNow,
            CancellationToken ct = default)
        {
            var today = RentalDateRules.Today(utcNow);
            var loanedItems = await db.Items
                .Include(item => item.ItemDefinition)
                .Where(item => item.Status == ItemStatus.LoanedOut
                    && item.ExpectedReturnDate.HasValue)
                .Where(item => item.CurrentDestination == null
                    || !item.CurrentDestination.StartsWith("租赁 "))
                .ToListAsync(ct);

            var overdueItems = loanedItems
                .Where(item => RentalDateRules.ToBusinessDate(item.ExpectedReturnDate!.Value) < today)
                .ToList();
            if (overdueItems.Count == 0)
            {
                return Array.Empty<Reminder>();
            }

            var itemIds = overdueItems.Select(item => item.Id).ToList();
            var itemIdStrings = itemIds.Select(id => id.ToString()).ToList();
            var outboundLogs = await db.AuditLogs
                .Where(log => itemIds.Contains(log.ItemId) && log.Action == AuditAction.Outbound)
                .OrderByDescending(log => log.Timestamp)
                .ToListAsync(ct);
            var latestOutboundByItemId = outboundLogs
                .GroupBy(log => log.ItemId)
                .ToDictionary(group => group.Key, group => group.First());

            var existingKeys = (await db.Reminders
                    .Where(reminder => reminder.RelatedEntityType == RelatedEntityType
                        && reminder.Type == ReminderType.Manual
                        && itemIdStrings.Contains(reminder.RelatedEntityId))
                    .Select(reminder => new
                    {
                        reminder.RelatedEntityId,
                        reminder.TargetUser,
                        reminder.DueAt
                    })
                    .ToListAsync(ct))
                .Select(reminder => BuildDeduplicationKey(
                    reminder.RelatedEntityId,
                    reminder.TargetUser,
                    reminder.DueAt))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var created = new List<Reminder>();
            foreach (var item in overdueItems)
            {
                if (!latestOutboundByItemId.TryGetValue(item.Id, out var outboundLog)
                    || string.IsNullOrWhiteSpace(outboundLog.User))
                {
                    continue;
                }

                var targetUser = outboundLog.User.Trim();
                var expectedReturnDay = RentalDateRules.ToBusinessDate(item.ExpectedReturnDate!.Value);
                var entityId = item.Id.ToString();
                var key = BuildDeduplicationKey(entityId, targetUser, expectedReturnDay);
                if (!existingKeys.Add(key))
                {
                    continue;
                }

                created.Add(new Reminder
                {
                    Type = ReminderType.Manual,
                    Level = ReminderLevel.Warning,
                    RelatedEntityType = RelatedEntityType,
                    RelatedEntityId = entityId,
                    TargetUser = targetUser,
                    Title = $"普通借出 {item.ShortId} 已超过预计回库时间",
                    Message = $"{item.ItemDefinition?.Name ?? "物品"}（{item.ShortId}）原预计 {expectedReturnDay:yyyy-MM-dd} 回库，当前去向：{item.CurrentDestination ?? "未填写"}。占用日历只延续到今天，请确认归还或重新设置预计回库时间。",
                    DueAt = expectedReturnDay,
                    CreatedAt = utcNow
                });
            }

            return created;
        }

        private static string BuildDeduplicationKey(
            string entityId,
            string? targetUser,
            DateTime dueAt) =>
            $"{entityId}|{targetUser?.Trim()}|{RentalDateRules.ToBusinessDate(dueAt):yyyy-MM-dd}";
    }
}

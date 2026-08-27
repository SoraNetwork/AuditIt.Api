using AuditIt.Api.Models;
using AuditIt.Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace AuditIt.Api.Services
{
    // Extension point for delivering reminders through side channels (DingTalk / SMS / email).
    // The in-app channel is already served by writing Reminder rows to the DB — no-op impl registered
    // to demonstrate the shape; register additional implementations later without touching the sweeper.
    public interface INotificationChannel
    {
        string Name { get; }
        Task DeliverAsync(Reminder reminder, CancellationToken ct);

        async Task DeliverBatchAsync(IReadOnlyCollection<Reminder> reminders, CancellationToken ct)
        {
            foreach (var reminder in reminders)
            {
                await DeliverAsync(reminder, ct);
            }
        }
    }

    public class InAppNotificationChannel : INotificationChannel
    {
        public string Name => "in-app";
        public Task DeliverAsync(Reminder reminder, CancellationToken ct) => Task.CompletedTask;
        public Task DeliverBatchAsync(IReadOnlyCollection<Reminder> reminders, CancellationToken ct) => Task.CompletedTask;
    }

    public class DingTalkNotificationChannel : INotificationChannel
    {
        private const int MaxWorkNoticeBytes = 2048;
        private const string DigestHeader = "【工作提醒汇总】";
        private const string DigestSeparator = "\n\n————\n\n";

        private readonly ApplicationDbContext _context;
        private readonly IDingTalkService _dingTalkService;
        private readonly ILogger<DingTalkNotificationChannel> _logger;

        public DingTalkNotificationChannel(
            ApplicationDbContext context,
            IDingTalkService dingTalkService,
            ILogger<DingTalkNotificationChannel> logger)
        {
            _context = context;
            _dingTalkService = dingTalkService;
            _logger = logger;
        }

        public string Name => "dingtalk-work-notice";

        public Task DeliverAsync(Reminder reminder, CancellationToken ct) =>
            DeliverBatchAsync(new[] { reminder }, ct);

        public async Task DeliverBatchAsync(IReadOnlyCollection<Reminder> reminders, CancellationToken ct)
        {
            if (reminders.Count == 0)
            {
                return;
            }

            var targetNames = reminders
                .Select(reminder => reminder.TargetUser?.Trim())
                .Where(target => !string.IsNullOrWhiteSpace(target))
                .Select(target => target!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (targetNames.Count == 0)
            {
                return;
            }

            var users = await _context.Users
                .AsNoTracking()
                .Where(user => user.Status == UserStatus.Active
                    && targetNames.Contains(user.Name)
                    && user.DingTalkUserId != null
                    && user.DingTalkUserId != "")
                .Select(user => new { user.Name, user.DingTalkUserId })
                .ToListAsync(ct);
            var userIdsByName = users.ToDictionary(
                user => user.Name,
                user => user.DingTalkUserId!.Trim(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var missingTarget in targetNames.Where(target => !userIdsByName.ContainsKey(target)))
            {
                _logger.LogDebug("Skip DingTalk notice for {TargetUser}: missing active DingTalkUserId.", missingTarget);
            }

            var deliveries = reminders
                .Where(reminder => !string.IsNullOrWhiteSpace(reminder.TargetUser)
                    && userIdsByName.ContainsKey(reminder.TargetUser.Trim()))
                .GroupBy(
                    reminder => userIdsByName[reminder.TargetUser!.Trim()],
                    StringComparer.OrdinalIgnoreCase)
                .SelectMany(group => BuildDigestChunks(group)
                    .Select(content => new WorkNoticeDelivery(group.Key, content)))
                .ToList();

            foreach (var deliveryGroup in deliveries.GroupBy(delivery => delivery.Content, StringComparer.Ordinal))
            {
                var userIds = deliveryGroup
                    .Select(delivery => delivery.UserId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                await _dingTalkService.SendWorkNoticeAsync(userIds, deliveryGroup.Key, ct);
            }
        }

        private static IReadOnlyList<string> BuildDigestChunks(IEnumerable<Reminder> reminders)
        {
            var notices = reminders
                .OrderBy(reminder => reminder.CreatedAt)
                .ThenBy(reminder => reminder.Id)
                .Select(BuildNoticeContent)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (notices.Count == 0)
            {
                return Array.Empty<string>();
            }

            if (notices.Count == 1)
            {
                return SplitNotice(notices[0], MaxWorkNoticeBytes);
            }

            var chunks = new List<string>();
            var current = new List<string>();
            var maxNoticeBytes = MaxWorkNoticeBytes
                - Encoding.UTF8.GetByteCount(DigestHeader)
                - Encoding.UTF8.GetByteCount(DigestSeparator);
            foreach (var notice in notices.SelectMany(item => SplitNotice(item, maxNoticeBytes)))
            {
                var candidate = BuildDigest(current.Append(notice));
                if (current.Count > 0 && Encoding.UTF8.GetByteCount(candidate) > MaxWorkNoticeBytes)
                {
                    chunks.Add(BuildDigest(current));
                    current.Clear();
                }

                current.Add(notice);
            }

            if (current.Count > 0)
            {
                chunks.Add(BuildDigest(current));
            }

            return chunks;
        }

        private static string BuildDigest(IEnumerable<string> notices) =>
            $"{DigestHeader}\n{string.Join(DigestSeparator, notices)}";

        private static string BuildNoticeContent(Reminder reminder)
        {
            var lines = new List<string>
            {
                $"【{FormatReminderType(reminder.Type)}】{reminder.Title}"
            };

            if (!string.IsNullOrWhiteSpace(reminder.Message))
            {
                lines.Add(reminder.Message.Trim());
            }

            lines.Add($"级别：{FormatReminderLevel(reminder.Level)}");
            lines.Add($"提醒日期：{FormatReminderDueAt(reminder)}");
            return string.Join("\n", lines);
        }

        private static IReadOnlyList<string> SplitNotice(string value, int maxBytes)
        {
            if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            {
                return new[] { value };
            }

            var titleEnd = value.IndexOf('\n');
            var title = titleEnd >= 0 ? value[..titleEnd] : "【工作提醒】";
            var continuationPrefix = $"{title}（续）\n";
            var parts = new List<string>();
            var remaining = value;
            var first = true;
            while (remaining.Length > 0)
            {
                var prefix = first ? string.Empty : continuationPrefix;
                var contentBudget = maxBytes - Encoding.UTF8.GetByteCount(prefix);
                var (content, charactersUsed) = TakeUtf8Prefix(remaining, contentBudget);
                if (charactersUsed == 0)
                {
                    throw new InvalidOperationException("钉钉工作通知标题超过消息长度限制。");
                }

                parts.Add(prefix + content);
                remaining = remaining[charactersUsed..];
                first = false;
            }

            return parts;
        }

        private static (string Text, int CharactersUsed) TakeUtf8Prefix(string value, int maxBytes)
        {
            var builder = new StringBuilder();
            var bytesUsed = 0;
            var charactersUsed = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                if (bytesUsed + rune.Utf8SequenceLength > maxBytes)
                {
                    break;
                }

                builder.Append(rune.ToString());
                bytesUsed += rune.Utf8SequenceLength;
                charactersUsed += rune.Utf16SequenceLength;
            }

            return (builder.ToString(), charactersUsed);
        }

        private static string FormatReminderType(ReminderType type) => type switch
        {
            ReminderType.RentalShipmentSoon => "发货提醒",
            ReminderType.RentalDeliveryUnsigned => "发货待签收",
            ReminderType.RentalReturnUnsigned => "回货待签收",
            ReminderType.RentalDueSoon => "到期提醒",
            ReminderType.RentalOverdue => "逾期提醒",
            ReminderType.Manual => "工作通知",
            _ => type.ToString()
        };

        private static string FormatReminderLevel(ReminderLevel level) => level switch
        {
            ReminderLevel.Critical => "紧急",
            ReminderLevel.Warning => "重要",
            ReminderLevel.Info => "普通",
            _ => level.ToString()
        };

        private static string FormatReminderDueAt(Reminder reminder) =>
            reminder.Type == ReminderType.Manual
                ? RentalDateRules.FormatDateTime(reminder.DueAt)
                : RentalDateRules.Format(reminder.DueAt);

        private sealed record WorkNoticeDelivery(string UserId, string Content);
    }
}

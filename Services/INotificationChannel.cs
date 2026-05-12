using AuditIt.Api.Models;
using AuditIt.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AuditIt.Api.Services
{
    // Extension point for delivering reminders through side channels (DingTalk / SMS / email).
    // The in-app channel is already served by writing Reminder rows to the DB — no-op impl registered
    // to demonstrate the shape; register additional implementations later without touching the sweeper.
    public interface INotificationChannel
    {
        string Name { get; }
        Task DeliverAsync(Reminder reminder, CancellationToken ct);
    }

    public class InAppNotificationChannel : INotificationChannel
    {
        public string Name => "in-app";
        public Task DeliverAsync(Reminder reminder, CancellationToken ct) => Task.CompletedTask;
    }

    public class DingTalkNotificationChannel : INotificationChannel
    {
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

        public async Task DeliverAsync(Reminder reminder, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(reminder.TargetUser))
            {
                return;
            }

            var targetName = reminder.TargetUser.Trim();
            var dingTalkUserId = await _context.Users
                .Where(u => u.Status == UserStatus.Active && u.Name == targetName)
                .Select(u => u.DingTalkUserId)
                .FirstOrDefaultAsync(ct);

            if (string.IsNullOrWhiteSpace(dingTalkUserId))
            {
                _logger.LogDebug("Skip DingTalk notice for {TargetUser}: missing DingTalkUserId.", targetName);
                return;
            }

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

            var content = string.Join("\n", lines);

            await _dingTalkService.SendWorkNoticeAsync(new[] { dingTalkUserId }, content, ct);
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
    }
}

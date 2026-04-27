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

            var content = string.IsNullOrWhiteSpace(reminder.Message)
                ? reminder.Title
                : $"{reminder.Title}\n{reminder.Message}";

            await _dingTalkService.SendWorkNoticeAsync(new[] { dingTalkUserId }, content, ct);
        }
    }
}

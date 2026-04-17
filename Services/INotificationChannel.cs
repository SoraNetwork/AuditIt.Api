using AuditIt.Api.Models;

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
}

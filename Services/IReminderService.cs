using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    public interface IReminderService
    {
        Task<IEnumerable<ReminderDto>> ListAsync(ReminderQueryParameters query, string? currentUser, bool canSeeAll);
        Task<bool> DismissAsync(int id, string? currentUser, bool canDismissAny);
        Task<int> DismissAllAsync(string? targetUser, string? currentUser, bool canDismissAny);
        Task<IEnumerable<ReminderDto>> CreateManualAsync(CreateReminderDto dto, string? currentUser);
    }
}

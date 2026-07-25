using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    public interface IRentalService
    {
        Task<(IEnumerable<RentalDto> items, int total)> ListAsync(RentalQueryParameters query);
        Task<IReadOnlyList<RentalCalendarEventDto>> GetCalendarAsync(
            RentalCalendarQueryParameters query,
            string? currentUser,
            bool includeReminders,
            bool canSeeAllReminders);
        Task<RentalDto?> GetByIdAsync(Guid id);
        Task<CreateRentalResult> CreateAsync(CreateRentalDto dto, string? currentUser);
        Task<RenewRentalResult> RenewAsync(Guid id, RenewRentalDto dto, string? currentUser);
        Task<UpdateRentalResult> UpdateAsync(Guid id, UpdateRentalDto dto, string? currentUser);
        Task<RentalShipmentResult> AddShipmentAsync(Guid rentalId, CreateShipmentDto dto, string? currentUser);
        Task<(RentalDto? rental, string? error)> MarkDeliveredAsync(Guid rentalId, int shipmentId, DeliverShipmentDto dto, string? currentUser);
        Task<(RentalDto? rental, string? error)> UpdateShipmentAsync(Guid rentalId, int shipmentId, UpdateShipmentDto dto, string? currentUser);
        Task<(RentalDto? rental, string? error)> DeleteShipmentAsync(Guid rentalId, int shipmentId, string? currentUser);
        Task<(RentalDto? rental, string? error)> ReturnAsync(Guid rentalId, ReturnRentalDto dto, string? currentUser);
        Task<(RentalDto? rental, string? error)> CancelAsync(Guid rentalId, CancelRentalDto dto, string? currentUser);
        Task<RentalItemsUpdateResult> UpdateRentalItemsAsync(Guid rentalId, UpdateRentalItemsDto dto, string? currentUser);
        Task<(RentalDto? rental, string? error)> BulkUpdateItemsAsync(Guid rentalId, BulkUpdateRentalItemsDto dto, string? currentUser);
        Task<(SfRouteSyncResultDto? result, string? error)> SyncSfRoutesAsync(
            Guid rentalId,
            bool forceRefresh,
            string? currentUser,
            CancellationToken ct = default);
        Task<SfPendingRouteRefreshResultDto> SyncPendingSfRoutesAsync(string? currentUser, CancellationToken ct = default);
    }
}

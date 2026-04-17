using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    public interface IRentalService
    {
        Task<IEnumerable<RentalDto>> ListAsync(RentalQueryParameters query);
        Task<RentalDto?> GetByIdAsync(Guid id);
        Task<(RentalDto? rental, string? error)> CreateAsync(CreateRentalDto dto, string? currentUser);
        Task<(RentalDto? rental, string? error)> UpdateAsync(Guid id, UpdateRentalDto dto, string? currentUser);
        Task<(RentalShipmentDto? shipment, string? error)> AddShipmentAsync(Guid rentalId, CreateShipmentDto dto, string? currentUser);
        Task<(RentalShipmentDto? shipment, string? error)> MarkDeliveredAsync(Guid rentalId, int shipmentId, DeliverShipmentDto dto, string? currentUser);
        Task<(RentalDto? rental, string? error)> ReturnAsync(Guid rentalId, ReturnRentalDto dto, string? currentUser);
        Task<(RentalDto? rental, string? error)> CancelAsync(Guid rentalId, CancelRentalDto dto, string? currentUser);
    }
}

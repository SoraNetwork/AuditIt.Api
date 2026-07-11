using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    public interface IRenterService
    {
        Task<IEnumerable<RenterDto>> SearchAsync(string? keyword, int limit);
        Task<RenterDto?> GetByIdAsync(Guid id);
        Task<RenterDto> CreateAsync(CreateRenterDto dto, string? currentUser);
        Task<RenterDto?> UpdateAsync(Guid id, UpdateRenterDto dto, string? currentUser);
        Task<bool> DeleteAsync(Guid id);

        // Resolve a renter from inline form: prefer RenterId, else upsert by Phone, else create anonymous.
        Task<Renter> ResolveOrUpsertAsync(RenterInlineDto inline, string? currentUser);
    }
}

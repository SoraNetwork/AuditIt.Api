using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    public interface ISettlementService
    {
        Task<SettlementSettingDto> GetSettingsAsync(CancellationToken ct = default);
        Task<(SettlementSettingDto? settings, string? error)> UpdateSettingsAsync(
            UpdateSettlementSettingDto dto,
            string? currentUser,
            CancellationToken ct = default);
        Task<SettlementPreviewDto?> GetPreviewAsync(Guid rentalId, CancellationToken ct = default);
        Task<(SettlementPreviewDto? preview, string? error)> SendForRentalAsync(
            Guid rentalId,
            string? currentUser,
            bool force = false,
            CancellationToken ct = default);
        Task TrySendForRentalAsync(Guid rentalId, string? currentUser, CancellationToken ct = default);
    }
}

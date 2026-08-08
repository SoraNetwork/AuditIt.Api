using AuditIt.Api.Models;

namespace AuditIt.Api.Services;

public interface IShipmentReminderService
{
    Task<ShipmentReminderSettingsDto> GetSettingsAsync(CancellationToken ct = default);
    Task<ShipmentReminderSettingsDto> UpdateSettingsAsync(UpdateShipmentReminderSettingsDto dto, string? currentUser, CancellationToken ct = default);
    Task<IReadOnlyList<AliyunSmsTemplateDto>> ListSmsTemplatesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ShipmentReminderTestResultDto>> SendTestAsync(TestShipmentReminderDto dto, CancellationToken ct = default);
    Task DispatchScheduledAsync(DateTime utcNow, CancellationToken ct = default);
}

using AuditIt.Api.Models;

namespace AuditIt.Api.Services;

public interface IAliyunShipmentReminderSender
{
    Task<string> SendSmsAsync(string mobile, string signName, string templateCode, IReadOnlyDictionary<string, string> templateParameters, CancellationToken ct);
    Task<string> SendVoiceAsync(string mobile, string? calledShowNumber, string ttsCode, IReadOnlyDictionary<string, string> templateParameters, CancellationToken ct);
    Task<IReadOnlyList<AliyunSmsTemplateDto>> ListSmsTemplatesAsync(CancellationToken ct);
}

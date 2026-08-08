using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models;

public class ShipmentReminderSettingsDto
{
    public bool Enabled { get; set; }
    public bool SmsEnabled { get; set; }
    public bool VoiceEnabled { get; set; }
    public int SendHour { get; set; }
    public int SendMinute { get; set; }
    public int VoiceSendHour { get; set; }
    public int VoiceSendMinute { get; set; }
    public List<ShipmentReminderTemplateVariableDto> TemplateVariables { get; set; } = new();
    public string? SmsSignName { get; set; }
    public string? SmsTemplateCode { get; set; }
    public string? VoiceTtsCode { get; set; }
    public string? VoiceCalledShowNumber { get; set; }
    public List<Guid> AdministratorUserIds { get; set; } = new();
    public string TemplateBody { get; set; } = ShipmentReminderTemplate.Body;
    public bool AliyunCredentialsConfigured { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public class UpdateShipmentReminderSettingsDto
{
    public bool Enabled { get; set; }
    public bool SmsEnabled { get; set; }
    public bool VoiceEnabled { get; set; }

    [Range(0, 23)]
    public int SendHour { get; set; } = 12;

    [Range(0, 59)]
    public int SendMinute { get; set; }

    [Range(0, 23)]
    public int VoiceSendHour { get; set; } = 12;

    [Range(0, 59)]
    public int VoiceSendMinute { get; set; } = 30;

    [MinLength(1)]
    public List<ShipmentReminderTemplateVariableDto> TemplateVariables { get; set; } = new();

    [StringLength(100)]
    public string? SmsSignName { get; set; }

    [StringLength(100)]
    public string? SmsTemplateCode { get; set; }

    [StringLength(100)]
    public string? VoiceTtsCode { get; set; }

    [StringLength(30)]
    public string? VoiceCalledShowNumber { get; set; }

    public List<Guid> AdministratorUserIds { get; set; } = new();
}

public class TestShipmentReminderDto
{
    [Required]
    [MinLength(1)]
    public List<Guid> UserIds { get; set; } = new();

    public bool SendSms { get; set; } = true;
    public bool SendVoice { get; set; }
}

public class ShipmentReminderTestResultDto
{
    public string UserName { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public ShipmentReminderChannel Channel { get; set; }
    public bool Success { get; set; }
    public string? ProviderRequestId { get; set; }
    public string? Error { get; set; }
}

/// <summary>Safe employee projection used only for reminder recipient selection.</summary>
public class ShipmentReminderRecipientDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Mobile { get; set; }
}

public class AliyunSmsTemplateDto
{
    public string TemplateCode { get; set; } = string.Empty;
    public string? TemplateName { get; set; }
    public string? TemplateContent { get; set; }
    public string? SignatureName { get; set; }
    public string? AuditStatus { get; set; }
    public bool IsApproved { get; set; }
    public bool MatchesShipmentReminder { get; set; }
    public List<string> VariableNames { get; set; } = new();
}

public class ShipmentReminderTemplateVariableDto
{
    [Required]
    [StringLength(50)]
    public string Name { get; set; } = string.Empty;

    public ShipmentReminderVariableSource Source { get; set; }

    [StringLength(500)]
    public string? StaticValue { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models;

/// <summary>
/// Settings for the 12:00 shipment reminder. Secrets deliberately live in server
/// configuration, not in this table, so that they can never be returned to a browser.
/// </summary>
public class ShipmentReminderSettings
{
    public const int SingletonId = 1;

    [Key]
    public int Id { get; set; } = SingletonId;

    public bool Enabled { get; set; }
    public bool SmsEnabled { get; set; } = true;
    public bool VoiceEnabled { get; set; }

    [Range(0, 23)]
    public int SendHour { get; set; } = 12;

    [Range(0, 59)]
    public int SendMinute { get; set; }

    /// <summary>JSON array of variable name-to-value-source mappings used by SMS and TTS.</summary>
    [Required]
    [StringLength(4000)]
    public string TemplateVariablesJson { get; set; } = ShipmentReminderTemplate.DefaultVariablesJson;

    [StringLength(100)]
    public string? SmsSignName { get; set; }

    [StringLength(100)]
    public string? SmsTemplateCode { get; set; }

    [StringLength(100)]
    public string? VoiceTtsCode { get; set; }

    [StringLength(30)]
    public string? VoiceCalledShowNumber { get; set; }

    /// <summary>JSON array of User IDs selected in the configuration screen.</summary>
    [Required]
    [StringLength(2000)]
    public string AdministratorUserIds { get; set; } = "[]";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [StringLength(100)]
    public string? UpdatedBy { get; set; }
}

public enum ShipmentReminderChannel
{
    Sms,
    Voice
}

public enum ShipmentReminderVariableSource
{
    OrderIdWithRenterName,
    RentalNumber,
    RenterName,
    RelativeExpectedShipDate,
    ExpectedShipDate,
    Creator,
    Responsible,
    StaticText
}

public enum ShipmentReminderDispatchStatus
{
    Pending,
    Succeeded,
    Failed
}

/// <summary>External-send ledger. Its unique index makes recurring sweeps idempotent.</summary>
public class ShipmentReminderDispatch
{
    [Key]
    public int Id { get; set; }

    public Guid RentalId { get; set; }

    [Required]
    [StringLength(30)]
    public string RecipientMobile { get; set; } = string.Empty;

    [StringLength(100)]
    public string? RecipientName { get; set; }

    public ShipmentReminderChannel Channel { get; set; }

    /// <summary>China business date for which the reminder was sent.</summary>
    public DateTime BusinessDate { get; set; }

    [StringLength(100)]
    public string? ProviderRequestId { get; set; }

    public ShipmentReminderDispatchStatus Status { get; set; } = ShipmentReminderDispatchStatus.Pending;

    [StringLength(1000)]
    public string? ErrorMessage { get; set; }

    public DateTime AttemptedAt { get; set; } = DateTime.UtcNow;

    public DateTime? SentAt { get; set; }
}

public static class ShipmentReminderTemplate
{
    public const string DefaultOrderIdVariableName = "order_id";
    public const string DefaultTimeVariableName = "time";
    public const string Body = "您好，您的租赁订单 ${order_id} 应于 ${time} 发货，请您尽早发货，避免超时。";
    public const string DefaultVariablesJson = "[{\"name\":\"order_id\",\"source\":\"OrderIdWithRenterName\"},{\"name\":\"time\",\"source\":\"RelativeExpectedShipDate\"}]";

    public static string BuildPreview(string orderId, string time) =>
        Body.Replace("${order_id}", orderId, StringComparison.Ordinal)
            .Replace("${time}", time, StringComparison.Ordinal);
}

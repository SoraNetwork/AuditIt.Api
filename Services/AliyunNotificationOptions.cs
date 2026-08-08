namespace AuditIt.Api.Services;

/// <summary>
/// Credentials are supplied by appsettings, user-secrets, environment variables, or
/// a secret store. Do not persist these values in the database or expose them via API.
/// </summary>
public class AliyunNotificationOptions
{
    public string? AccessKeyId { get; set; }
    public string? AccessKeySecret { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccessKeyId)
        && !string.IsNullOrWhiteSpace(AccessKeySecret);
}


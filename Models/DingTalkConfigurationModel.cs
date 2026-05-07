namespace AuditIt.Api.Models
{
    public class DingTalkConfiguration
    {
        public string AppKey { get; set; } = string.Empty;
        public string AppSecret { get; set; } = string.Empty;
        public long? AgentId { get; set; }
        public bool EnableWorkNotice { get; set; }
        public string? RobotWebhookUrl { get; set; }
        public string? RobotSecret { get; set; }
    }
}

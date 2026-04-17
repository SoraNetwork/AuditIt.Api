using System;

namespace AuditIt.Api.Models
{
    public class ReminderDto
    {
        public int Id { get; set; }
        public ReminderType Type { get; set; }
        public ReminderLevel Level { get; set; }
        public string RelatedEntityType { get; set; } = string.Empty;
        public string RelatedEntityId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Message { get; set; }
        public string? TargetUser { get; set; }
        public DateTime DueAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? DismissedAt { get; set; }
        public string? DismissedBy { get; set; }
    }

    public class ReminderQueryParameters
    {
        public bool UnreadOnly { get; set; } = true;
        // 不传则默认取"当前登录用户 + 广播"；传"all"则不按用户过滤（需查看权限）。
        public string? TargetUser { get; set; }
        public ReminderType? Type { get; set; }
        public int Limit { get; set; } = 100;
    }

    public class CreateReminderDto
    {
        [System.ComponentModel.DataAnnotations.Required]
        [System.ComponentModel.DataAnnotations.StringLength(200)]
        public string Title { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.StringLength(1000)]
        public string? Message { get; set; }

        public ReminderLevel Level { get; set; } = ReminderLevel.Info;

        // 多人推送。空列表=广播给所有人（仅 reminder.create + 角色足够的用户可用）。
        public List<string>? TargetUsers { get; set; }

        public DateTime? DueAt { get; set; }

        [System.ComponentModel.DataAnnotations.StringLength(40)]
        public string? RelatedEntityType { get; set; }

        [System.ComponentModel.DataAnnotations.StringLength(40)]
        public string? RelatedEntityId { get; set; }
    }

    public class ReminderOptions
    {
        public int SweepIntervalMinutes { get; set; } = 10;
        public int DueSoonLeadHours { get; set; } = 48;
    }
}

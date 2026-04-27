using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public enum UserStatus
    {
        Active,
        Left
    }

    public class User
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        // 历史字段：存最近一次登录携带的钉钉 ID（UserId 或 UnionId），仅用于审计，不作登录匹配。
        [StringLength(100)]
        public string? DingTalkId { get; set; }

        [StringLength(100)]
        public string? LastDingTalkId { get; set; }

        [StringLength(100)]
        public string? DingTalkUserId { get; set; }

        [StringLength(100)]
        public string? DingTalkUnionId { get; set; }

        [StringLength(30)]
        public string? Mobile { get; set; }

        [StringLength(100)]
        public string? JobNumber { get; set; }

        [StringLength(100)]
        public string? JobTitle { get; set; }

        public DateTime? LastDingTalkSyncAt { get; set; }

        public DateTime? LastLoginAt { get; set; }

        public UserStatus Status { get; set; } = UserStatus.Active;

        [StringLength(200)]
        public string? Notes { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public virtual ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    }
}

using System;
using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public enum ReminderType
    {
        RentalDueSoon,
        RentalOverdue,
        Manual
    }

    public enum ReminderLevel
    {
        Info,
        Warning,
        Critical
    }

    public class Reminder
    {
        [Key]
        public int Id { get; set; }

        public ReminderType Type { get; set; }

        public ReminderLevel Level { get; set; } = ReminderLevel.Info;

        [Required]
        [StringLength(40)]
        public string RelatedEntityType { get; set; } = string.Empty;

        [Required]
        [StringLength(40)]
        public string RelatedEntityId { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        public string Title { get; set; } = string.Empty;

        [StringLength(1000)]
        public string? Message { get; set; }

        [StringLength(100)]
        public string? TargetUser { get; set; }

        public DateTime DueAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? DismissedAt { get; set; }

        [StringLength(100)]
        public string? DismissedBy { get; set; }
    }
}

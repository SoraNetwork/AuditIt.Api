using System;
using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class Renter
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        [StringLength(30)]
        public string? Phone { get; set; }

        [StringLength(30)]
        public string? IdCardNo { get; set; }

        [StringLength(100)]
        public string? XianyuId { get; set; }

        [StringLength(100)]
        public string? TaobaoId { get; set; }

        [StringLength(100)]
        public string? XiaohongshuId { get; set; }

        [StringLength(500)]
        public string? DefaultAddress { get; set; }

        [StringLength(500)]
        public string? Notes { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [StringLength(100)]
        public string? CreatedBy { get; set; }

        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }
}

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AuditIt.Api.Models
{
    public enum AuditAction
    {
        Inbound,
        Outbound,
        Check,
        Return,
        Dispose,
        Transfer
    }

    public class AuditLog
    {
        [Key]
        public int Id { get; set; }

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public AuditAction Action { get; set; }

        public Guid ItemId { get; set; }
        [ForeignKey("ItemId")]
        public virtual Item? Item { get; set; }

        [Required]
        [StringLength(50)]
        public string ItemShortId { get; set; } = string.Empty;

        [Required]
        [StringLength(100)]
        public string ItemName { get; set; } = string.Empty;

        public int WarehouseId { get; set; }
        [ForeignKey("WarehouseId")]
        public virtual Warehouse? Warehouse { get; set; }

        [Required]
        [StringLength(100)]
        public string WarehouseName { get; set; } = string.Empty;

        [Required]
        [StringLength(100)]
        public string User { get; set; } = string.Empty;

        [StringLength(200)]
        public string? Destination { get; set; }
    }
}

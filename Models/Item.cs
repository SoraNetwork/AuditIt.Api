using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AuditIt.Api.Models
{
    public enum ItemStatus
    {
        InStock,
        LoanedOut,
        Disposed
    }

    public class Item
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        [StringLength(50)]
        public string ShortId { get; set; } = string.Empty; // This will now store the External Barcode

        public int ItemDefinitionId { get; set; }
        [ForeignKey("ItemDefinitionId")]
        public virtual ItemDefinition? ItemDefinition { get; set; }

        public int WarehouseId { get; set; }
        [ForeignKey("WarehouseId")]
        public virtual Warehouse? Warehouse { get; set; }

        public ItemStatus Status { get; set; }

        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        public DateTime EntryDate { get; set; } = DateTime.UtcNow;

        [StringLength(500)]
        public string? Remarks { get; set; }

        [StringLength(2048)]
        public string? PhotoUrl { get; set; }
    }
}

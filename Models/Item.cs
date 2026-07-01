using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AuditIt.Api.Models
{
    public enum ItemStatus
    {
        InStock,
        LoanedOut,
        Disposed,
        SuspectedMissing
    }

    public class Item
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        [StringLength(50)]
        public string ShortId { get; set; } = string.Empty; // This will now store the External Barcode

        // 厂家序列号 / 特殊 ID（SN）。全库唯一，作用等价于 ShortId。
        [StringLength(100)]
        public string? SerialNumber { get; set; }

        public int ItemDefinitionId { get; set; }
        [ForeignKey("ItemDefinitionId")]
        public virtual ItemDefinition? ItemDefinition { get; set; }

        public int WarehouseId { get; set; }
        [ForeignKey("WarehouseId")]
        public virtual Warehouse? Warehouse { get; set; }

        [StringLength(500)]
        public string? OwnerUserNamesSnapshot { get; set; }

        public ItemStatus Status { get; set; }

        public bool IsDeleted { get; set; }

        public DateTime? DeletedAt { get; set; }

        [StringLength(100)]
        public string? DeletedBy { get; set; }

        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        public DateTime EntryDate { get; set; } = DateTime.UtcNow;

        [StringLength(500)]
        public string? Remarks { get; set; }

        [StringLength(2048)]
        public string? PhotoUrl { get; set; }

        [StringLength(200)]
        public string? CurrentDestination { get; set; }

        public decimal? ItemValue { get; set; }

        public virtual ICollection<ItemListing> Listings { get; set; } = new List<ItemListing>();
    }
}

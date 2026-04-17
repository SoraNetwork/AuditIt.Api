using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AuditIt.Api.Models
{
    public enum ReturnCondition
    {
        Good,
        MinorDamage,
        MajorDamage,
        Lost
    }

    public class RentalItem
    {
        [Key]
        public int Id { get; set; }

        public Guid RentalId { get; set; }
        [ForeignKey("RentalId")]
        public virtual Rental? Rental { get; set; }

        public Guid ItemId { get; set; }
        [ForeignKey("ItemId")]
        public virtual Item? Item { get; set; }

        [StringLength(50)]
        public string ItemShortIdSnapshot { get; set; } = string.Empty;

        [StringLength(200)]
        public string ItemNameSnapshot { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,2)")]
        public decimal? PerItemPrice { get; set; }

        public DateTime? ReturnedAt { get; set; }

        public ReturnCondition? ReturnCondition { get; set; }

        [StringLength(500)]
        public string? ReturnNotes { get; set; }
    }
}

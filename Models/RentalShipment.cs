using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AuditIt.Api.Models
{
    public enum ShipmentDirection
    {
        Outbound,
        Inbound
    }

    public class RentalShipment
    {
        [Key]
        public int Id { get; set; }

        public Guid RentalId { get; set; }
        [ForeignKey("RentalId")]
        public virtual Rental? Rental { get; set; }

        public ShipmentDirection Direction { get; set; }

        public int OriginWarehouseId { get; set; }
        [ForeignKey("OriginWarehouseId")]
        public virtual Warehouse? OriginWarehouse { get; set; }

        [Required]
        [StringLength(50)]
        public string Carrier { get; set; } = string.Empty;

        [StringLength(100)]
        public string? TrackingNumber { get; set; }

        public DateTime ShippedAt { get; set; } = DateTime.UtcNow;

        public DateTime? DeliveredAt { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? ShippingFee { get; set; }

        [StringLength(500)]
        public string? Notes { get; set; }

        [StringLength(100)]
        public string? CreatedBy { get; set; }

        public virtual ICollection<RentalShipmentItem> RentalItems { get; set; } = new List<RentalShipmentItem>();
    }
}

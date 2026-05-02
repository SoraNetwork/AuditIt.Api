using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AuditIt.Api.Models
{
    public enum RentalStatus
    {
        Pending,
        Active,
        Overdue,
        Returned,
        Cancelled,
        Renewed
    }

    public class Rental
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        [StringLength(30)]
        public string RentalNumber { get; set; } = string.Empty;

        public Guid RenterId { get; set; }
        [ForeignKey("RenterId")]
        public virtual Renter? Renter { get; set; }

        public RentalStatus Status { get; set; } = RentalStatus.Pending;

        public DateTime StartDate { get; set; } = DateTime.UtcNow;

        public DateTime ExpectedShipDate { get; set; } = DateTime.UtcNow.Date.AddDays(-1);

        public DateTime ExpectedEndDate { get; set; }

        public DateTime? ActualEndDate { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal TotalPrice { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? Deposit { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal OtherFee { get; set; }

        [StringLength(500)]
        public string? ShippingAddress { get; set; }

        [StringLength(100)]
        public string? PlatformOrderNo { get; set; }

        public Guid? RenewedFromRentalId { get; set; }

        [StringLength(30)]
        public string? RenewedFromRentalNumber { get; set; }

        public Guid? RenewedToRentalId { get; set; }

        [StringLength(30)]
        public string? RenewedToRentalNumber { get; set; }

        public int? RenewalSequence { get; set; }

        [StringLength(500)]
        public string? Notes { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [StringLength(100)]
        public string? CreatedBy { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [StringLength(100)]
        public string? UpdatedBy { get; set; }

        // 订单负责人（员工姓名），提醒会定向推送给此人；空则仅推给 CreatedBy。
        [StringLength(100)]
        public string? AssignedTo { get; set; }

        public virtual ICollection<RentalItem> Items { get; set; } = new List<RentalItem>();

        public virtual ICollection<RentalShipment> Shipments { get; set; } = new List<RentalShipment>();
    }
}

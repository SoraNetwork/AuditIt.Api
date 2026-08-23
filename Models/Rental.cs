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
        Renewed,
        PartiallyShipped
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

        public DateTime ExpectedShipDate { get; set; } = DateTime.UtcNow.Date.AddDays(-3);

        public DateTime ExpectedEndDate { get; set; }

        public DateTime? ExpectedReturnDate { get; set; }

        public DateTime? ActualEndDate { get; set; }

        public bool HasRenewalIntent { get; set; }

        public DateTime? RenewalIntentEndDate { get; set; }

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

        [StringLength(100)]
        public string? PaymentAccount { get; set; }

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

        public DateTime? SettlementNotifiedAt { get; set; }

        [StringLength(30)]
        public string? SettlementNotifiedStatus { get; set; }

        // 订单负责人（员工姓名），提醒会定向推送给此人；空则仅推给 CreatedBy。
        [StringLength(100)]
        public string? AssignedTo { get; set; }

        // 手动发货人姓名，与结算单相对应
        [StringLength(100)]
        public string? SenderName { get; set; }

        public virtual ICollection<RentalItem> Items { get; set; } = new List<RentalItem>();

        public virtual ICollection<RentalShipment> Shipments { get; set; } = new List<RentalShipment>();
    }
}

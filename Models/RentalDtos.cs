using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class RentalDto
    {
        public Guid Id { get; set; }
        public string RentalNumber { get; set; } = string.Empty;
        public RentalStatus Status { get; set; }
        public Guid RenterId { get; set; }
        public RenterDto? Renter { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime ExpectedEndDate { get; set; }
        public DateTime? ActualEndDate { get; set; }
        public decimal TotalPrice { get; set; }
        public decimal? Deposit { get; set; }
        public string? ShippingAddress { get; set; }
        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public string? AssignedTo { get; set; }
        public List<RentalItemDto> Items { get; set; } = new();
        public List<RentalShipmentDto> Shipments { get; set; } = new();
    }

    public class RentalItemDto
    {
        public int Id { get; set; }
        public Guid ItemId { get; set; }
        public string ItemShortIdSnapshot { get; set; } = string.Empty;
        public string ItemNameSnapshot { get; set; } = string.Empty;
        public decimal? PerItemPrice { get; set; }
        public DateTime? ReturnedAt { get; set; }
        public ReturnCondition? ReturnCondition { get; set; }
        public string? ReturnNotes { get; set; }
    }

    public class RentalShipmentDto
    {
        public int Id { get; set; }
        public ShipmentDirection Direction { get; set; }
        public int OriginWarehouseId { get; set; }
        public string? OriginWarehouseName { get; set; }
        public string Carrier { get; set; } = string.Empty;
        public string? TrackingNumber { get; set; }
        public DateTime ShippedAt { get; set; }
        public DateTime? DeliveredAt { get; set; }
        public decimal? ShippingFee { get; set; }
        public string? Notes { get; set; }
        public string? CreatedBy { get; set; }
    }

    public class CreateRentalDto
    {
        // Either RenterId (existing) OR inline info (upserted by Phone). Both allowed.
        [Required]
        public RenterInlineDto Renter { get; set; } = new();

        [Required]
        [MinLength(1)]
        public List<Guid> ItemIds { get; set; } = new();

        public DateTime? StartDate { get; set; }

        [Required]
        public DateTime ExpectedEndDate { get; set; }

        [Range(0, double.MaxValue)]
        public decimal TotalPrice { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? Deposit { get; set; }

        [StringLength(500)]
        public string? ShippingAddress { get; set; }

        [StringLength(500)]
        public string? Notes { get; set; }

        [StringLength(100)]
        public string? AssignedTo { get; set; }
    }

    public class UpdateRentalDto
    {
        public DateTime? ExpectedEndDate { get; set; }
        [Range(0, double.MaxValue)]
        public decimal? TotalPrice { get; set; }
        [Range(0, double.MaxValue)]
        public decimal? Deposit { get; set; }
        [StringLength(500)]
        public string? ShippingAddress { get; set; }
        [StringLength(500)]
        public string? Notes { get; set; }
        [StringLength(100)]
        public string? AssignedTo { get; set; }
    }

    public class CreateShipmentDto
    {
        public ShipmentDirection Direction { get; set; } = ShipmentDirection.Outbound;

        [Required]
        public int OriginWarehouseId { get; set; }

        [Required]
        [StringLength(50)]
        public string Carrier { get; set; } = string.Empty;

        [StringLength(100)]
        public string? TrackingNumber { get; set; }

        public DateTime? ShippedAt { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? ShippingFee { get; set; }

        [StringLength(500)]
        public string? Notes { get; set; }
    }

    public class DeliverShipmentDto
    {
        public DateTime? DeliveredAt { get; set; }
    }

    public class ReturnRentalDto
    {
        // If null/empty, return all outstanding rental items.
        public List<int>? RentalItemIds { get; set; }
        public ReturnCondition? Condition { get; set; }
        [StringLength(500)]
        public string? Notes { get; set; }
    }

    public class CancelRentalDto
    {
        [StringLength(500)]
        public string? Reason { get; set; }
    }

    public class RentalQueryParameters
    {
        public RentalStatus? Status { get; set; }
        public Guid? RenterId { get; set; }
        public string? RentalNumber { get; set; }
        public DateTime? StartDateFrom { get; set; }
        public DateTime? StartDateTo { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
    }
}

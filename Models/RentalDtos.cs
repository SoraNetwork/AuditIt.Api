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
        public DateTime ExpectedShipDate { get; set; }
        public DateTime ExpectedEndDate { get; set; }
        public DateTime? ExpectedReturnDate { get; set; }
        public DateTime? ActualEndDate { get; set; }
        public bool HasRenewalIntent { get; set; }
        public DateTime? RenewalIntentEndDate { get; set; }
        public decimal TotalPrice { get; set; }
        public decimal? Deposit { get; set; }
        public decimal OtherFee { get; set; }
        public decimal TotalShippingFee { get; set; }
        public decimal AccountedAmount { get; set; }
        public string? ShippingAddress { get; set; }
        public string? PlatformOrderNo { get; set; }
        public Guid? RenewedFromRentalId { get; set; }
        public string? RenewedFromRentalNumber { get; set; }
        public Guid? RenewedToRentalId { get; set; }
        public string? RenewedToRentalNumber { get; set; }
        public int? RenewalSequence { get; set; }
        public bool IsRenewal { get; set; }
        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? SettlementNotifiedAt { get; set; }
        public string? SettlementNotifiedStatus { get; set; }
        public string? AssignedTo { get; set; }
        public string? SenderName { get; set; }
        public List<RentalItemDto> Items { get; set; } = new();
        public List<RentalShipmentDto> Shipments { get; set; } = new();
    }

    public class RentalItemDto
    {
        public int Id { get; set; }
        public Guid? ItemId { get; set; }
        public int? ItemDefinitionId { get; set; }
        public int? CategoryId { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public string ItemShortIdSnapshot { get; set; } = string.Empty;
        public string ItemNameSnapshot { get; set; } = string.Empty;
        public decimal? PerItemPrice { get; set; }
        public DateTime? ReturnedAt { get; set; }
        public ReturnCondition? ReturnCondition { get; set; }
        public string? ReturnNotes { get; set; }
        public string? ListingRemarks { get; set; }
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
        public List<RentalShipmentItemDto> Items { get; set; } = new();
    }

    public class RentalShipmentItemDto
    {
        public int RentalItemId { get; set; }
        public Guid? ItemId { get; set; }
        public string ItemShortIdSnapshot { get; set; } = string.Empty;
        public string ItemNameSnapshot { get; set; } = string.Empty;
    }

    public class CreateRentalDto
    {
        // Either RenterId (existing) OR inline info (upserted by Phone). Both allowed.
        [Required]
        public RenterInlineDto Renter { get; set; } = new();

        public List<string> ItemIds { get; set; } = new();

        public List<int> ItemDefinitionIds { get; set; } = new();

        // Optional for backward compatibility. When provided, the server derives
        // TotalPrice from these per-rental-item prices.
        public List<CreateRentalItemPriceDto> ItemPrices { get; set; } = new();

        public DateTime? StartDate { get; set; }

        public DateTime? ExpectedShipDate { get; set; }

        [Required]
        public DateTime ExpectedEndDate { get; set; }

        public DateTime? ExpectedReturnDate { get; set; }

        public bool HasRenewalIntent { get; set; }

        public DateTime? RenewalIntentEndDate { get; set; }

        [Range(0, double.MaxValue)]
        public decimal TotalPrice { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? Deposit { get; set; }

        [Range(0, double.MaxValue)]
        public decimal OtherFee { get; set; }

        [StringLength(500)]
        public string? ShippingAddress { get; set; }

        [StringLength(100)]
        public string? PlatformOrderNo { get; set; }

        [StringLength(500)]
        public string? Notes { get; set; }

        [StringLength(100)]
        public string? AssignedTo { get; set; }

        public bool AllowScheduleConflict { get; set; }
    }

    public class CreateRentalItemPriceDto
    {
        public string? ItemId { get; set; }

        public int? ItemDefinitionId { get; set; }

        [Range(0, double.MaxValue)]
        public decimal PerItemPrice { get; set; }
    }

    public class UpdateRentalDto
    {
        public Guid? RenterId { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? ExpectedShipDate { get; set; }
        public DateTime? ExpectedEndDate { get; set; }
        public DateTime? ExpectedReturnDate { get; set; }
        public bool? HasRenewalIntent { get; set; }
        public DateTime? RenewalIntentEndDate { get; set; }
        [Range(0, double.MaxValue)]
        public decimal? TotalPrice { get; set; }
        [Range(0, double.MaxValue)]
        public decimal? Deposit { get; set; }
        [Range(0, double.MaxValue)]
        public decimal? OtherFee { get; set; }
        [StringLength(500)]
        public string? ShippingAddress { get; set; }
        [StringLength(100)]
        public string? PlatformOrderNo { get; set; }
        [StringLength(500)]
        public string? Notes { get; set; }
        [StringLength(100)]
        public string? AssignedTo { get; set; }

        public string? CreatedBy { get; set; }

        public string? SenderName { get; set; }

        public bool AllowScheduleConflict { get; set; }
    }

    public class CreateRentalResult
    {
        public RentalDto? Rental { get; set; }
        public string? Error { get; set; }
        public RentalCreateConflictDto? Conflict { get; set; }
    }

    public class UpdateRentalResult
    {
        public RentalDto? Rental { get; set; }
        public string? Error { get; set; }
        public RentalCreateConflictDto? Conflict { get; set; }
    }

    public class RenewRentalDto
    {
        public DateTime? StartDate { get; set; }

        [Required]
        public DateTime ExpectedEndDate { get; set; }

        [Range(0, double.MaxValue)]
        public decimal TotalPrice { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? Deposit { get; set; }

        [Range(0, double.MaxValue)]
        public decimal OtherFee { get; set; }

        [StringLength(500)]
        public string? Notes { get; set; }

        public bool AllowScheduleConflict { get; set; }
    }

    public class RenewRentalResult
    {
        public RentalDto? OriginalRental { get; set; }
        public RentalDto? RenewalRental { get; set; }
        public string? Error { get; set; }
        public RentalCreateConflictDto? Conflict { get; set; }
    }

    public class UpdateRentalItemsDto
    {
        public List<string> ItemIds { get; set; } = new();

        public List<int> ItemDefinitionIds { get; set; } = new();

        public bool AllowScheduleConflict { get; set; }
    }

    public class RentalItemsUpdateResult
    {
        public RentalDto? Rental { get; set; }
        public string? Error { get; set; }
        public RentalCreateConflictDto? Conflict { get; set; }
    }

    public class RentalShipmentResult
    {
        public RentalDto? Rental { get; set; }
        public string? Error { get; set; }
        public RentalCreateConflictDto? Conflict { get; set; }
    }

    public class RentalCreateConflictDto
    {
        public string Message { get; set; } = "Selected items have rental time conflicts.";
        public List<RentalScheduleConflictDto> PendingShipmentConflicts { get; set; } = new();
        public List<RentalScheduleConflictDto> ShippedConflicts { get; set; } = new();
        public List<RentalScheduleConflictDto> ReturnPendingConflicts { get; set; } = new();
    }

    public class RentalScheduleConflictDto
    {
        public Guid RentalId { get; set; }
        public string RentalNumber { get; set; } = string.Empty;
        public RentalStatus RentalStatus { get; set; }
        public Guid ItemId { get; set; }
        public string ItemShortId { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public DateTime StartDate { get; set; }
        public DateTime ExpectedEndDate { get; set; }
        public bool HasRenewalIntent { get; set; }
        public DateTime? RenewalIntentEndDate { get; set; }
        public bool HasOutboundShipment { get; set; }
        public string? ConflictReason { get; set; }
    }

    public enum RentalCalendarEventKind
    {
        RentalPeriod,
        ShipmentRequired,
        ReturnRequired,
        OutboundShipment,
        InboundShipment,
        Reminder
    }

    public class RentalCalendarQueryParameters
    {
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public string? TargetUser { get; set; }
    }

    public class RentalCalendarEventDto
    {
        public string Id { get; set; } = string.Empty;
        public RentalCalendarEventKind Kind { get; set; }
        public ReminderType? ReminderType { get; set; }
        public ReminderLevel Level { get; set; } = ReminderLevel.Info;
        public Guid? RentalId { get; set; }
        public string? RentalNumber { get; set; }
        public Guid? RenterId { get; set; }
        public string? RenterName { get; set; }
        public RentalStatus? RentalStatus { get; set; }
        public bool HasRenewalIntent { get; set; }
        public DateTime? RenewalIntentEndDate { get; set; }
        public int? ReminderId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime StartAt { get; set; }
        public DateTime EndAt { get; set; }
        public bool AllDay { get; set; } = true;
        public bool IsOpen { get; set; } = true;
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

        public bool AllowOpenItemConflict { get; set; }

        public List<RentalItemShipSelectionDto> ItemSelections { get; set; } = new();
    }

    public class RentalItemShipSelectionDto
    {
        public int RentalItemId { get; set; }
        public Guid ItemId { get; set; }
    }

    public class DeliverShipmentDto
    {
        public DateTime? DeliveredAt { get; set; }
    }

    public class UpdateShipmentDto
    {
        [Range(0, double.MaxValue)]
        public decimal? ShippingFee { get; set; }
    }

    public class ReturnRentalItemDto
    {
        [Required]
        public int RentalItemId { get; set; }

        public ReturnCondition? Condition { get; set; }
    }

    public class ReturnRentalDto
    {
        // If null/empty, return all outstanding rental items.
        public List<int>? RentalItemIds { get; set; }

        // Per-item condition takes precedence over the legacy global Condition.
        public List<ReturnRentalItemDto> Items { get; set; } = new();
        public ReturnCondition? Condition { get; set; }
        [StringLength(500)]
        public string? Notes { get; set; }

        // When a damaged item needs repair, keep it occupied as a normal loan.
        public bool RepairOccupancy { get; set; }
        public DateTime? RepairExpectedReturnDate { get; set; }
    }

    public class CancelRentalDto
    {
        [StringLength(500)]
        public string? Reason { get; set; }
    }

    public class BulkUpdateRentalItemDto
    {
        [Required]
        public int RentalItemId { get; set; }

        public string? ListingRemarks { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? PerItemPrice { get; set; }
    }

    public class BulkUpdateRentalItemsDto
    {
        [Required]
        [MinLength(1)]
        public List<BulkUpdateRentalItemDto> Items { get; set; } = new();
    }

    public class RentalQueryParameters
    {
        public RentalStatus? Status { get; set; }
        public Guid? RenterId { get; set; }
        public string? Search { get; set; }
        public string? RentalNumber { get; set; }
        public DateTime? StartDateFrom { get; set; }
        public DateTime? StartDateTo { get; set; }
        public bool PendingSettlement { get; set; }
        public string? SortField { get; set; }
        public string? SortOrder { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
    }
}

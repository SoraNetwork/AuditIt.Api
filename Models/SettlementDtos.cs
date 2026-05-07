using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class SettlementSettingDto
    {
        public decimal TechnicianPercent { get; set; }
        public decimal CreatorPercent { get; set; }
        public decimal ItemOwnerPercent { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
    }

    public class UpdateSettlementSettingDto
    {
        [Range(0, 100)]
        public decimal TechnicianPercent { get; set; }

        [Range(0, 100)]
        public decimal CreatorPercent { get; set; }

        [Range(0, 100)]
        public decimal ItemOwnerPercent { get; set; }
    }

    public class SettlementPreviewDto
    {
        public Guid RentalId { get; set; }
        public string RentalNumber { get; set; } = string.Empty;
        public RentalStatus Status { get; set; }
        public decimal TotalPrice { get; set; }
        public decimal AccountedAmount { get; set; }
        public decimal TechnicianPercent { get; set; }
        public decimal TechnicianAmount { get; set; }
        public decimal CreatorPercent { get; set; }
        public decimal CreatorAmount { get; set; }
        public string? CreatorName { get; set; }
        public decimal ItemOwnerPercent { get; set; }
        public decimal ItemOwnerAmount { get; set; }
        public List<SettlementOwnerShareDto> OwnerShares { get; set; } = new();
        public string MarkdownText { get; set; } = string.Empty;
        public bool CanSend { get; set; }
        public string? IneligibleReason { get; set; }
        public DateTime? SettlementNotifiedAt { get; set; }
        public string? SettlementNotifiedStatus { get; set; }
    }

    public class SettlementOwnerShareDto
    {
        public string OwnerName { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }
}

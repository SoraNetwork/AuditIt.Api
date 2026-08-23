namespace AuditIt.Api.Models
{
    public class FinanceReportQueryParameters
    {
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
    }

    public class FinanceReportSummaryDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public int RentalCount { get; set; }
        public int ActiveRentalCount { get; set; }
        public int ClosedRentalCount { get; set; }
        public decimal TotalOrderAmount { get; set; }
        public decimal TotalDeposit { get; set; }
        public decimal TotalShippingFee { get; set; }
        public decimal TotalOtherFee { get; set; }
        public decimal AccountedAmount { get; set; }
        public List<FinanceReportCategorySummaryDto> Categories { get; set; } = new();
        public List<FinanceReportStatusSummaryDto> Statuses { get; set; } = new();
        public List<FinanceReportPaymentAccountSummaryDto> PaymentAccounts { get; set; } = new();
    }

    public class FinanceReportCategorySummaryDto
    {
        public string Category { get; set; } = string.Empty;
        public int Count { get; set; }
        public decimal TotalOrderAmount { get; set; }
        public decimal AccountedAmount { get; set; }
    }

    public class FinanceReportStatusSummaryDto
    {
        public RentalStatus Status { get; set; }
        public int Count { get; set; }
        public decimal TotalOrderAmount { get; set; }
        public decimal AccountedAmount { get; set; }
    }

    public class FinanceReportPaymentAccountSummaryDto
    {
        public string PaymentAccount { get; set; } = "未填写";
        public int Count { get; set; }
        public decimal TotalOrderAmount { get; set; }
        public decimal AccountedAmount { get; set; }
    }

    public class FinanceReportDetailDto
    {
        public Guid RentalId { get; set; }
        public string RentalNumber { get; set; } = string.Empty;
        public RentalStatus Status { get; set; }
        public Guid RenterId { get; set; }
        public string? RenterName { get; set; }
        public string? AssignedTo { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime ExpectedEndDate { get; set; }
        public DateTime CreatedAt { get; set; }
        public decimal TotalPrice { get; set; }
        public decimal Deposit { get; set; }
        public decimal TotalShippingFee { get; set; }
        public decimal OtherFee { get; set; }
        public decimal AccountedAmount { get; set; }
        public int ItemCount { get; set; }
        public string? PlatformOrderNo { get; set; }
        public string? PaymentAccount { get; set; }
    }
}

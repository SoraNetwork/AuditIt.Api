namespace AuditIt.Api.Models
{
    public class ItemAvailabilityCalendarDto
    {
        public ItemDto Item { get; set; } = new();
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public List<ItemBusyPeriodDto> BusyPeriods { get; set; } = new();
        public List<ItemFreePeriodDto> FreePeriods { get; set; } = new();
    }

    public class ItemBusyPeriodDto
    {
        public Guid RentalId { get; set; }
        public string RentalNumber { get; set; } = string.Empty;
        public RentalStatus RentalStatus { get; set; }
        public Guid RenterId { get; set; }
        public string? RenterName { get; set; }
        public DateTime StartAt { get; set; }
        public DateTime EndAt { get; set; }
        public bool IsOpen { get; set; }
        public bool IsUncertain { get; set; }
        public bool IsManualLoan { get; set; }
        public ItemOccupancyStatus OccupancyStatus { get; set; } = ItemOccupancyStatus.Scheduled;
    }

    public class ItemFreePeriodDto
    {
        public DateTime StartAt { get; set; }
        public DateTime EndAt { get; set; }
    }

    public class ItemDefinitionOccupancyCalendarDto
    {
        public int ItemDefinitionId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int TotalStock { get; set; }
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public List<ItemDefinitionDailyStockDto> DailyStocks { get; set; } = new();
    }

    public class ItemDefinitionDailyStockDto
    {
        public DateTime Date { get; set; }
        public int TotalStock { get; set; }
        public int OccupiedCount { get; set; }
        public int RemainingStock { get; set; }
        public List<ItemDefinitionDailyOccupancyDto> Details { get; set; } = new();
    }

    public class ItemDefinitionDailyOccupancyDto
    {
        public Guid RentalId { get; set; }
        public string RentalNumber { get; set; } = string.Empty;
        public RentalStatus RentalStatus { get; set; }
        public Guid RenterId { get; set; }
        public string? RenterName { get; set; }
        public int Quantity { get; set; }
        public bool IsUncertain { get; set; }
        public bool IsManualLoan { get; set; }
        public ItemOccupancyStatus OccupancyStatus { get; set; } = ItemOccupancyStatus.Scheduled;
    }

    public enum ItemOccupancyStatus
    {
        Scheduled,
        Returning
    }
}

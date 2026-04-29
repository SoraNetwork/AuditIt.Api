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
        public string? RenterName { get; set; }
        public DateTime StartAt { get; set; }
        public DateTime EndAt { get; set; }
        public bool IsOpen { get; set; }
    }

    public class ItemFreePeriodDto
    {
        public DateTime StartAt { get; set; }
        public DateTime EndAt { get; set; }
    }
}

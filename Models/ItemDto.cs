namespace AuditIt.Api.Models
{
    public class ItemDto
    {
        public string Id { get; set; } = string.Empty;
        public string ShortId { get; set; } = string.Empty;
        public string? SerialNumber { get; set; }

        public int ItemDefinitionId { get; set; }
        public string ItemDefinitionName { get; set; } = string.Empty;

        public int WarehouseId { get; set; }
        public string WarehouseName { get; set; } = string.Empty;

        public List<string> OwnerUserNames { get; set; } = [];
        public string? OwnerUserName { get; set; }

        public ItemStatus Status { get; set; }
        public string? CurrentDestination { get; set; }
        public string? Remarks { get; set; }
        public string? PhotoUrl { get; set; }

        public string EntryDate { get; set; } = string.Empty;
        public string LastUpdated { get; set; } = string.Empty;
    }
}

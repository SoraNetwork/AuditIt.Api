namespace AuditIt.Api.Models
{
    public class ItemQueryParameters
    {
        public int? WarehouseId { get; set; }
        public int? CategoryId { get; set; }
        public ItemStatus? Status { get; set; }
        public Guid? Id { get; set; }
        public string? ShortId { get; set; }
        public string? SerialNumber { get; set; }
        public string? Search { get; set; }
    }

    public class CheckAnalysisQueryParameters
    {
        public int? WarehouseId { get; set; }
        public int? CategoryId { get; set; }
        public string? Search { get; set; }
        public DateTime? StartAt { get; set; }
        public DateTime? EndAt { get; set; }
    }

    public class CheckAnalysisResultDto
    {
        public List<ItemDto> CheckedItems { get; set; } = new();
        public List<ItemDto> UncheckedItems { get; set; } = new();
    }
}

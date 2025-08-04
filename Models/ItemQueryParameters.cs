namespace AuditIt.Api.Models
{
    public class ItemQueryParameters
    {
        public int? WarehouseId { get; set; }
        public ItemStatus? Status { get; set; }
        public Guid? Id { get; set; }
        public string? ShortId { get; set; }
    }
}
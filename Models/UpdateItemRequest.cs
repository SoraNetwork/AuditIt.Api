namespace AuditIt.Api.Models
{
    public class UpdateItemRequest
    {
        public string? Destination { get; set; }
        public ItemStatus? Status { get; set; }
    }
}

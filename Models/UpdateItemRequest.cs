namespace AuditIt.Api.Models
{
    public class UpdateExpectedReturnDateRequest
    {
        public DateTime? ExpectedReturnDate { get; set; }
    }

    public class UpdateItemRequest
    {
        public string? Destination { get; set; }
        public DateTime? ExpectedReturnDate { get; set; }
        public ItemStatus? Status { get; set; }
        public bool PermanentlyHidden { get; set; }
    }
}

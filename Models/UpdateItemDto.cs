using Microsoft.AspNetCore.Http;

namespace AuditIt.Api.Models
{
    public class UpdateItemDto
    {
        public string? ShortId { get; set; }
        public string? SerialNumber { get; set; }
        public string? Remarks { get; set; }
        public string? CurrentDestination { get; set; }
        public decimal? ItemValue { get; set; }
        public List<string>? OwnerUserNames { get; set; }
        public bool? ClearOwnerUser { get; set; }
        public IFormFile? Photo { get; set; }
        public bool? DeletePhoto { get; set; }
    }
}


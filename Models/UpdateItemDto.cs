using Microsoft.AspNetCore.Http;

namespace AuditIt.Api.Models
{
    public class UpdateItemDto
    {
        public string? ShortId { get; set; }
        public string? Remarks { get; set; }
        public string? CurrentDestination { get; set; }
        public IFormFile? Photo { get; set; }
        public bool? DeletePhoto { get; set; }
    }
}


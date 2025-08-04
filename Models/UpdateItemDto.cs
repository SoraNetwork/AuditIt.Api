using Microsoft.AspNetCore.Http;

namespace AuditIt.Api.Models
{
    public class UpdateItemDto
    {
        public string? Remarks { get; set; }
        public IFormFile? Photo { get; set; }
        public bool? DeletePhoto { get; set; }
    }
}


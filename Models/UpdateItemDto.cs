using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class UpdateItemDto
    {
        [StringLength(500)]
        public string? Remarks { get; set; }

        public IFormFile? Photo { get; set; }
    }
}

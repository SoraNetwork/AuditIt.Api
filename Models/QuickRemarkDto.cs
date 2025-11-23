using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class CreateQuickRemarkDto
    {
        [Required]
        [StringLength(200)]
        public string Content { get; set; } = string.Empty;
    }

    public class QuickRemarkDto
    {
        public int Id { get; set; }
        
        public string Content { get; set; } = string.Empty;
        
        public DateTime CreatedAt { get; set; }
    }
}
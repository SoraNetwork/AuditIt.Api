using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class QuickRemark
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        [StringLength(200)]
        public string Content { get; set; } = string.Empty;
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class Warehouse
    {
        [Key]
        public int Id { get; set; }
        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;
        [StringLength(200)]
        public string? Location { get; set; }
        public int Capacity { get; set; }
        public string? Description { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}

using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class User
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        [Required]
        [StringLength(100)]
        public string Name { get; set; }
        [StringLength(100)]
        public string DingTalkId { get; set; }
    }
}

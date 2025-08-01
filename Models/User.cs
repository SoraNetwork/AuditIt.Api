using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class User
    {
        [Key]
        public int Id { get; set; }
        [Required]
        [StringLength(100)]
        public string Name { get; set; }
        [StringLength(20)]
        public string Phone { get; set; }
        [StringLength(100)]
        public string DingTalkId { get; set; }
    }
}

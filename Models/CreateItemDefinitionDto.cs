using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class CreateItemDefinitionDto
    {
        [Required]
        [StringLength(100)]
        public string Name { get; set; }

        [Required]
        public int CategoryId { get; set; }

        [StringLength(50)]
        public string Unit { get; set; }

        public string Description { get; set; }
    }
}

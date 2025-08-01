using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class CreateCategoryDto
    {
        [Required]
        [StringLength(100)]
        public string Name { get; set; }

        public string Description { get; set; }
    }
}

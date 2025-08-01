using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class CreateWarehouseDto
    {
        [Required]
        [StringLength(100)]
        public string Name { get; set; }

        [StringLength(200)]
        public string Location { get; set; }

        public int Capacity { get; set; }

        public string Description { get; set; }
    }
}

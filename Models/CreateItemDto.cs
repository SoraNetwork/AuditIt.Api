using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class CreateItemDto
    {
        [Required]
        public int ItemDefinitionId { get; set; }

        [Required]
        public int WarehouseId { get; set; }

        [StringLength(50)]
        public string? ShortId { get; set; } // External Barcode

        [StringLength(100)]
        public string? SerialNumber { get; set; }

        [StringLength(500)]
        public string? Remarks { get; set; }

        public IFormFile? Photo { get; set; }
    }
}

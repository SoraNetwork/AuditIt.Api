using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class TransferWarehouseRequest
    {
        [Required]
        public int NewWarehouseId { get; set; }
        
        [StringLength(500)]
        public string? Remarks { get; set; }
    }
}

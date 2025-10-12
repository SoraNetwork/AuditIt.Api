using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class CreateItemsDto
    {
        [Required]
        public int ItemDefinitionId { get; set; }

        [Required]
        public int WarehouseId { get; set; }
        [Required]
        public List<Item> Items { get; set; } = [];
        public class Item
        {
            
            [StringLength(50)]
            public string? ShortId { get; set; } 
            [StringLength(500)]
            public string? Remarks { get; set; }

        }
    }
}

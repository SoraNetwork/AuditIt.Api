using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AuditIt.Api.Models
{
    public class SettlementSetting
    {
        [Key]
        public int Id { get; set; } = 1;

        [Range(0, 100)]
        [Column(TypeName = "decimal(5,2)")]
        public decimal TechnicianPercent { get; set; } = 10m;

        [Range(0, 100)]
        [Column(TypeName = "decimal(5,2)")]
        public decimal CreatorPercent { get; set; } = 30m;

        [Range(0, 100)]
        [Column(TypeName = "decimal(5,2)")]
        public decimal ShipperPercent { get; set; } = 10m;

        [Range(0, 100)]
        [Column(TypeName = "decimal(5,2)")]
        public decimal ItemOwnerPercent { get; set; } = 50m;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [StringLength(100)]
        public string? UpdatedBy { get; set; }
    }
}

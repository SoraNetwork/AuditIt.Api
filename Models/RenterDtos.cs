using System;
using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class RenterDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Phone { get; set; }
        public string? IdCardNo { get; set; }
        public string? XianyuId { get; set; }
        public string? TaobaoId { get; set; }
        public string? XiaohongshuId { get; set; }
        public string? DefaultAddress { get; set; }
        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    public class CreateRenterDto
    {
        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        [StringLength(30)]
        public string? Phone { get; set; }

        [StringLength(30)]
        public string? IdCardNo { get; set; }

        [StringLength(100)]
        public string? XianyuId { get; set; }

        [StringLength(100)]
        public string? TaobaoId { get; set; }

        [StringLength(100)]
        public string? XiaohongshuId { get; set; }

        [StringLength(500)]
        public string? DefaultAddress { get; set; }

        [StringLength(500)]
        public string? Notes { get; set; }
    }

    public class UpdateRenterDto : CreateRenterDto
    {
    }

    // Inline renter form used during rental creation. Phone-keyed upsert.
    public class RenterInlineDto
    {
        public Guid? RenterId { get; set; }

        [StringLength(100)]
        public string? Name { get; set; }

        [StringLength(30)]
        public string? Phone { get; set; }

        [StringLength(30)]
        public string? IdCardNo { get; set; }

        [StringLength(100)]
        public string? XianyuId { get; set; }

        [StringLength(100)]
        public string? TaobaoId { get; set; }

        [StringLength(100)]
        public string? XiaohongshuId { get; set; }

        [StringLength(500)]
        public string? DefaultAddress { get; set; }

        [StringLength(500)]
        public string? Notes { get; set; }
    }
}

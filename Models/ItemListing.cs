using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AuditIt.Api.Models
{
    public enum ListingPlatform
    {
        Xianyu,
        Taobao,
        Xiaohongshu,
        Other
    }

    public enum ListingStatus
    {
        Draft,
        Listed,
        Hidden,
        Sold
    }

    public class ItemListing
    {
        [Key]
        public int Id { get; set; }

        public Guid ItemId { get; set; }
        [ForeignKey("ItemId")]
        public virtual Item? Item { get; set; }

        public ListingPlatform Platform { get; set; }

        [Required]
        [StringLength(1024)]
        public string Url { get; set; } = string.Empty;

        [StringLength(200)]
        public string? Title { get; set; }

        public ListingStatus Status { get; set; } = ListingStatus.Listed;

        [StringLength(500)]
        public string? Remarks { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}

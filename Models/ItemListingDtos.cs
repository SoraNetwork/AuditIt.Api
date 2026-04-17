using System;
using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class ItemListingDto
    {
        public int Id { get; set; }
        public Guid ItemId { get; set; }
        public ListingPlatform Platform { get; set; }
        public string Url { get; set; } = string.Empty;
        public string? Title { get; set; }
        public ListingStatus Status { get; set; }
        public string? Remarks { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class CreateItemListingDto
    {
        [Required]
        public ListingPlatform Platform { get; set; }

        [Required]
        [StringLength(1024)]
        public string Url { get; set; } = string.Empty;

        [StringLength(200)]
        public string? Title { get; set; }

        public ListingStatus Status { get; set; } = ListingStatus.Listed;

        [StringLength(500)]
        public string? Remarks { get; set; }
    }

    public class UpdateItemListingDto
    {
        public ListingPlatform? Platform { get; set; }

        [StringLength(1024)]
        public string? Url { get; set; }

        [StringLength(200)]
        public string? Title { get; set; }

        public ListingStatus? Status { get; set; }

        [StringLength(500)]
        public string? Remarks { get; set; }
    }
}

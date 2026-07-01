using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AuditIt.Api.Services
{
    public class ItemListingService : IItemListingService
    {
        private readonly ApplicationDbContext _context;

        public ItemListingService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<ItemListingDto>> GetByItemAsync(Guid itemId)
        {
            var itemExists = await _context.Items.AnyAsync(i => i.Id == itemId);
            if (!itemExists) return Array.Empty<ItemListingDto>();

            return await _context.ItemListings
                .Where(l => l.ItemId == itemId)
                .OrderByDescending(l => l.UpdatedAt)
                .Select(l => ToDto(l))
                .ToListAsync();
        }

        public async Task<ItemListingDto?> CreateAsync(Guid itemId, CreateItemListingDto dto)
        {
            var itemExists = await _context.Items.AnyAsync(i => i.Id == itemId);
            if (!itemExists) return null;

            var listing = new ItemListing
            {
                ItemId = itemId,
                Platform = dto.Platform,
                Url = dto.Url,
                Title = dto.Title,
                Status = dto.Status,
                Remarks = dto.Remarks,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _context.ItemListings.Add(listing);
            await _context.SaveChangesAsync();
            return ToDto(listing);
        }

        public async Task<ItemListingDto?> UpdateAsync(int id, UpdateItemListingDto dto)
        {
            var listing = await _context.ItemListings
                .Where(l => _context.Items.Any(i => i.Id == l.ItemId))
                .FirstOrDefaultAsync(l => l.Id == id);
            if (listing == null) return null;

            if (dto.Platform.HasValue) listing.Platform = dto.Platform.Value;
            if (dto.Url != null) listing.Url = dto.Url;
            if (dto.Title != null) listing.Title = dto.Title;
            if (dto.Status.HasValue) listing.Status = dto.Status.Value;
            if (dto.Remarks != null) listing.Remarks = dto.Remarks;
            listing.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return ToDto(listing);
        }

        public async Task<bool> DeleteAsync(int id)
        {
            var listing = await _context.ItemListings
                .Where(l => _context.Items.Any(i => i.Id == l.ItemId))
                .FirstOrDefaultAsync(l => l.Id == id);
            if (listing == null) return false;
            _context.ItemListings.Remove(listing);
            await _context.SaveChangesAsync();
            return true;
        }

        private static ItemListingDto ToDto(ItemListing l) => new()
        {
            Id = l.Id,
            ItemId = l.ItemId,
            Platform = l.Platform,
            Url = l.Url,
            Title = l.Title,
            Status = l.Status,
            Remarks = l.Remarks,
            CreatedAt = l.CreatedAt,
            UpdatedAt = l.UpdatedAt
        };
    }
}

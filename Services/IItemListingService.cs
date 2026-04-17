using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    public interface IItemListingService
    {
        Task<IEnumerable<ItemListingDto>> GetByItemAsync(Guid itemId);
        Task<ItemListingDto?> CreateAsync(Guid itemId, CreateItemListingDto dto);
        Task<ItemListingDto?> UpdateAsync(int id, UpdateItemListingDto dto);
        Task<bool> DeleteAsync(int id);
    }
}

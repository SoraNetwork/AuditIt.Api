using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuditIt.Api.Controllers
{
    [ApiController]
    [Authorize]
    public class ItemListingsController : ControllerBase
    {
        private readonly IItemListingService _listings;

        public ItemListingsController(IItemListingService listings)
        {
            _listings = listings;
        }

        [HttpGet("api/items/{itemId:guid}/listings")]
        [RequirePermission(PermissionCodes.ItemView)]
        public async Task<ActionResult<IEnumerable<ItemListingDto>>> GetForItem(Guid itemId)
        {
            return Ok(await _listings.GetByItemAsync(itemId));
        }

        [HttpPost("api/items/{itemId:guid}/listings")]
        [RequirePermission(PermissionCodes.ListingManage)]
        public async Task<ActionResult<ItemListingDto>> Create(Guid itemId, [FromBody] CreateItemListingDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var created = await _listings.CreateAsync(itemId, dto);
            if (created == null) return NotFound("物品不存在。");
            return CreatedAtAction(nameof(GetForItem), new { itemId }, created);
        }

        [HttpPut("api/listings/{id:int}")]
        [RequirePermission(PermissionCodes.ListingManage)]
        public async Task<ActionResult<ItemListingDto>> Update(int id, [FromBody] UpdateItemListingDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var updated = await _listings.UpdateAsync(id, dto);
            return updated == null ? NotFound() : Ok(updated);
        }

        [HttpDelete("api/listings/{id:int}")]
        [RequirePermission(PermissionCodes.ListingManage)]
        public async Task<IActionResult> Delete(int id)
        {
            var ok = await _listings.DeleteAsync(id);
            return ok ? NoContent() : NotFound();
        }
    }
}

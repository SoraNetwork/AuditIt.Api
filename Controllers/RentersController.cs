using System.Security.Claims;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuditIt.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class RentersController : ControllerBase
    {
        private readonly IRenterService _renters;

        public RentersController(IRenterService renters)
        {
            _renters = renters;
        }

        [HttpGet]
        [RequirePermission(PermissionCodes.RenterView)]
        public async Task<ActionResult<IEnumerable<RenterDto>>> Search([FromQuery] string? keyword, [FromQuery] int limit = 50)
        {
            return Ok(await _renters.SearchAsync(keyword, limit));
        }

        [HttpGet("{id:guid}")]
        [RequirePermission(PermissionCodes.RenterView)]
        public async Task<ActionResult<RenterDto>> GetById(Guid id)
        {
            var renter = await _renters.GetByIdAsync(id);
            return renter == null ? NotFound() : Ok(renter);
        }

        [HttpPost]
        [RequirePermission(PermissionCodes.RenterManage)]
        public async Task<ActionResult<RenterDto>> Create([FromBody] CreateRenterDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var created = await _renters.CreateAsync(dto, CurrentUser());
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }

        [HttpPut("{id:guid}")]
        [RequirePermission(PermissionCodes.RenterManage)]
        public async Task<ActionResult<RenterDto>> Update(Guid id, [FromBody] UpdateRenterDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var updated = await _renters.UpdateAsync(id, dto, CurrentUser());
            return updated == null ? NotFound() : Ok(updated);
        }

        [HttpDelete("{id:guid}")]
        [RequirePermission(PermissionCodes.RenterManage)]
        public async Task<IActionResult> Delete(Guid id)
        {
            var ok = await _renters.DeleteAsync(id);
            if (!ok) return Conflict("租客不存在或存在关联租赁单。");
            return NoContent();
        }

        private string? CurrentUser() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}

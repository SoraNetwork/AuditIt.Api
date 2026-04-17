using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuditIt.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class QuickRemarksController : ControllerBase
    {
        private readonly IQuickRemarkService _quickRemarkService;

        public QuickRemarksController(IQuickRemarkService quickRemarkService)
        {
            _quickRemarkService = quickRemarkService;
        }

        [HttpGet]
        [RequirePermission(PermissionCodes.ItemView)]
        public async Task<ActionResult<IEnumerable<QuickRemarkDto>>> GetQuickRemarks()
        {
            var quickRemarks = await _quickRemarkService.GetAllQuickRemarksAsync();
            return Ok(quickRemarks);
        }

        [HttpPost]
        [RequirePermission(PermissionCodes.ItemUpdate)]
        public async Task<ActionResult<QuickRemarkDto>> CreateQuickRemark([FromBody] CreateQuickRemarkDto dto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var quickRemark = await _quickRemarkService.CreateQuickRemarkAsync(dto);
            return CreatedAtAction(nameof(GetQuickRemarks), new { id = quickRemark.Id }, quickRemark);
        }

        [HttpDelete("{id}")]
        [RequirePermission(PermissionCodes.ItemUpdate)]
        public async Task<IActionResult> DeleteQuickRemark(int id)
        {
            var result = await _quickRemarkService.DeleteQuickRemarkAsync(id);
            if (!result)
            {
                return NotFound();
            }

            return NoContent();
        }
    }
}
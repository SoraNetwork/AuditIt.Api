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
    public class RemindersController : ControllerBase
    {
        private readonly IReminderService _reminders;

        public RemindersController(IReminderService reminders)
        {
            _reminders = reminders;
        }

        [HttpGet]
        [RequirePermission(PermissionCodes.ReminderView)]
        public async Task<ActionResult<IEnumerable<ReminderDto>>> List([FromQuery] ReminderQueryParameters query)
        {
            return Ok(await _reminders.ListAsync(query, CurrentUser(), CanDismissAny()));
        }

        [HttpPost("{id:int}/dismiss")]
        [RequirePermission(PermissionCodes.ReminderView)]
        public async Task<IActionResult> Dismiss(int id)
        {
            var ok = await _reminders.DismissAsync(id, CurrentUser(), CanDismissAny());
            return ok ? NoContent() : NotFound();
        }

        [HttpPost("dismiss-all")]
        [RequirePermission(PermissionCodes.ReminderView)]
        public async Task<ActionResult<object>> DismissAll([FromQuery] string? targetUser)
        {
            var count = await _reminders.DismissAllAsync(targetUser, CurrentUser(), CanDismissAny());
            return Ok(new { dismissed = count });
        }

        [HttpPost]
        [RequirePermission(PermissionCodes.ReminderCreate)]
        public async Task<ActionResult<IEnumerable<ReminderDto>>> Create([FromBody] CreateReminderDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var created = await _reminders.CreateManualAsync(dto, CurrentUser());
            return Ok(created);
        }

        private string? CurrentUser() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        private bool CanDismissAny() => User.HasPermission(PermissionCodes.ReminderDismissAny);
    }
}

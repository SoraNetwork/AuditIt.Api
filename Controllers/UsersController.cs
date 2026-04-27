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
    public class UsersController : ControllerBase
    {
        private readonly IUserService _users;

        public UsersController(IUserService users)
        {
            _users = users;
        }

        [HttpGet]
        [RequirePermission(PermissionCodes.UserView)]
        public async Task<ActionResult<IEnumerable<UserDto>>> Search([FromQuery] UserQueryParameters query)
        {
            return Ok(await _users.SearchAsync(query));
        }

        [HttpGet("{id:guid}")]
        [RequirePermission(PermissionCodes.UserView)]
        public async Task<ActionResult<UserDto>> Get(Guid id)
        {
            var user = await _users.GetAsync(id);
            return user == null ? NotFound() : Ok(user);
        }

        [HttpPut("{id:guid}/status")]
        [RequirePermission(PermissionCodes.UserManage)]
        public async Task<ActionResult<UserDto>> UpdateStatus(Guid id, [FromBody] UpdateUserStatusDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var updated = await _users.UpdateStatusAsync(id, dto);
            return updated == null ? NotFound() : Ok(updated);
        }

        [HttpPut("{id:guid}/roles")]
        [RequirePermission(PermissionCodes.UserManage)]
        public async Task<ActionResult<UserDto>> AssignRoles(Guid id, [FromBody] AssignRolesDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var updated = await _users.AssignRolesAsync(id, dto, CurrentUser());
            return updated == null ? NotFound() : Ok(updated);
        }

        [HttpPost("sync-dingtalk")]
        [RequirePermission(PermissionCodes.UserManage)]
        public async Task<ActionResult<SyncDingTalkUsersResultDto>> SyncDingTalk([FromBody] SyncDingTalkUsersDto dto, CancellationToken ct)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            return Ok(await _users.SyncDingTalkUsersAsync(dto, CurrentUser(), ct));
        }

        private string? CurrentUser() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}

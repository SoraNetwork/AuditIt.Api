using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuditIt.Api.Controllers
{
    [ApiController]
    [Route("api")]
    [Authorize]
    public class RolesController : ControllerBase
    {
        private readonly IRoleService _roles;

        public RolesController(IRoleService roles)
        {
            _roles = roles;
        }

        [HttpGet("roles")]
        [RequirePermission(PermissionCodes.RoleManage)]
        public async Task<ActionResult<IEnumerable<RoleDto>>> List()
        {
            return Ok(await _roles.ListAsync());
        }

        [HttpGet("roles/{id:int}")]
        [RequirePermission(PermissionCodes.RoleManage)]
        public async Task<ActionResult<RoleDto>> Get(int id)
        {
            var role = await _roles.GetAsync(id);
            return role == null ? NotFound() : Ok(role);
        }

        [HttpPost("roles")]
        [RequirePermission(PermissionCodes.RoleManage)]
        public async Task<ActionResult<RoleDto>> Create([FromBody] CreateRoleDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var (role, error) = await _roles.CreateAsync(dto);
            if (error != null) return BadRequest(new { error });
            return CreatedAtAction(nameof(Get), new { id = role!.Id }, role);
        }

        [HttpPut("roles/{id:int}")]
        [RequirePermission(PermissionCodes.RoleManage)]
        public async Task<ActionResult<RoleDto>> Update(int id, [FromBody] UpdateRoleDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var (role, error) = await _roles.UpdateAsync(id, dto);
            if (error != null) return BadRequest(new { error });
            return role == null ? NotFound() : Ok(role);
        }

        [HttpDelete("roles/{id:int}")]
        [RequirePermission(PermissionCodes.RoleManage)]
        public async Task<IActionResult> Delete(int id)
        {
            var (ok, error) = await _roles.DeleteAsync(id);
            if (!ok) return Conflict(new { error });
            return NoContent();
        }

        [HttpGet("permissions")]
        [RequirePermission(PermissionCodes.RoleManage)]
        public async Task<ActionResult<IEnumerable<PermissionDto>>> ListPermissions()
        {
            return Ok(await _roles.ListPermissionsAsync());
        }
    }
}

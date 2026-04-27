using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    public interface IRoleService
    {
        Task<IEnumerable<RoleDto>> ListAsync();
        Task<RoleDto?> GetAsync(int id);
        Task<(RoleDto? role, string? error)> CreateAsync(CreateRoleDto dto);
        Task<(RoleDto? role, string? error)> UpdateAsync(int id, UpdateRoleDto dto);
        Task<(bool ok, string? error)> DeleteAsync(int id);

        Task<IEnumerable<PermissionDto>> ListPermissionsAsync();
    }

    public interface IUserService
    {
        Task<IEnumerable<UserDto>> SearchAsync(UserQueryParameters query);
        Task<UserDto?> GetAsync(Guid id);
        Task<UserDto?> UpdateStatusAsync(Guid id, UpdateUserStatusDto dto);
        Task<UserDto?> AssignRolesAsync(Guid id, AssignRolesDto dto, string? currentUser);
        Task<SyncDingTalkUsersResultDto> SyncDingTalkUsersAsync(SyncDingTalkUsersDto dto, string? currentUser, CancellationToken ct = default);
    }
}

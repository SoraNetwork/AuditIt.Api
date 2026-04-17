using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    public interface IIdentityService
    {
        // 根据钉钉回调的 name/dingtalk-id，保证 User 存在且激活；返回其权限码列表。
        // 如果该 Name 命中 BootstrapAdminNames，会在首次登录时自动挂 Admin 角色。
        Task<(User user, IReadOnlyList<string> permissions)?> LoginUpsertAsync(string name, string? dingTalkId);

        Task<IReadOnlyList<string>> GetPermissionsForUserAsync(Guid userId);

        Task<IReadOnlyList<string>> GetUsersWithPermissionAsync(string permissionCode);
    }
}

using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuditIt.Api.Services
{
    public class IdentityService : IIdentityService
    {
        private readonly ApplicationDbContext _context;
        private readonly IOptions<AuthOptions> _authOptions;

        public IdentityService(ApplicationDbContext context, IOptions<AuthOptions> authOptions)
        {
            _context = context;
            _authOptions = authOptions;
        }

        public async Task<(User user, IReadOnlyList<string> permissions)?> LoginUpsertAsync(string name, string? dingTalkId)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var trimmed = name.Trim();

            var user = await _context.Users
                .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
                .ThenInclude(r => r!.RolePermissions).ThenInclude(rp => rp.Permission)
                .FirstOrDefaultAsync(u => u.Name == trimmed);

            if (user == null)
            {
                user = new User
                {
                    Id = Guid.NewGuid(),
                    Name = trimmed,
                    LastLoginAt = DateTime.UtcNow,
                    Status = UserStatus.Active,
                    CreatedAt = DateTime.UtcNow
                };
                _context.Users.Add(user);

                var auth = _authOptions.Value;
                var isBootstrapAdmin = auth.BootstrapAdminNames.Any(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase));
                var roleName = isBootstrapAdmin ? BuiltInRoles.Admin
                    : (string.IsNullOrWhiteSpace(auth.DefaultRoleForNewUsers) ? BuiltInRoles.Operator : auth.DefaultRoleForNewUsers);

                var role = await _context.Roles.FirstOrDefaultAsync(r => r.Name == roleName)
                           ?? await _context.Roles.FirstOrDefaultAsync(r => r.Name == BuiltInRoles.Operator);
                if (role != null)
                {
                    _context.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, AssignedBy = "system" });
                }

                await _context.SaveChangesAsync();

                // 重新加载关联，保证权限解析稳定
                user = await _context.Users
                    .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
                    .ThenInclude(r => r!.RolePermissions).ThenInclude(rp => rp.Permission)
                    .FirstAsync(u => u.Id == user.Id);
            }
            else
            {
                if (user.Status == UserStatus.Left) return null;
                user.LastLoginAt = DateTime.UtcNow;

                // 已激活但已被踢出所有角色时，不授予任何权限（但允许登录，仅可见基本资料）。
                await _context.SaveChangesAsync();
            }

            var permissions = user.UserRoles
                .Where(ur => ur.Role != null)
                .SelectMany(ur => ur.Role!.RolePermissions)
                .Where(rp => rp.Permission != null)
                .Select(rp => rp.Permission!.Code)
                .Distinct()
                .ToList();

            return (user, permissions);
        }

        public async Task<IReadOnlyList<string>> GetPermissionsForUserAsync(Guid userId)
        {
            return await _context.UserRoles
                .Where(ur => ur.UserId == userId)
                .SelectMany(ur => ur.Role!.RolePermissions)
                .Select(rp => rp.Permission!.Code)
                .Distinct()
                .ToListAsync();
        }

        public async Task<IReadOnlyList<string>> GetUsersWithPermissionAsync(string permissionCode)
        {
            return await _context.UserRoles
                .Where(ur => ur.Role != null
                             && ur.User != null
                             && ur.User.Status == UserStatus.Active
                             && ur.Role.RolePermissions.Any(rp => rp.Permission != null && rp.Permission.Code == permissionCode))
                .Select(ur => ur.User!.Name)
                .Distinct()
                .ToListAsync();
        }

        public async Task<IReadOnlyList<string>> GetUsersInRoleAsync(string roleName)
        {
            return await _context.UserRoles
                .Where(ur => ur.Role != null
                             && ur.User != null
                             && ur.User.Status == UserStatus.Active
                             && ur.Role.Name == roleName)
                .Select(ur => ur.User!.Name)
                .Distinct()
                .ToListAsync();
        }
    }
}

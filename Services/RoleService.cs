using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuditIt.Api.Services
{
    public class RoleService : IRoleService
    {
        private readonly ApplicationDbContext _context;

        public RoleService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<RoleDto>> ListAsync()
        {
            var rows = await _context.Roles
                .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
                .OrderBy(r => r.Id)
                .ToListAsync();
            return rows.Select(ToDto).ToList();
        }

        public async Task<RoleDto?> GetAsync(int id)
        {
            var role = await _context.Roles
                .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
                .FirstOrDefaultAsync(r => r.Id == id);
            return role == null ? null : ToDto(role);
        }

        public async Task<(RoleDto? role, string? error)> CreateAsync(CreateRoleDto dto)
        {
            if (await _context.Roles.AnyAsync(r => r.Name == dto.Name))
                return (null, $"角色 {dto.Name} 已存在。");

            var role = new Role
            {
                Name = dto.Name,
                Description = dto.Description,
                IsBuiltIn = false
            };
            _context.Roles.Add(role);
            await _context.SaveChangesAsync();

            var error = await ApplyPermissionsAsync(role, dto.Permissions);
            if (error != null) return (null, error);

            await _context.SaveChangesAsync();
            return (await GetAsync(role.Id), null);
        }

        public async Task<(RoleDto? role, string? error)> UpdateAsync(int id, UpdateRoleDto dto)
        {
            var role = await _context.Roles
                .Include(r => r.RolePermissions)
                .FirstOrDefaultAsync(r => r.Id == id);
            if (role == null) return (null, "角色不存在。");

            if (dto.Description != null) role.Description = dto.Description;

            if (dto.Permissions != null)
            {
                if (role.IsBuiltIn) return (null, "内置角色的权限由系统维护，不允许修改。");
                _context.RolePermissions.RemoveRange(role.RolePermissions);
                await _context.SaveChangesAsync();
                var error = await ApplyPermissionsAsync(role, dto.Permissions);
                if (error != null) return (null, error);
            }

            await _context.SaveChangesAsync();
            return (await GetAsync(id), null);
        }

        public async Task<(bool ok, string? error)> DeleteAsync(int id)
        {
            var role = await _context.Roles
                .Include(r => r.UserRoles)
                .FirstOrDefaultAsync(r => r.Id == id);
            if (role == null) return (false, "角色不存在。");
            if (role.IsBuiltIn) return (false, "内置角色不可删除。");
            if (role.UserRoles.Count > 0) return (false, "仍有用户持有该角色，无法删除。");

            _context.Roles.Remove(role);
            await _context.SaveChangesAsync();
            return (true, null);
        }

        public async Task<IEnumerable<PermissionDto>> ListPermissionsAsync()
        {
            return await _context.Permissions
                .OrderBy(p => p.Category).ThenBy(p => p.Code)
                .Select(p => new PermissionDto { Code = p.Code, Category = p.Category, Description = p.Description })
                .ToListAsync();
        }

        private async Task<string?> ApplyPermissionsAsync(Role role, List<string> permissionCodes)
        {
            if (permissionCodes.Count == 0) return null;
            var perms = await _context.Permissions
                .Where(p => permissionCodes.Contains(p.Code))
                .ToListAsync();
            var missing = permissionCodes.Except(perms.Select(p => p.Code)).ToList();
            if (missing.Count > 0) return $"未知权限：{string.Join(", ", missing)}";

            foreach (var p in perms)
            {
                _context.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = p.Id });
            }
            return null;
        }

        private static RoleDto ToDto(Role r) => new()
        {
            Id = r.Id,
            Name = r.Name,
            Description = r.Description,
            IsBuiltIn = r.IsBuiltIn,
            Permissions = r.RolePermissions
                .Where(rp => rp.Permission != null)
                .Select(rp => rp.Permission!.Code)
                .OrderBy(c => c)
                .ToList()
        };
    }

    public class UserService : IUserService
    {
        private readonly ApplicationDbContext _context;
        private readonly IDingTalkService _dingTalkService;
        private readonly IOptions<AuthOptions> _authOptions;

        public UserService(
            ApplicationDbContext context,
            IDingTalkService dingTalkService,
            IOptions<AuthOptions> authOptions)
        {
            _context = context;
            _dingTalkService = dingTalkService;
            _authOptions = authOptions;
        }

        public async Task<IEnumerable<UserDto>> SearchAsync(UserQueryParameters query)
        {
            var q = _context.Users
                .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
                .ThenInclude(r => r!.RolePermissions).ThenInclude(rp => rp.Permission)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(query.Keyword))
            {
                var k = query.Keyword.Trim();
                q = q.Where(u => u.Name.Contains(k)
                    || (u.LastDingTalkId ?? "").Contains(k)
                    || (u.DingTalkUserId ?? "").Contains(k)
                    || (u.Mobile ?? "").Contains(k));
            }
            if (query.Status.HasValue) q = q.Where(u => u.Status == query.Status.Value);
            if (!string.IsNullOrWhiteSpace(query.Role))
            {
                var role = query.Role.Trim();
                q = q.Where(u => u.UserRoles.Any(ur => ur.Role != null && ur.Role.Name == role));
            }

            var limit = Math.Clamp(query.Limit, 1, 500);
            var rows = await q.OrderByDescending(u => u.LastLoginAt).Take(limit).ToListAsync();
            return rows.Select(ToDto).ToList();
        }

        public async Task<UserDto?> GetAsync(Guid id)
        {
            var user = await _context.Users
                .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
                .ThenInclude(r => r!.RolePermissions).ThenInclude(rp => rp.Permission)
                .FirstOrDefaultAsync(u => u.Id == id);
            return user == null ? null : ToDto(user);
        }

        public async Task<UserDto?> UpdateStatusAsync(Guid id, UpdateUserStatusDto dto)
        {
            var user = await _context.Users
                .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
                .ThenInclude(r => r!.RolePermissions).ThenInclude(rp => rp.Permission)
                .FirstOrDefaultAsync(u => u.Id == id);
            if (user == null) return null;

            user.Status = dto.Status;
            if (dto.Notes != null) user.Notes = dto.Notes;
            await _context.SaveChangesAsync();
            return ToDto(user);
        }

        public async Task<UserDto?> AssignRolesAsync(Guid id, AssignRolesDto dto, string? currentUser)
        {
            var user = await _context.Users
                .Include(u => u.UserRoles)
                .FirstOrDefaultAsync(u => u.Id == id);
            if (user == null) return null;

            _context.UserRoles.RemoveRange(user.UserRoles);
            await _context.SaveChangesAsync();

            var distinctRoleIds = dto.RoleIds.Distinct().ToList();
            var validRoleIds = await _context.Roles
                .Where(r => distinctRoleIds.Contains(r.Id))
                .Select(r => r.Id).ToListAsync();

            foreach (var rid in validRoleIds)
            {
                _context.UserRoles.Add(new UserRole
                {
                    UserId = user.Id,
                    RoleId = rid,
                    AssignedAt = DateTime.UtcNow,
                    AssignedBy = currentUser
                });
            }
            await _context.SaveChangesAsync();

            return await GetAsync(id);
        }

        public async Task<SyncDingTalkUsersResultDto> SyncDingTalkUsersAsync(
            SyncDingTalkUsersDto dto,
            string? currentUser,
            CancellationToken ct = default)
        {
            var result = new SyncDingTalkUsersResultDto();
            var directoryUsers = await _dingTalkService.GetDirectoryUsersAsync(ct);
            result.Pulled = directoryUsers.Count;

            var defaultRoleName = string.IsNullOrWhiteSpace(dto.DefaultRoleName)
                ? _authOptions.Value.DefaultRoleForNewUsers
                : dto.DefaultRoleName.Trim();
            if (string.IsNullOrWhiteSpace(defaultRoleName))
            {
                defaultRoleName = BuiltInRoles.Operator;
            }

            var defaultRole = await _context.Roles.FirstOrDefaultAsync(r => r.Name == defaultRoleName, ct)
                ?? await _context.Roles.FirstOrDefaultAsync(r => r.Name == BuiltInRoles.Operator, ct);

            var now = DateTime.UtcNow;
            var seenUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dingUser in directoryUsers)
            {
                if (string.IsNullOrWhiteSpace(dingUser.UserId) || string.IsNullOrWhiteSpace(dingUser.Name))
                {
                    result.Skipped++;
                    continue;
                }

                var userId = dingUser.UserId.Trim();
                var name = dingUser.Name.Trim();
                seenUserIds.Add(userId);

                var user = await _context.Users
                    .Include(u => u.UserRoles)
                    .FirstOrDefaultAsync(u =>
                        u.DingTalkUserId == userId
                        || u.LastDingTalkId == userId
                        || u.DingTalkId == userId,
                        ct);

                if (user == null)
                {
                    var byName = await _context.Users
                        .Include(u => u.UserRoles)
                        .FirstOrDefaultAsync(u => u.Name == name, ct);

                    if (byName != null
                        && !string.IsNullOrWhiteSpace(byName.DingTalkUserId)
                        && !string.Equals(byName.DingTalkUserId, userId, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Skipped++;
                        result.Messages.Add($"姓名重复，已跳过：{name} / {userId}");
                        continue;
                    }

                    user = byName;
                }

                if (user == null)
                {
                    user = new User
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        Status = dingUser.Active == false ? UserStatus.Left : UserStatus.Active,
                        CreatedAt = now
                    };
                    _context.Users.Add(user);
                    result.Created++;

                    if (defaultRole != null && user.Status == UserStatus.Active)
                    {
                        _context.UserRoles.Add(new UserRole
                        {
                            UserId = user.Id,
                            RoleId = defaultRole.Id,
                            AssignedAt = now,
                            AssignedBy = currentUser ?? "dingtalk-sync"
                        });
                    }
                }
                else
                {
                    result.Updated++;
                    user.Name = name;
                    if (dingUser.Active == false)
                    {
                        user.Status = UserStatus.Left;
                    }
                }

                user.DingTalkId = userId;
                user.LastDingTalkId = userId;
                user.DingTalkUserId = userId;
                user.DingTalkUnionId = dingUser.UnionId;
                user.Mobile = dingUser.Mobile;
                user.JobNumber = dingUser.JobNumber;
                user.JobTitle = dingUser.Title;
                user.LastDingTalkSyncAt = now;
            }

            if (dto.DeactivateMissing)
            {
                var localDingUsers = await _context.Users
                    .Where(u => u.Status == UserStatus.Active && u.DingTalkUserId != null)
                    .ToListAsync(ct);

                foreach (var user in localDingUsers.Where(u => !seenUserIds.Contains(u.DingTalkUserId!)))
                {
                    user.Status = UserStatus.Left;
                    result.Deactivated++;
                }
            }

            await _context.SaveChangesAsync(ct);
            return result;
        }

        internal static UserDto ToDto(User u)
        {
            var perms = u.UserRoles
                .Where(ur => ur.Role != null)
                .SelectMany(ur => ur.Role!.RolePermissions)
                .Where(rp => rp.Permission != null)
                .Select(rp => rp.Permission!.Code)
                .Distinct()
                .OrderBy(c => c)
                .ToList();

            return new UserDto
            {
                Id = u.Id,
                Name = u.Name,
                Status = u.Status,
                LastDingTalkId = u.LastDingTalkId ?? u.DingTalkId,
                DingTalkUserId = u.DingTalkUserId,
                Mobile = u.Mobile,
                JobNumber = u.JobNumber,
                JobTitle = u.JobTitle,
                LastDingTalkSyncAt = u.LastDingTalkSyncAt,
                LastLoginAt = u.LastLoginAt,
                Notes = u.Notes,
                CreatedAt = u.CreatedAt,
                Roles = u.UserRoles.Where(ur => ur.Role != null).Select(ur => ur.Role!.Name).OrderBy(n => n).ToList(),
                Permissions = perms
            };
        }
    }
}

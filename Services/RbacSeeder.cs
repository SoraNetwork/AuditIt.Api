using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AuditIt.Api.Services
{
    // 启动时保证 Permission 表与内置角色处于预期状态；新增权限码不会丢失，已停用的内置角色重新激活。
    public static class RbacSeeder
    {
        public static async Task SeedAsync(ApplicationDbContext db)
        {
            await SyncPermissionsAsync(db);
            await SyncBuiltInRolesAsync(db);
        }

        private static async Task SyncPermissionsAsync(ApplicationDbContext db)
        {
            var existing = await db.Permissions.ToDictionaryAsync(p => p.Code);
            foreach (var (code, category, description) in PermissionCodes.Catalog)
            {
                if (!existing.TryGetValue(code, out var perm))
                {
                    db.Permissions.Add(new Permission { Code = code, Category = category, Description = description });
                }
                else if (perm.Category != category || perm.Description != description)
                {
                    perm.Category = category;
                    perm.Description = description;
                }
            }
            await db.SaveChangesAsync();
        }

        private static async Task SyncBuiltInRolesAsync(ApplicationDbContext db)
        {
            var allPermissions = await db.Permissions.ToDictionaryAsync(p => p.Code, p => p.Id);

            foreach (var roleName in new[] { BuiltInRoles.Admin, BuiltInRoles.Manager, BuiltInRoles.Operator, BuiltInRoles.Viewer })
            {
                var role = await db.Roles
                    .Include(r => r.RolePermissions)
                    .FirstOrDefaultAsync(r => r.Name == roleName);

                if (role == null)
                {
                    role = new Role
                    {
                        Name = roleName,
                        Description = BuiltInRoles.Descriptions.TryGetValue(roleName, out var d) ? d : null,
                        IsBuiltIn = true
                    };
                    db.Roles.Add(role);
                    await db.SaveChangesAsync();
                }
                else
                {
                    role.IsBuiltIn = true;
                    if (role.Description == null && BuiltInRoles.Descriptions.TryGetValue(roleName, out var d))
                        role.Description = d;
                }

                var desired = BuiltInRoles.PermissionsFor(roleName).ToHashSet();
                var current = role.RolePermissions
                    .Select(rp => allPermissions.FirstOrDefault(kv => kv.Value == rp.PermissionId).Key)
                    .Where(c => !string.IsNullOrEmpty(c))
                    .ToHashSet();

                // 添加缺失的
                foreach (var code in desired.Except(current))
                {
                    if (allPermissions.TryGetValue(code, out var pid))
                        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = pid });
                }
                // 移除不应存在的（仅对内置角色强校正）
                foreach (var code in current.Except(desired))
                {
                    if (allPermissions.TryGetValue(code, out var pid))
                    {
                        var rp = role.RolePermissions.FirstOrDefault(x => x.PermissionId == pid);
                        if (rp != null) db.RolePermissions.Remove(rp);
                    }
                }

                await db.SaveChangesAsync();
            }
        }
    }
}

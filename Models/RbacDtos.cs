using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class UserDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public UserStatus Status { get; set; }
        public string? LastDingTalkId { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<string> Roles { get; set; } = new();
        public List<string> Permissions { get; set; } = new();
    }

    public class UserQueryParameters
    {
        public string? Keyword { get; set; }
        public UserStatus? Status { get; set; }
        public string? Role { get; set; }
        public int Limit { get; set; } = 100;
    }

    public class UpdateUserStatusDto
    {
        [Required]
        public UserStatus Status { get; set; }
        [StringLength(200)]
        public string? Notes { get; set; }
    }

    public class AssignRolesDto
    {
        [Required]
        public List<int> RoleIds { get; set; } = new();
    }

    public class RoleDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsBuiltIn { get; set; }
        public List<string> Permissions { get; set; } = new();
    }

    public class CreateRoleDto
    {
        [Required]
        [StringLength(50)]
        public string Name { get; set; } = string.Empty;

        [StringLength(200)]
        public string? Description { get; set; }

        public List<string> Permissions { get; set; } = new();
    }

    public class UpdateRoleDto
    {
        [StringLength(200)]
        public string? Description { get; set; }

        public List<string>? Permissions { get; set; }
    }

    public class PermissionDto
    {
        public string Code { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    public class AuthOptions
    {
        public List<string> BootstrapAdminNames { get; set; } = new();
        public string DefaultRoleForNewUsers { get; set; } = BuiltInRoles.Operator;
    }
}

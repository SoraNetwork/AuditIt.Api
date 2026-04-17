using System.ComponentModel.DataAnnotations.Schema;

namespace AuditIt.Api.Models
{
    public class RolePermission
    {
        public int RoleId { get; set; }
        [ForeignKey("RoleId")]
        public virtual Role? Role { get; set; }

        public int PermissionId { get; set; }
        [ForeignKey("PermissionId")]
        public virtual Permission? Permission { get; set; }
    }

    public class UserRole
    {
        public Guid UserId { get; set; }
        [ForeignKey("UserId")]
        public virtual User? User { get; set; }

        public int RoleId { get; set; }
        [ForeignKey("RoleId")]
        public virtual Role? Role { get; set; }

        public DateTime AssignedAt { get; set; } = DateTime.UtcNow;

        [System.ComponentModel.DataAnnotations.StringLength(100)]
        public string? AssignedBy { get; set; }
    }
}

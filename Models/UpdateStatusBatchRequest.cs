using System;
using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class UpdateStatusBatchRequest
    {
        [Required]
        public Guid[] ItemIds { get; set; } = Array.Empty<Guid>();

        [Required]
        public ItemStatus Status { get; set; }
    }
}

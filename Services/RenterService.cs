using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AuditIt.Api.Services
{
    public class RenterService : IRenterService
    {
        private readonly ApplicationDbContext _context;
        private readonly IIdentityService _identityService;
        private readonly IEnumerable<INotificationChannel> _notificationChannels;

        public RenterService(
            ApplicationDbContext context,
            IIdentityService identityService,
            IEnumerable<INotificationChannel> notificationChannels)
        {
            _context = context;
            _identityService = identityService;
            _notificationChannels = notificationChannels;
        }

        public async Task<IEnumerable<RenterDto>> SearchAsync(string? keyword, int limit)
        {
            var q = _context.Renters.AsQueryable();
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                var k = keyword.Trim();
                q = q.Where(r =>
                    r.Name.Contains(k) ||
                    (r.Phone != null && r.Phone.Contains(k)) ||
                    (r.IdCardNo != null && r.IdCardNo.Contains(k)));
            }

            return await q.OrderByDescending(r => r.LastUpdated)
                .Take(Math.Clamp(limit, 1, 500))
                .Select(r => ToDto(r))
                .ToListAsync();
        }

        public async Task<RenterDto?> GetByIdAsync(Guid id)
        {
            var r = await _context.Renters.FindAsync(id);
            return r == null ? null : ToDto(r);
        }

        public async Task<RenterDto> CreateAsync(CreateRenterDto dto, string? currentUser)
        {
            var renter = new Renter
            {
                Id = Guid.NewGuid(),
                Name = dto.Name,
                Phone = Normalize(dto.Phone),
                IdCardNo = dto.IdCardNo,
                XianyuId = dto.XianyuId,
                TaobaoId = dto.TaobaoId,
                XiaohongshuId = dto.XiaohongshuId,
                DefaultAddress = dto.DefaultAddress,
                Notes = dto.Notes,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = currentUser,
                LastUpdated = DateTime.UtcNow
            };
            _context.Renters.Add(renter);
            await _context.SaveChangesAsync();
            await NotifyAdminsAsync(renter, "created", currentUser);
            return ToDto(renter);
        }

        public async Task<RenterDto?> UpdateAsync(Guid id, UpdateRenterDto dto, string? currentUser)
        {
            var renter = await _context.Renters.FindAsync(id);
            if (renter == null) return null;

            var changes = BuildChangeSummary(renter, dto);

            renter.Name = dto.Name;
            renter.Phone = Normalize(dto.Phone);
            renter.IdCardNo = dto.IdCardNo;
            renter.XianyuId = dto.XianyuId;
            renter.TaobaoId = dto.TaobaoId;
            renter.XiaohongshuId = dto.XiaohongshuId;
            renter.DefaultAddress = dto.DefaultAddress;
            renter.Notes = dto.Notes;
            renter.LastUpdated = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            await NotifyAdminsAsync(renter, "updated", currentUser, changes);
            return ToDto(renter);
        }

        public async Task<bool> DeleteAsync(Guid id)
        {
            var renter = await _context.Renters.FindAsync(id);
            if (renter == null) return false;

            var hasRentals = await _context.Rentals.AnyAsync(r => r.RenterId == id);
            if (hasRentals) return false;

            _context.Renters.Remove(renter);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<Renter> ResolveOrUpsertAsync(RenterInlineDto inline, string? currentUser)
        {
            if (inline.RenterId.HasValue)
            {
                var existing = await _context.Renters.FindAsync(inline.RenterId.Value);
                if (existing != null)
                {
                    ApplyInlineUpdates(existing, inline);
                    existing.LastUpdated = DateTime.UtcNow;
                    return existing;
                }
            }

            var phone = Normalize(inline.Phone);
            if (!string.IsNullOrEmpty(phone))
            {
                var byPhone = await _context.Renters.FirstOrDefaultAsync(r => r.Phone == phone);
                if (byPhone != null)
                {
                    ApplyInlineUpdates(byPhone, inline);
                    byPhone.LastUpdated = DateTime.UtcNow;
                    return byPhone;
                }
            }

            var created = new Renter
            {
                Id = Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(inline.Name) ? "匿名租客" : inline.Name!,
                Phone = phone,
                IdCardNo = inline.IdCardNo,
                XianyuId = inline.XianyuId,
                TaobaoId = inline.TaobaoId,
                XiaohongshuId = inline.XiaohongshuId,
                DefaultAddress = inline.DefaultAddress,
                Notes = inline.Notes,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = currentUser,
                LastUpdated = DateTime.UtcNow
            };
            _context.Renters.Add(created);
            return created;
        }

        private static void ApplyInlineUpdates(Renter target, RenterInlineDto inline)
        {
            if (!string.IsNullOrWhiteSpace(inline.Name)) target.Name = inline.Name!;
            if (!string.IsNullOrWhiteSpace(inline.IdCardNo)) target.IdCardNo = inline.IdCardNo;
            if (!string.IsNullOrWhiteSpace(inline.XianyuId)) target.XianyuId = inline.XianyuId;
            if (!string.IsNullOrWhiteSpace(inline.TaobaoId)) target.TaobaoId = inline.TaobaoId;
            if (!string.IsNullOrWhiteSpace(inline.XiaohongshuId)) target.XiaohongshuId = inline.XiaohongshuId;
            if (!string.IsNullOrWhiteSpace(inline.DefaultAddress)) target.DefaultAddress = inline.DefaultAddress;
            if (!string.IsNullOrWhiteSpace(inline.Notes)) target.Notes = inline.Notes;
        }

        private static string? Normalize(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return null;
            return phone.Trim();
        }

        private async Task NotifyAdminsAsync(Renter renter, string action, string? currentUser, string? extra = null)
        {
            try
            {
                var admins = await _identityService.GetUsersInRoleAsync(BuiltInRoles.Admin);
                var targets = admins
                    .Where(user => !string.IsNullOrWhiteSpace(user))
                    .Select(user => user.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (targets.Count == 0)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                var title = $"Renter {action}: {renter.Name}";
                var message = BuildRenterNotificationMessage(renter, action, currentUser, extra);
                var reminders = targets.Select(target => new Reminder
                {
                    Type = ReminderType.Manual,
                    Level = ReminderLevel.Info,
                    RelatedEntityType = "Renter",
                    RelatedEntityId = renter.Id.ToString(),
                    Title = title,
                    Message = message,
                    TargetUser = target,
                    DueAt = now,
                    CreatedAt = now
                }).ToList();

                _context.Reminders.AddRange(reminders);
                await _context.SaveChangesAsync();

                foreach (var reminder in reminders)
                {
                    foreach (var channel in _notificationChannels)
                    {
                        try
                        {
                            await channel.DeliverAsync(reminder, default);
                        }
                        catch
                        {
                            // Notification side-channel failures should not block renter edits.
                        }
                    }
                }
            }
            catch
            {
                // Notification failures should not block renter edits.
            }
        }

        private static string BuildRenterNotificationMessage(Renter renter, string action, string? currentUser, string? extra)
        {
            var lines = new List<string>
            {
                $"Action: renter {action}",
                $"Renter: {renter.Name}",
                $"Phone: {renter.Phone ?? "-"}"
            };

            if (!string.IsNullOrWhiteSpace(renter.XianyuId)) lines.Add($"Xianyu: {renter.XianyuId}");
            if (!string.IsNullOrWhiteSpace(renter.TaobaoId)) lines.Add($"Taobao: {renter.TaobaoId}");
            if (!string.IsNullOrWhiteSpace(renter.XiaohongshuId)) lines.Add($"Xiaohongshu: {renter.XiaohongshuId}");
            if (!string.IsNullOrWhiteSpace(currentUser)) lines.Add($"Operator: {currentUser}");
            if (!string.IsNullOrWhiteSpace(extra)) lines.Add($"Changes: {extra}");

            return string.Join("\n", lines);
        }

        private static string? BuildChangeSummary(Renter renter, UpdateRenterDto dto)
        {
            var changes = new List<string>();
            AddChange(changes, "Name", renter.Name, dto.Name);
            AddChange(changes, "Phone", renter.Phone, Normalize(dto.Phone));
            AddChange(changes, "IdCardNo", renter.IdCardNo, dto.IdCardNo);
            AddChange(changes, "XianyuId", renter.XianyuId, dto.XianyuId);
            AddChange(changes, "TaobaoId", renter.TaobaoId, dto.TaobaoId);
            AddChange(changes, "XiaohongshuId", renter.XiaohongshuId, dto.XiaohongshuId);
            AddChange(changes, "DefaultAddress", renter.DefaultAddress, dto.DefaultAddress);
            AddChange(changes, "Notes", renter.Notes, dto.Notes);
            return changes.Count == 0 ? null : string.Join("; ", changes);
        }

        private static void AddChange(List<string> changes, string field, string? before, string? after)
        {
            if (!string.Equals(before ?? string.Empty, after ?? string.Empty, StringComparison.Ordinal))
            {
                changes.Add($"{field}: {Truncate(before)} -> {Truncate(after)}");
            }
        }

        private static string Truncate(string? value)
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
            return normalized.Length <= 40 ? normalized : normalized[..40] + "...";
        }

        internal static RenterDto ToDto(Renter r) => new()
        {
            Id = r.Id,
            Name = r.Name,
            Phone = r.Phone,
            IdCardNo = r.IdCardNo,
            XianyuId = r.XianyuId,
            TaobaoId = r.TaobaoId,
            XiaohongshuId = r.XiaohongshuId,
            DefaultAddress = r.DefaultAddress,
            Notes = r.Notes,
            CreatedAt = r.CreatedAt,
            LastUpdated = r.LastUpdated
        };
    }
}

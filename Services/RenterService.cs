using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AuditIt.Api.Services
{
    public class RenterService : IRenterService
    {
        private readonly ApplicationDbContext _context;

        public RenterService(ApplicationDbContext context)
        {
            _context = context;
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
                    (r.XianyuId != null && r.XianyuId.Contains(k)) ||
                    (r.TaobaoId != null && r.TaobaoId.Contains(k)) ||
                    (r.XiaohongshuId != null && r.XiaohongshuId.Contains(k)));
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
            return ToDto(renter);
        }

        public async Task<RenterDto?> UpdateAsync(Guid id, UpdateRenterDto dto)
        {
            var renter = await _context.Renters.FindAsync(id);
            if (renter == null) return null;

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

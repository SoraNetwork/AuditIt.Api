using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AuditIt.Api.Services
{
    public class QuickRemarkService : IQuickRemarkService
    {
        private readonly ApplicationDbContext _context;

        public QuickRemarkService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<QuickRemarkDto>> GetAllQuickRemarksAsync()
        {
            var quickRemarks = await _context.QuickRemarks
                .OrderByDescending(qr => qr.CreatedAt)
                .Select(qr => new QuickRemarkDto
                {
                    Id = qr.Id,
                    Content = qr.Content,
                    CreatedAt = qr.CreatedAt
                })
                .ToListAsync();

            return quickRemarks;
        }

        public async Task<QuickRemarkDto> CreateQuickRemarkAsync(CreateQuickRemarkDto dto)
        {
            var quickRemark = new QuickRemark
            {
                Content = dto.Content,
                CreatedAt = DateTime.UtcNow
            };

            _context.QuickRemarks.Add(quickRemark);
            await _context.SaveChangesAsync();

            return new QuickRemarkDto
            {
                Id = quickRemark.Id,
                Content = quickRemark.Content,
                CreatedAt = quickRemark.CreatedAt
            };
        }

        public async Task<bool> DeleteQuickRemarkAsync(int id)
        {
            var quickRemark = await _context.QuickRemarks.FindAsync(id);
            if (quickRemark == null)
            {
                return false;
            }

            _context.QuickRemarks.Remove(quickRemark);
            await _context.SaveChangesAsync();

            return true;
        }
    }
}
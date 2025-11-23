using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    public interface IQuickRemarkService
    {
        Task<IEnumerable<QuickRemarkDto>> GetAllQuickRemarksAsync();
        Task<QuickRemarkDto> CreateQuickRemarkAsync(CreateQuickRemarkDto dto);
        Task<bool> DeleteQuickRemarkAsync(int id);
    }
}
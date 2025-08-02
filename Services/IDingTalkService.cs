using System.Threading.Tasks;
using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    public interface IDingTalkService
    {
        Task<string> GetAccessTokenAsync();
        Task<DingTalkUserInfo> GetLegacyUserInfoByCodeAsync(string code);
        Task<SnsUserInfo> GetSsoUserInfoByCodeAsync(string ssoCode);
    }
}
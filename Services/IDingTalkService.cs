using System.Threading.Tasks;
using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    public interface IDingTalkService
    {
        Task<string> GetAccessTokenAsync(); // This gets the app access token
        Task<DingTalkUserInfo> GetLegacyUserInfoByCodeAsync(string code); // For in-app免登
        Task<DingTalkContactUser> GetSsoUserInfoByCodeAsync(string ssoCode); // For web SSO
    }
}

using System.Threading.Tasks;
using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    public interface IDingTalkService
    {
        Task<string> GetAccessTokenAsync(); // This gets the app access token
        Task<DingTalkUserInfo> GetLegacyUserInfoByCodeAsync(string code); // For in-app免登
        Task<DingTalkContactUser> GetSsoUserInfoByCodeAsync(string ssoCode); // For web SSO
        Task<IReadOnlyList<DingTalkUserDetail>> GetDirectoryUsersAsync(CancellationToken ct = default);
        Task SendWorkNoticeAsync(IEnumerable<string> userIds, string content, CancellationToken ct = default);
        Task<bool> SendRobotMarkdownAsync(string title, string markdownText, string? msgUuid = null, CancellationToken ct = default);
    }
}

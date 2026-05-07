using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AuditIt.Api.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AuditIt.Api.Services
{
    public class DingTalkService : IDingTalkService
    {
        private readonly HttpClient _httpClient;
        private readonly DingTalkConfiguration _dingTalkConfig;
        private readonly IMemoryCache _cache;
        private const string AppAccessTokenCacheKey = "DingTalkAppAccessToken";
        private const long RootDepartmentId = 1;

        public DingTalkService(HttpClient httpClient, IOptions<DingTalkConfiguration> dingTalkConfigOptions, IMemoryCache cache)
        {
            _httpClient = httpClient;
            _dingTalkConfig = dingTalkConfigOptions.Value;
            _cache = cache;
        }

        // Gets the application access token, used for legacy in-app login
        public async Task<string> GetAccessTokenAsync()
        {
            if (_cache.TryGetValue(AppAccessTokenCacheKey, out string accessToken))
            {
                return accessToken;
            }

            var requestBody = new
            {
                appKey = _dingTalkConfig.AppKey,
                appSecret = _dingTalkConfig.AppSecret
            };

            var response = await _httpClient.PostAsJsonAsync("https://api.dingtalk.com/v1.0/oauth2/accessToken", requestBody);
            response.EnsureSuccessStatusCode();

            var tokenResponse = await response.Content.ReadFromJsonAsync<AccessTokenResponse>();
            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                throw new InvalidOperationException("无法从钉钉获取应用 Access Token。");
            }
            
            var cacheEntryOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromSeconds(tokenResponse.ExpireIn - 120));

            _cache.Set(AppAccessTokenCacheKey, tokenResponse.AccessToken, cacheEntryOptions);

            return tokenResponse.AccessToken;
        }

        // Legacy in-app免登login
        public async Task<DingTalkUserInfo> GetLegacyUserInfoByCodeAsync(string code)
        {
            var appAccessToken = await GetAccessTokenAsync();
            var requestBody = new { code };
            
            var response = await _httpClient.PostAsJsonAsync($"https://oapi.dingtalk.com/topapi/v2/user/getuserinfo?access_token={appAccessToken}", requestBody);
            response.EnsureSuccessStatusCode();

            var userResponse = await response.Content.ReadFromJsonAsync<DingTalkUserResponse>();
            if (userResponse == null || userResponse.ErrorCode != 0)
            {
                throw new InvalidOperationException($"获取钉钉用户信息失败：{userResponse?.ErrorMessage}");
            }

            return userResponse.Result;
        }

        // Web SSO login flow
        public async Task<DingTalkContactUser> GetSsoUserInfoByCodeAsync(string ssoCode)
        {
            // Step 1: Get user-specific access token using the SSO code
            var userAccessToken = await GetUserAccessTokenAsync(ssoCode);

            // Step 2: Get user's contact info using the user-specific access token
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.dingtalk.com/v1.0/contact/users/me");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("x-acs-dingtalk-access-token", userAccessToken);

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"获取用户个人信息失败: {response.StatusCode} - {errorContent}");
            }

            var userInfo = await response.Content.ReadFromJsonAsync<DingTalkContactUser>();
            if (userInfo == null)
            {
                throw new InvalidOperationException("无法解析用户个人信息。");
            }

            return userInfo;
        }

        private async Task<string> GetUserAccessTokenAsync(string ssoCode)
        {
            var requestUrl = "https://api.dingtalk.com/v1.0/oauth2/userAccessToken";
            var requestBody = new
            {
                clientId = _dingTalkConfig.AppKey,
                clientSecret = _dingTalkConfig.AppSecret,
                code = ssoCode,
                grantType = "authorization_code"
            };

            var response = await _httpClient.PostAsJsonAsync(requestUrl, requestBody);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"获取用户个人 Access Token 失败: {response.StatusCode} - {errorContent}");
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<UserAccessTokenResponse>();
            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                throw new InvalidOperationException("未能从响应中获取用户个人 Access Token。");
            }

            return tokenResponse.AccessToken;
        }

        public async Task<IReadOnlyList<DingTalkUserDetail>> GetDirectoryUsersAsync(CancellationToken ct = default)
        {
            var token = await GetAccessTokenAsync();
            var departmentIds = await GetAllDepartmentIdsAsync(token, ct);
            var userIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var deptId in departmentIds)
            {
                foreach (var userId in await GetDepartmentUserIdsAsync(token, deptId, ct))
                {
                    userIds.Add(userId);
                }
            }

            HashSet<string> dimissionUserIds;
            try
            {
                dimissionUserIds = await GetDimissionUserIdsAsync(token, ct);
            }
            catch (InvalidOperationException)
            {
                dimissionUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var userId in dimissionUserIds)
            {
                userIds.Add(userId);
            }

            var users = new List<DingTalkUserDetail>();
            foreach (var userId in userIds)
            {
                var isDimissionUser = dimissionUserIds.Contains(userId);
                DingTalkUserDetail? user;
                try
                {
                    user = await GetUserDetailAsync(token, userId, ct);
                }
                catch (InvalidOperationException) when (isDimissionUser)
                {
                    continue;
                }

                if (user != null && !string.IsNullOrWhiteSpace(user.Name))
                {
                    if (string.IsNullOrWhiteSpace(user.UserId))
                    {
                        user.UserId = userId;
                    }

                    if (isDimissionUser || dimissionUserIds.Contains(user.UserId))
                    {
                        user.Active = false;
                        user.Status = 2;
                    }

                    users.Add(user);
                }
            }

            return users
                .OrderBy(u => u.Name)
                .ThenBy(u => u.UserId)
                .ToList();
        }

        public async Task SendWorkNoticeAsync(IEnumerable<string> userIds, string content, CancellationToken ct = default)
        {
            if (!_dingTalkConfig.EnableWorkNotice || !_dingTalkConfig.AgentId.HasValue)
            {
                return;
            }

            var targets = userIds
                .Select(id => id?.Trim())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Chunk(100);

            var token = await GetAccessTokenAsync();
            foreach (var chunk in targets)
            {
                var requestBody = new
                {
                    agent_id = _dingTalkConfig.AgentId.Value,
                    userid_list = string.Join(",", chunk),
                    msg = new
                    {
                        msgtype = "text",
                        text = new
                        {
                            content
                        }
                    }
                };

                var response = await _httpClient.PostAsJsonAsync(
                    $"https://oapi.dingtalk.com/topapi/message/corpconversation/asyncsend_v2?access_token={Uri.EscapeDataString(token)}",
                    requestBody,
                    ct);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<DingTalkWorkNoticeResponse>(cancellationToken: ct);
                if (result == null || result.ErrorCode != 0)
                {
                    throw new InvalidOperationException($"发送钉钉工作通知失败：{result?.ErrorMessage ?? "未知错误"}");
                }
            }
        }

        public async Task<bool> SendRobotMarkdownAsync(string title, string markdownText, string? msgUuid = null, CancellationToken ct = default)
        {
            var webhookUrl = BuildRobotWebhookUrl();
            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                return false;
            }

            var requestBody = new
            {
                msgtype = "markdown",
                msgUuid,
                markdown = new
                {
                    title,
                    text = markdownText
                }
            };

            var response = await _httpClient.PostAsJsonAsync(webhookUrl, requestBody, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<DingTalkRobotSendResponse>(cancellationToken: ct);
            if (result == null || result.ErrorCode != 0)
            {
                throw new InvalidOperationException($"发送钉钉群机器人消息失败：{result?.ErrorMessage ?? "未知错误"}");
            }

            return true;
        }

        private string? BuildRobotWebhookUrl()
        {
            var webhookUrl = _dingTalkConfig.RobotWebhookUrl?.Trim();
            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                return null;
            }

            var secret = _dingTalkConfig.RobotSecret?.Trim();
            if (string.IsNullOrWhiteSpace(secret))
            {
                return webhookUrl;
            }

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var signSource = $"{timestamp}\n{secret}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var sign = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signSource)));
            var separator = webhookUrl.Contains('?') ? '&' : '?';
            return $"{webhookUrl}{separator}timestamp={Uri.EscapeDataString(timestamp)}&sign={Uri.EscapeDataString(sign)}";
        }

        private async Task<List<long>> GetAllDepartmentIdsAsync(string token, CancellationToken ct)
        {
            var result = new List<long> { RootDepartmentId };
            var queue = new Queue<long>();
            queue.Enqueue(RootDepartmentId);

            while (queue.Count > 0)
            {
                var deptId = queue.Dequeue();
                var children = await GetSubDepartmentsAsync(token, deptId, ct);
                foreach (var child in children)
                {
                    if (result.Contains(child.DeptId))
                    {
                        continue;
                    }

                    result.Add(child.DeptId);
                    queue.Enqueue(child.DeptId);
                }
            }

            return result;
        }

        private async Task<IReadOnlyList<DingTalkDepartment>> GetSubDepartmentsAsync(string token, long deptId, CancellationToken ct)
        {
            var body = new
            {
                dept_id = deptId,
                language = "zh_CN"
            };

            var response = await _httpClient.PostAsJsonAsync(
                $"https://oapi.dingtalk.com/topapi/v2/department/listsub?access_token={Uri.EscapeDataString(token)}",
                body,
                ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<DingTalkApiResponse<List<DingTalkDepartment>>>(cancellationToken: ct);
            EnsureDingTalkSuccess(result?.ErrorCode ?? -1, result?.ErrorMessage, "获取钉钉部门列表失败");
            return result?.Result ?? new List<DingTalkDepartment>();
        }

        private async Task<IReadOnlyList<string>> GetDepartmentUserIdsAsync(string token, long deptId, CancellationToken ct)
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"https://oapi.dingtalk.com/topapi/user/listid?access_token={Uri.EscapeDataString(token)}",
                new { dept_id = deptId },
                ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<DingTalkApiResponse<DingTalkDepartmentUserListResult>>(cancellationToken: ct);
            EnsureDingTalkSuccess(result?.ErrorCode ?? -1, result?.ErrorMessage, "获取钉钉部门成员失败");
            return result?.Result?.UserIds ?? new List<string>();
        }

        private async Task<DingTalkUserDetail?> GetUserDetailAsync(string token, string userId, CancellationToken ct)
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"https://oapi.dingtalk.com/topapi/v2/user/get?access_token={Uri.EscapeDataString(token)}",
                new
                {
                    userid = userId,
                    language = "zh_CN"
                },
                ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<DingTalkApiResponse<DingTalkUserDetail>>(cancellationToken: ct);
            EnsureDingTalkSuccess(result?.ErrorCode ?? -1, result?.ErrorMessage, $"获取钉钉用户 {userId} 详情失败");
            return result?.Result;
        }

        private async Task<HashSet<string>> GetDimissionUserIdsAsync(string token, CancellationToken ct)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long offset = 0;
            const int size = 50;

            while (true)
            {
                var response = await _httpClient.PostAsJsonAsync(
                    $"https://oapi.dingtalk.com/topapi/smartwork/hrm/employee/querydimission?access_token={Uri.EscapeDataString(token)}",
                    new
                    {
                        offset,
                        size
                    },
                    ct);
                response.EnsureSuccessStatusCode();

                var body = await response.Content.ReadFromJsonAsync<DingTalkApiResponse<DingTalkDimissionUserListResult>>(cancellationToken: ct);
                EnsureDingTalkSuccess(body?.ErrorCode ?? -1, body?.ErrorMessage, "获取钉钉离职员工列表失败");

                var page = body?.Result;
                if (page == null || page.UserIds.Count == 0)
                {
                    break;
                }

                foreach (var userId in page.UserIds.Where(id => !string.IsNullOrWhiteSpace(id)))
                {
                    result.Add(userId.Trim());
                }

                if (!page.NextCursor.HasValue || page.NextCursor.Value <= offset)
                {
                    break;
                }

                offset = page.NextCursor.Value;
            }

            return result;
        }

        private static void EnsureDingTalkSuccess(int errorCode, string? errorMessage, string prefix)
        {
            if (errorCode != 0)
            {
                throw new InvalidOperationException($"{prefix}：{errorMessage ?? errorCode.ToString()}");
            }
        }

        private sealed class DingTalkRobotSendResponse
        {
            [JsonPropertyName("errcode")]
            public int ErrorCode { get; set; }

            [JsonPropertyName("errmsg")]
            public string? ErrorMessage { get; set; }
        }
    }
}

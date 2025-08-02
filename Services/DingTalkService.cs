using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
        private const string AccessTokenCacheKey = "DingTalkAccessToken";

        public DingTalkService(HttpClient httpClient, IOptions<DingTalkConfiguration> dingTalkConfigOptions, IMemoryCache cache)
        {
            _httpClient = httpClient;
            _dingTalkConfig = dingTalkConfigOptions.Value;
            _cache = cache;
        }

        public async Task<string> GetAccessTokenAsync()
        {
            if (_cache.TryGetValue(AccessTokenCacheKey, out string accessToken))
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
                throw new InvalidOperationException("无法从钉钉获取 Access Token。");
            }
            
            var cacheEntryOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromSeconds(tokenResponse.ExpireIn - 120));

            _cache.Set(AccessTokenCacheKey, tokenResponse.AccessToken, cacheEntryOptions);

            return tokenResponse.AccessToken;
        }

        public async Task<DingTalkUserInfo> GetLegacyUserInfoByCodeAsync(string code)
        {
            var accessToken = await GetAccessTokenAsync();
            var requestBody = new { code };
            
            var response = await _httpClient.PostAsJsonAsync($"https://oapi.dingtalk.com/topapi/v2/user/getuserinfo?access_token={accessToken}", requestBody);
            response.EnsureSuccessStatusCode();

            var userResponse = await response.Content.ReadFromJsonAsync<DingTalkUserResponse>();
            if (userResponse == null || userResponse.ErrorCode != 0)
            {
                throw new InvalidOperationException($"获取钉钉用户信息失败：{userResponse?.ErrorMessage}");
            }

            return userResponse.Result;
        }

        public async Task<DingTalkSsoUserInfo> GetSsoUserInfoByCodeAsync(string ssoCode)
        {
            var timestamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds().ToString();
            var signature = GenerateSignature(timestamp, _dingTalkConfig.AppSecret);

            var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.dingtalk.com/v1.0/contact/users/me");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("x-acs-dingtalk-app-key", _dingTalkConfig.AppKey);
            request.Headers.Add("x-acs-dingtalk-timestamp", timestamp);
            request.Headers.Add("x-acs-dingtalk-signature", signature);
            // 根据新版文档，SSO code 作为 unionId 的临时凭证，通过 x-acs-dingtalk-access-token 头发送
            request.Headers.Add("x-acs-dingtalk-access-token", ssoCode);


            var response = await _httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"获取钉钉 SSO 用户信息失败: {response.StatusCode} - {errorContent}");
            }

            var userInfo = await response.Content.ReadFromJsonAsync<DingTalkSsoUserInfo>();
            if (userInfo == null)
            {
                throw new InvalidOperationException("无法解析钉钉 SSO 用户信息。");
            }

            // 新的 SSO API 返回的用户信息结构可能不同，需要适配
            // 这里假设 DingTalkSsoUserInfo 已经适配了 /v1.0/contact/users/me 的返回结构
            // 主要字段：nick, unionId, openId, mobile
            // 我们需要将其映射到我们自己的 User 模型
            return userInfo;
        }

        private string GenerateSignature(string timestamp, string appSecret)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp));
            return Convert.ToBase64String(hash);
        }
    }
}

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web;
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

        public async Task<SnsUserInfo> GetSsoUserInfoByCodeAsync(string ssoCode)
        {
            var timestamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds().ToString();
            var signature = GenerateSignature(timestamp, _dingTalkConfig.AppSecret);
            
            var requestUrl = $"https://oapi.dingtalk.com/sns/getuserinfo_bycode?accessKey={_dingTalkConfig.AppKey}&timestamp={timestamp}&signature={HttpUtility.UrlEncode(signature)}";

            var requestBody = new { tmp_auth_code = ssoCode };

            var response = await _httpClient.PostAsJsonAsync(requestUrl, requestBody);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"获取钉钉 SSO 用户信息失败: {response.StatusCode} - {errorContent}");
            }

            var snsResponse = await response.Content.ReadFromJsonAsync<DingTalkSnsResponse>();
            if (snsResponse == null || snsResponse.ErrorCode != 0)
            {
                 throw new InvalidOperationException($"获取钉钉 SSO 用户信息失败: {snsResponse?.ErrorMessage}");
            }

            return snsResponse.UserInfo;
        }

        private string GenerateSignature(string timestamp, string appSecret)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp));
            return Convert.ToBase64String(hash);
        }
    }
}
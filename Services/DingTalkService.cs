using System;
using System.Net.Http;
using System.Net.Http.Json;
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
            // 尝试从缓存中获取 accessToken
            if (_cache.TryGetValue(AccessTokenCacheKey, out string accessToken))
            {
                return accessToken;
            }

            var requestBody = new
            {
                appKey = _dingTalkConfig.AppKey,
                appSecret = _dingTalkConfig.AppSecret
            };

            // 调用钉钉 API 获取新的 accessToken
            var response = await _httpClient.PostAsJsonAsync("https://api.dingtalk.com/v1.0/oauth2/accessToken", requestBody);

            response.EnsureSuccessStatusCode();

            var tokenResponse = await response.Content.ReadFromJsonAsync<AccessTokenResponse>();

            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                throw new InvalidOperationException("无法从钉钉获取 Access Token。");
            }
            
            // 将 Token 存入缓存，并设置一个比实际过期时间稍短的缓存时间以确保安全
            var cacheEntryOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromSeconds(tokenResponse.ExpireIn - 120)); // 2分钟缓冲期

            _cache.Set(AccessTokenCacheKey, tokenResponse.AccessToken, cacheEntryOptions);

            return tokenResponse.AccessToken;
        }

        public async Task<DingTalkUserInfo> GetUserInfoByCodeAsync(string code)
        {
            var accessToken = await GetAccessTokenAsync();

            var requestBody = new
            {
                code = code
            };
            
            var response = await _httpClient.PostAsJsonAsync($"https://oapi.dingtalk.com/topapi/v2/user/getuserinfo?access_token={accessToken}", requestBody);
            response.EnsureSuccessStatusCode();

            var userResponse = await response.Content.ReadFromJsonAsync<DingTalkUserResponse>();
            
            if (userResponse.ErrorCode != 0)
            {
                throw new InvalidOperationException($"获取钉钉用户信息失败：{userResponse.ErrorMessage}");
            }

            return userResponse.Result;
        }
    }
}

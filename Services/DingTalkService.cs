using System;
using System.Net.Http;
using System.Net.Http.Headers;
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
        private const string AppAccessTokenCacheKey = "DingTalkAppAccessToken";

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
    }
}

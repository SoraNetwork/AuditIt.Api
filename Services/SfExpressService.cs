using System.Text.Json;
using System.Net.Http.Json;
using AuditIt.Api.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AuditIt.Api.Services
{
    public class SfExpressService : ISfExpressService
    {
        private const string TokenCacheKey = "SfExpress:OAuthToken";
        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly IOptionsMonitor<SfExpressOptions> _options;
        private readonly ILogger<SfExpressService> _logger;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public SfExpressService(
            HttpClient httpClient,
            IMemoryCache cache,
            IOptionsMonitor<SfExpressOptions> options,
            ILogger<SfExpressService> logger)
        {
            _httpClient = httpClient;
            _cache = cache;
            _options = options;
            _logger = logger;
        }

        public async Task<IReadOnlyList<SfRouteQueryResult>> QueryRoutesAsync(
            IReadOnlyList<SfRouteQueryItem> items,
            bool forceRefresh,
            CancellationToken ct = default)
        {
            var queryable = items
                .Where(i => !string.IsNullOrWhiteSpace(i.TrackingNumber) && !string.IsNullOrWhiteSpace(i.CheckPhoneNo))
                .Select(i => new SfRouteQueryItem
                {
                    ShipmentId = i.ShipmentId,
                    TrackingNumber = i.TrackingNumber.Trim().ToUpperInvariant(),
                    CheckPhoneNo = i.CheckPhoneNo.Trim()
                })
                .DistinctBy(i => $"{i.TrackingNumber}:{i.CheckPhoneNo}")
                .ToList();

            if (queryable.Count == 0)
            {
                return Array.Empty<SfRouteQueryResult>();
            }

            var results = new List<SfRouteQueryResult>();
            var toQuery = new List<SfRouteQueryItem>();
            foreach (var item in queryable)
            {
                var cacheKey = BuildRouteCacheKey(item.TrackingNumber, item.CheckPhoneNo);
                if (!forceRefresh && _cache.TryGetValue<SfRouteQueryResult>(cacheKey, out var cached) && cached != null)
                {
                    results.Add(CloneForShipment(cached, item, fromCache: true));
                    continue;
                }

                toQuery.Add(item);
            }

            foreach (var batch in toQuery.Chunk(10))
            {
                var fresh = await QueryBatchAsync(batch, ct);
                foreach (var result in fresh)
                {
                    var source = batch.FirstOrDefault(i =>
                        string.Equals(i.TrackingNumber, result.TrackingNumber, StringComparison.OrdinalIgnoreCase));
                    if (source == null)
                    {
                        continue;
                    }

                    var cacheKey = BuildRouteCacheKey(source.TrackingNumber, source.CheckPhoneNo);
                    _cache.Set(cacheKey, result, TimeSpan.FromMinutes(Math.Max(1, _options.CurrentValue.CacheMinutes)));
                    results.Add(result);
                }
            }

            return queryable
                .Select(item => results.FirstOrDefault(r => r.ShipmentId == item.ShipmentId)
                    ?? new SfRouteQueryResult
                    {
                        ShipmentId = item.ShipmentId,
                        TrackingNumber = item.TrackingNumber,
                        CheckPhoneNo = item.CheckPhoneNo,
                        QueriedAt = DateTime.UtcNow,
                        Error = "未返回顺丰路由结果。"
                    })
                .ToList();
        }

        private async Task<List<SfRouteQueryResult>> QueryBatchAsync(IReadOnlyList<SfRouteQueryItem> batch, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var options = _options.CurrentValue;
            if (string.IsNullOrWhiteSpace(options.PartnerId) || string.IsNullOrWhiteSpace(options.Secret))
            {
                return batch.Select(i => BuildError(i, now, "顺丰 PartnerId/Secret 未配置。")).ToList();
            }

            try
            {
                var token = await GetAccessTokenAsync(ct);
                var msgData = JsonSerializer.Serialize(new
                {
                    language = string.IsNullOrWhiteSpace(options.Language) ? "zh-CN" : options.Language,
                    trackingType = 1,
                    trackingNumber = batch.Select(i => i.TrackingNumber).ToArray(),
                    methodType = 1,
                    checkPhoneNo = string.Join(",", batch.Select(i => i.CheckPhoneNo))
                }, JsonOptions);

                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["partnerID"] = options.PartnerId,
                    ["requestID"] = Guid.NewGuid().ToString("N"),
                    ["serviceCode"] = SfExpressConstants.RouteServiceCode,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                    ["accessToken"] = token,
                    ["msgData"] = msgData
                });
                content.Headers.ContentType!.CharSet = "UTF-8";

                using var response = await _httpClient.PostAsync(options.ServiceUrl, content, ct);
                var envelope = await response.Content.ReadFromJsonAsync<SfApiEnvelope>(JsonOptions, ct);
                if (!response.IsSuccessStatusCode || envelope == null)
                {
                    return batch.Select(i => BuildError(i, now, $"顺丰路由查询失败：HTTP {(int)response.StatusCode}")).ToList();
                }

                if (!string.Equals(envelope.ApiResultCode, "A1000", StringComparison.OrdinalIgnoreCase))
                {
                    var error = $"顺丰平台返回 {envelope.ApiResultCode}: {envelope.ApiErrorMsg}";
                    return batch.Select(i => BuildError(i, now, error)).ToList();
                }

                var apiResult = string.IsNullOrWhiteSpace(envelope.ApiResultData)
                    ? null
                    : JsonSerializer.Deserialize<SfRouteApiResult>(envelope.ApiResultData, JsonOptions);
                if (apiResult == null || !apiResult.Success)
                {
                    var error = $"顺丰业务返回 {apiResult?.ErrorCode}: {apiResult?.ErrorMsg}";
                    return batch.Select(i => BuildError(i, now, error)).ToList();
                }

                return batch.Select(item =>
                {
                    var routeResp = apiResult.MsgData?.RouteResps.FirstOrDefault(r =>
                        string.Equals(r.MailNo, item.TrackingNumber, StringComparison.OrdinalIgnoreCase));
                    var routes = routeResp?.Routes ?? new List<SfRouteNodeDto>();
                    var deliveredAt = ResolveDeliveredAt(routes);
                    var exception = ResolveException(routes, deliveredAt.HasValue);
                    return new SfRouteQueryResult
                    {
                        ShipmentId = item.ShipmentId,
                        TrackingNumber = item.TrackingNumber,
                        CheckPhoneNo = item.CheckPhoneNo,
                        FromCache = false,
                        QueriedAt = now,
                        Routes = routes,
                        DeliveredAt = deliveredAt,
                        HasException = exception != null,
                        ExceptionMessage = exception
                    };
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SF Express route query failed.");
                return batch.Select(i => BuildError(i, now, $"顺丰路由查询异常：{ex.Message}")).ToList();
            }
        }

        private async Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            if (_cache.TryGetValue<string>(TokenCacheKey, out var cached) && !string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }

            var options = _options.CurrentValue;
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["partnerID"] = options.PartnerId,
                ["secret"] = options.Secret,
                ["grantType"] = "password"
            });
            content.Headers.ContentType!.CharSet = "UTF-8";

            using var response = await _httpClient.PostAsync(options.AccessTokenUrl, content, ct);
            var token = await response.Content.ReadFromJsonAsync<SfOAuthTokenResponse>(JsonOptions, ct);
            if (!response.IsSuccessStatusCode
                || token == null
                || !string.Equals(token.ApiResultCode, "A1000", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                throw new InvalidOperationException($"获取顺丰 OAuth2 token 失败：{token?.ApiResultCode} {token?.ApiErrorMsg}");
            }

            var ttl = TimeSpan.FromSeconds(Math.Max(60, (token.ExpiresIn ?? 7200) - 300));
            _cache.Set(TokenCacheKey, token.AccessToken, ttl);
            return token.AccessToken;
        }

        private static SfRouteQueryResult CloneForShipment(SfRouteQueryResult cached, SfRouteQueryItem item, bool fromCache) => new()
        {
            ShipmentId = item.ShipmentId,
            TrackingNumber = item.TrackingNumber,
            CheckPhoneNo = item.CheckPhoneNo,
            Queryable = cached.Queryable,
            FromCache = fromCache,
            QueriedAt = cached.QueriedAt,
            Error = cached.Error,
            Routes = cached.Routes,
            DeliveredAt = cached.DeliveredAt,
            HasException = cached.HasException,
            ExceptionMessage = cached.ExceptionMessage
        };

        private static SfRouteQueryResult BuildError(SfRouteQueryItem item, DateTime now, string error) => new()
        {
            ShipmentId = item.ShipmentId,
            TrackingNumber = item.TrackingNumber,
            CheckPhoneNo = item.CheckPhoneNo,
            QueriedAt = now,
            Error = error
        };

        private static DateTime? ResolveDeliveredAt(IEnumerable<SfRouteNodeDto> routes)
        {
            var delivered = routes
                .Where(route => ContainsAny(route.SecondaryStatusName, "签收", "已签收")
                    || ContainsAny(route.FirstStatusName, "签收", "已签收")
                    || ContainsAny(route.Remark, "已签收", "签收成功", "本人签收", "代收", "驿站签收"))
                .Select(route => ParseRouteTime(route.AcceptTime))
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .OrderByDescending(value => value)
                .FirstOrDefault();

            return delivered == default ? null : delivered;
        }

        private static string? ResolveException(IEnumerable<SfRouteNodeDto> routes, bool delivered)
        {
            if (delivered)
            {
                return null;
            }

            var exceptionRoute = routes
                .OrderByDescending(route => ParseRouteTime(route.AcceptTime) ?? DateTime.MinValue)
                .FirstOrDefault(route =>
                    ContainsAny(
                        $"{route.FirstStatusName} {route.SecondaryStatusName} {route.Remark}",
                        "异常",
                        "拒收",
                        "退回",
                        "破损",
                        "遗失",
                        "丢失",
                        "滞留",
                        "无法派送",
                        "派送不成功",
                        "联系不上",
                        "未妥投",
                        "问题件"));

            return exceptionRoute == null
                ? null
                : $"{exceptionRoute.AcceptTime} {exceptionRoute.AcceptAddress} {exceptionRoute.Remark}".Trim();
        }

        private static DateTime? ParseRouteTime(string? value)
        {
            if (!DateTime.TryParse(value, out var parsed))
            {
                return null;
            }

            return DateTime.SpecifyKind(parsed.AddHours(-8), DateTimeKind.Utc);
        }

        private static bool ContainsAny(string? value, params string[] needles)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildRouteCacheKey(string trackingNumber, string checkPhoneNo) =>
            $"SfExpress:Route:{trackingNumber.Trim().ToUpperInvariant()}:{checkPhoneNo.Trim()}";
    }
}

using System.Globalization;
using System.Text.Json;
using AuditIt.Api.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AuditIt.Api.Services
{
    public class SfDeliveryEstimateService : ISfDeliveryEstimateService
    {
        private const string TokenCacheKey = "SfExpress:OAuthToken";
        private const string ServiceCode = SfExpressConstants.DeliveryEstimateServiceCode;
        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly IOptionsMonitor<SfExpressOptions> _options;
        private readonly ILogger<SfDeliveryEstimateService> _logger;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public SfDeliveryEstimateService(
            HttpClient httpClient,
            IMemoryCache cache,
            IOptionsMonitor<SfExpressOptions> options,
            ILogger<SfDeliveryEstimateService> logger)
        {
            _httpClient = httpClient;
            _cache = cache;
            _options = options;
            _logger = logger;
        }

        public async Task<SfDeliveryEstimateResultDto> QueryAsync(
            SfDeliveryEstimateQuery query,
            CancellationToken ct = default)
        {
            var destination = ParseAddress(query.DestinationAddress);
            var consignedTime = DateTime.SpecifyKind(query.StartDate.Date.AddDays(-3).AddHours(17), DateTimeKind.Unspecified);
            var targetDeliveryTime = DateTime.SpecifyKind(query.StartDate.Date.AddDays(-1).AddHours(23).AddMinutes(59).AddSeconds(59), DateTimeKind.Unspecified);
            var result = new SfDeliveryEstimateResultDto
            {
                DestinationAddress = query.DestinationAddress.Trim(),
                Destination = destination,
                Weight = query.Weight,
                ConsignedTime = consignedTime,
                TargetDeliveryTime = targetDeliveryTime,
            };

            if (query.Sources.Count == 0)
            {
                return result;
            }

            var options = _options.CurrentValue;
            if (string.IsNullOrWhiteSpace(options.PartnerId) || string.IsNullOrWhiteSpace(options.Secret))
            {
                result.Warehouses.AddRange(query.Sources.Select(source => BuildWarehouseError(source, "顺丰 PartnerId/Secret 未配置。")));
                return result;
            }

            string token;
            try
            {
                token = await GetAccessTokenAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SF Express delivery estimate token request failed.");
                result.Warehouses.AddRange(query.Sources.Select(source => BuildWarehouseError(source, $"获取顺丰访问令牌失败：{ex.Message}")));
                return result;
            }

            foreach (var source in query.Sources)
            {
                result.Warehouses.Add(await QuerySourceAsync(
                    source,
                    destination,
                    query.DestinationAddress,
                    query.Weight,
                    consignedTime,
                    targetDeliveryTime,
                    token,
                    ct));
            }

            return result;
        }

        private async Task<SfDeliveryEstimateWarehouseDto> QuerySourceAsync(
            SfDeliveryEstimateSource source,
            SfParsedAddressDto destination,
            string destinationAddress,
            decimal weight,
            DateTime consignedTime,
            DateTime targetDeliveryTime,
            string token,
            CancellationToken ct)
        {
            var parsedSource = ParseAddress(source.Address);
            if (string.IsNullOrWhiteSpace(source.Address))
            {
                return BuildWarehouseError(source, "该仓库未填写地址，无法查询顺丰时效。", parsedSource);
            }

            try
            {
                var options = _options.CurrentValue;
                var msgData = JsonSerializer.Serialize(new
                {
                    // 为空时按接口约定返回默认时效对应的全部产品。
                    businessType = string.Empty,
                    weight = (double)weight,
                    consignedTime = FormatLocalDateTime(consignedTime),
                    searchPrice = "1",
                    destAddress = new
                    {
                        province = destination.Province,
                        city = destination.City,
                        district = destination.District,
                        address = destinationAddress.Trim(),
                    },
                    srcAddress = new
                    {
                        province = parsedSource.Province,
                        city = parsedSource.City,
                        district = parsedSource.District,
                        address = source.Address.Trim(),
                    },
                }, JsonOptions);

                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["partnerID"] = options.PartnerId,
                    ["requestID"] = Guid.NewGuid().ToString("N"),
                    ["serviceCode"] = ServiceCode,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                    ["accessToken"] = token,
                    ["msgData"] = msgData,
                });
                content.Headers.ContentType!.CharSet = "UTF-8";

                using var response = await _httpClient.PostAsync(options.ServiceUrl, content, ct);
                var raw = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    return BuildWarehouseError(source, $"顺丰时效查询失败：HTTP {(int)response.StatusCode}", parsedSource);
                }

                var apiResult = DeserializeDeliveryResult(raw, out var platformError);
                if (platformError != null)
                {
                    return BuildWarehouseError(source, platformError, parsedSource);
                }

                if (apiResult == null || !apiResult.Success)
                {
                    return BuildWarehouseError(
                        source,
                        $"顺丰业务返回 {apiResult?.ErrorCode ?? "未知错误"}：{apiResult?.ErrorMsg ?? "未返回时效产品"}",
                        parsedSource);
                }

                var products = (apiResult.MsgData?.DeliverTmDto ?? new List<SfDeliveryProductResponse>())
                    .Select(product => ToProduct(product, consignedTime, targetDeliveryTime))
                    .ToList();

                return new SfDeliveryEstimateWarehouseDto
                {
                    WarehouseId = source.WarehouseId,
                    WarehouseName = source.WarehouseName,
                    Address = source.Address,
                    Source = parsedSource,
                    Products = products,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SF Express delivery estimate failed for warehouse {WarehouseId}.", source.WarehouseId);
                return BuildWarehouseError(source, $"顺丰时效查询异常：{ex.Message}", parsedSource);
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
                ["grantType"] = "password",
            });
            content.Headers.ContentType!.CharSet = "UTF-8";

            using var response = await _httpClient.PostAsync(options.AccessTokenUrl, content, ct);
            var token = await response.Content.ReadFromJsonAsync<SfOAuthTokenResponse>(JsonOptions, ct);
            if (!response.IsSuccessStatusCode
                || token == null
                || !string.Equals(token.ApiResultCode, "A1000", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                throw new InvalidOperationException($"{token?.ApiResultCode} {token?.ApiErrorMsg}");
            }

            var ttl = TimeSpan.FromSeconds(Math.Max(60, (token.ExpiresIn ?? 7200) - 300));
            _cache.Set(TokenCacheKey, token.AccessToken, ttl);
            return token.AccessToken;
        }

        private static SfDeliveryProductDto ToProduct(
            SfDeliveryProductResponse product,
            DateTime consignedTime,
            DateTime targetDeliveryTime)
        {
            var deliveryTime = ParseLatestDate(product.DeliverTime);
            DateTime? latestShipTime = deliveryTime.HasValue
                ? DateTime.SpecifyKind(consignedTime.Add(targetDeliveryTime - deliveryTime.Value), DateTimeKind.Unspecified)
                : null;

            return new SfDeliveryProductDto
            {
                BusinessType = product.BusinessType ?? string.Empty,
                BusinessTypeDesc = product.BusinessTypeDesc ?? product.BusinessType ?? string.Empty,
                DeliverTime = product.DeliverTime,
                Fee = product.Fee,
                SearchPrice = product.SearchPrice,
                CloseTime = product.CloseTime,
                DeliveryTime = deliveryTime,
                PlannedDeliveryTime = deliveryTime.HasValue ? targetDeliveryTime : null,
                LatestShipTime = latestShipTime,
                ConsignedTime = consignedTime,
            };
        }

        private static SfDeliveryEstimateWarehouseDto BuildWarehouseError(
            SfDeliveryEstimateSource source,
            string error,
            SfParsedAddressDto? parsedSource = null) => new()
            {
                WarehouseId = source.WarehouseId,
                WarehouseName = source.WarehouseName,
                Address = source.Address,
                Source = parsedSource ?? ParseAddress(source.Address),
                Error = error,
            };

        private static SfDeliveryApiResult? DeserializeDeliveryResult(string raw, out string? platformError)
        {
            platformError = null;
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var businessRoot = root;

            if (root.TryGetProperty("apiResultCode", out var apiResultCode)
                && !string.Equals(apiResultCode.GetString(), "A1000", StringComparison.OrdinalIgnoreCase))
            {
                var apiError = root.TryGetProperty("apiErrorMsg", out var apiErrorMsg) ? apiErrorMsg.GetString() : null;
                platformError = $"顺丰平台返回 {apiResultCode.GetString()}：{apiError}";
                return null;
            }

            if (root.TryGetProperty("apiResultData", out var apiResultData))
            {
                if (apiResultData.ValueKind == JsonValueKind.String)
                {
                    var text = apiResultData.GetString();
                    if (string.IsNullOrWhiteSpace(text)) return null;
                    using var nested = JsonDocument.Parse(text);
                    businessRoot = nested.RootElement.Clone();
                }
                else
                {
                    businessRoot = apiResultData;
                }
            }

            return businessRoot.Deserialize<SfDeliveryApiResult>(JsonOptions);
        }

        private static SfParsedAddressDto ParseAddress(string? address)
        {
            var value = (address ?? string.Empty).Replace(" ", string.Empty).Replace("　", string.Empty);
            var province = new[]
            {
                "新疆维吾尔自治区", "广西壮族自治区", "宁夏回族自治区", "内蒙古自治区", "西藏自治区",
                "黑龙江省", "吉林省", "辽宁省", "河北省", "山西省", "江苏省", "浙江省", "安徽省",
                "福建省", "江西省", "山东省", "河南省", "湖北省", "湖南省", "广东省", "海南省",
                "四川省", "贵州省", "云南省", "陕西省", "甘肃省", "青海省", "台湾省",
                "北京市", "天津市", "上海市", "重庆市", "香港特别行政区", "澳门特别行政区",
            }.FirstOrDefault(value.StartsWith);

            string? normalizedProvince = province;
            if (province is "北京市" or "天津市" or "上海市" or "重庆市")
            {
                normalizedProvince = province[..2];
            }

            var remaining = province == null ? value : value[province.Length..];
            string? city = normalizedProvince;
            if (normalizedProvince is not ("北京" or "天津" or "上海" or "重庆")
                && remaining.Length > 0)
            {
                var cityMatch = System.Text.RegularExpressions.Regex.Match(
                    remaining,
                    "^(?<city>[\\u4e00-\\u9fff]{2,12}(?:市|自治州|地区|盟))");
                city = cityMatch.Success ? cityMatch.Groups["city"].Value : null;
                if (city != null) remaining = remaining[city.Length..];
            }

            var districtMatch = System.Text.RegularExpressions.Regex.Match(
                remaining,
                "^(?<district>[\\u4e00-\\u9fff]{2,12}(?:区|县|旗|市))");

            return new SfParsedAddressDto
            {
                Province = normalizedProvince,
                City = city,
                District = districtMatch.Success ? districtMatch.Groups["district"].Value : null,
            };
        }

        private static DateTime? ParseLatestDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var dates = value
                .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => DateTime.TryParse(part.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
                    ? DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified)
                    : (DateTime?)null)
                .Where(parsed => parsed.HasValue)
                .Select(parsed => parsed!.Value)
                .ToList();
            return dates.Count == 0 ? null : dates.Max();
        }

        private static string FormatLocalDateTime(DateTime value) =>
            value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        private sealed class SfDeliveryApiResult
        {
            public bool Success { get; set; }
            public string? ErrorCode { get; set; }
            public string? ErrorMsg { get; set; }
            public SfDeliveryMsgData? MsgData { get; set; }
        }

        private sealed class SfDeliveryMsgData
        {
            public List<SfDeliveryProductResponse> DeliverTmDto { get; set; } = new();
        }

        private sealed class SfDeliveryProductResponse
        {
            public string? BusinessType { get; set; }
            public string? BusinessTypeDesc { get; set; }
            public string? DeliverTime { get; set; }
            public decimal? Fee { get; set; }
            public string? SearchPrice { get; set; }
            public string? CloseTime { get; set; }
        }
    }
}

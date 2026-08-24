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
        private const int DeliverySearchBackDays = 7;
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
            var destination = ChineseAddressParser.Parse(query.DestinationAddress);
            var normalizedDestinationAddress = ChineseAddressParser.NormalizeForSf(query.DestinationAddress);
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
                    normalizedDestinationAddress,
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
            var parsedSource = ChineseAddressParser.Parse(source.Address);
            if (string.IsNullOrWhiteSpace(source.Address))
            {
                return BuildWarehouseError(source, "该仓库未填写地址，无法查询顺丰时效。", parsedSource);
            }

            try
            {
                var selections = new Dictionary<string, SfDeliveryProductSelection>(StringComparer.OrdinalIgnoreCase);
                string? firstError = null;
                var candidateTimes = new[] { consignedTime }
                    .Concat(BuildCandidateConsignedTimes(targetDeliveryTime))
                    .Distinct()
                    .ToList();

                foreach (var candidateConsignedTime in candidateTimes)
                {
                    var queryResult = await QuerySourceAtTimeAsync(
                        source,
                        destination,
                        destinationAddress,
                        parsedSource,
                        weight,
                        candidateConsignedTime,
                        token,
                        ct);
                    if (queryResult.Error != null)
                    {
                        firstError ??= queryResult.Error;
                        continue;
                    }

                    foreach (var product in queryResult.Products)
                    {
                        if (!selections.TryGetValue(product.BusinessType!, out var selection))
                        {
                            selection = new SfDeliveryProductSelection
                            {
                                FallbackProduct = product,
                                FallbackConsignedTime = candidateConsignedTime,
                            };
                            selections[product.BusinessType!] = selection;
                        }

                        var deliveryTime = ParseLatestDate(product.DeliverTime);
                        if (!deliveryTime.HasValue || deliveryTime.Value.Date != targetDeliveryTime.Date)
                        {
                            continue;
                        }

                        if (selection.BestProduct == null
                            || candidateConsignedTime > selection.BestConsignedTime
                            || (candidateConsignedTime == selection.BestConsignedTime
                                && (selection.BestDeliveryTime == null
                                    || deliveryTime.Value > selection.BestDeliveryTime.Value)))
                        {
                            selection.BestProduct = product;
                            selection.BestConsignedTime = candidateConsignedTime;
                            selection.BestDeliveryTime = deliveryTime.Value;
                        }
                    }

                    if (selections.Count > 0 && selections.Values.All(selection => selection.BestProduct != null))
                    {
                        break;
                    }
                }

                if (selections.Count == 0 && firstError != null)
                {
                    return BuildWarehouseError(source, firstError, parsedSource);
                }

                var products = selections.Values
                    .OrderBy(selection => int.TryParse(
                        selection.FallbackProduct?.BusinessType,
                        out var businessType)
                        ? businessType
                        : int.MaxValue)
                    .Select(selection => ToProduct(
                        selection.BestProduct ?? selection.FallbackProduct!,
                        selection.FallbackConsignedTime,
                        targetDeliveryTime,
                        selection.BestProduct == null ? null : selection.BestConsignedTime))
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

        private async Task<SfDeliveryApiQueryResult> QuerySourceAtTimeAsync(
            SfDeliveryEstimateSource source,
            SfParsedAddressDto destination,
            string destinationAddress,
            SfParsedAddressDto parsedSource,
            decimal weight,
            DateTime consignedTime,
            string token,
            CancellationToken ct)
        {
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
                        address = destinationAddress,
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
                    return new SfDeliveryApiQueryResult
                    {
                        Error = $"顺丰时效查询失败：HTTP {(int)response.StatusCode}",
                    };
                }

                var apiResult = DeserializeDeliveryResult(raw, out var platformError);
                if (platformError != null)
                {
                    return new SfDeliveryApiQueryResult { Error = platformError };
                }

                if (apiResult == null || !apiResult.Success)
                {
                    return new SfDeliveryApiQueryResult
                    {
                        Error = $"顺丰业务返回 {apiResult?.ErrorCode ?? "未知错误"}：{apiResult?.ErrorMsg ?? "未返回时效产品"}",
                    };
                }

                return new SfDeliveryApiQueryResult
                {
                    Products = (apiResult.MsgData?.DeliverTmDto ?? new List<SfDeliveryProductResponse>())
                        .Where(product => IsDisplayableProduct(product.BusinessType))
                        .Where(product => !string.IsNullOrWhiteSpace(product.BusinessType))
                        .ToList(),
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SF Express delivery estimate failed for warehouse {WarehouseId}.", source.WarehouseId);
                return new SfDeliveryApiQueryResult { Error = $"顺丰时效查询异常：{ex.Message}" };
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
            DateTime targetDeliveryTime,
            DateTime? selectedConsignedTime)
        {
            var deliveryTime = ParseLatestDate(product.DeliverTime);
            var effectiveConsignedTime = selectedConsignedTime ?? consignedTime;
            int? deliveryDays = deliveryTime.HasValue
                ? Math.Max(1, (int)Math.Ceiling((deliveryTime.Value - effectiveConsignedTime).TotalDays))
                : null;
            DateTime? latestShipTime = selectedConsignedTime.HasValue && deliveryDays.HasValue
                ? BuildLatestShipTime(targetDeliveryTime, deliveryDays.Value)
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
                DeliveryDays = deliveryDays,
                PlannedDeliveryTime = selectedConsignedTime.HasValue && deliveryTime.HasValue
                    ? targetDeliveryTime
                    : null,
                LatestShipTime = latestShipTime,
                ConsignedTime = effectiveConsignedTime,
            };
        }

        private static IEnumerable<DateTime> BuildCandidateConsignedTimes(DateTime targetDeliveryTime)
        {
            for (var daysBack = 0; daysBack <= DeliverySearchBackDays; daysBack++)
            {
                yield return DateTime.SpecifyKind(
                    targetDeliveryTime.Date.AddDays(-daysBack).AddHours(18).AddMinutes(59).AddSeconds(59),
                    DateTimeKind.Unspecified);
            }
        }

        private static DateTime BuildLatestShipTime(DateTime targetDeliveryTime, int deliveryDays)
        {
            var latestShipDate = targetDeliveryTime.Date.AddDays(-Math.Max(1, deliveryDays));
            return AdjustToPickupWindow(DateTime.SpecifyKind(
                latestShipDate.AddHours(18).AddMinutes(59).AddSeconds(59),
                DateTimeKind.Unspecified));
        }

        private static SfDeliveryEstimateWarehouseDto BuildWarehouseError(
            SfDeliveryEstimateSource source,
            string error,
            SfParsedAddressDto? parsedSource = null) => new()
            {
                WarehouseId = source.WarehouseId,
                WarehouseName = source.WarehouseName,
                Address = source.Address,
                Source = parsedSource ?? ChineseAddressParser.Parse(source.Address),
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

        private static DateTime AdjustToPickupWindow(DateTime value)
        {
            var pickupStart = TimeSpan.FromHours(5);
            var pickupEnd = TimeSpan.FromHours(19);
            if (value.TimeOfDay < pickupStart)
            {
                return value.Date.AddDays(-1).AddHours(18).AddMinutes(59).AddSeconds(59);
            }

            if (value.TimeOfDay >= pickupEnd)
            {
                return value.Date.AddHours(18).AddMinutes(59).AddSeconds(59);
            }

            return value;
        }

        private static bool IsDisplayableProduct(string? businessType) =>
            int.TryParse(businessType, out var type) && type < 10;

        private static string FormatLocalDateTime(DateTime value) =>
            value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        private sealed class SfDeliveryApiQueryResult
        {
            public List<SfDeliveryProductResponse> Products { get; init; } = new();
            public string? Error { get; init; }
        }

        private sealed class SfDeliveryProductSelection
        {
            public SfDeliveryProductResponse? FallbackProduct { get; init; }
            public DateTime FallbackConsignedTime { get; init; }
            public SfDeliveryProductResponse? BestProduct { get; set; }
            public DateTime BestConsignedTime { get; set; }
            public DateTime? BestDeliveryTime { get; set; }
        }

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

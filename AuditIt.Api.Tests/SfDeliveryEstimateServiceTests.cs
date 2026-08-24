using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AuditIt.Api.Tests;

public class SfDeliveryEstimateServiceTests
{
    [Fact]
    public async Task QueryAsync_sendsAllProductQueryAndComputesLatestShipTime()
    {
        var handler = new SfEstimateHandler();
        using var httpClient = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var options = new StaticOptionsMonitor<SfExpressOptions>(new SfExpressOptions
        {
            PartnerId = "partner",
            Secret = "secret",
            AccessTokenUrl = "https://sf.test/oauth2/accessToken",
            ServiceUrl = "https://sf.test/std/service",
        });
        var service = new SfDeliveryEstimateService(
            httpClient,
            cache,
            options,
            NullLogger<SfDeliveryEstimateService>.Instance);

        var result = await service.QueryAsync(new SfDeliveryEstimateQuery
        {
            DestinationAddress = "孙也 18817710672 上海市青浦区徐泾镇乐爱路333弄18号楼202室",
            StartDate = new DateTime(2026, 8, 20),
            Weight = 2.5m,
            Sources = new[]
            {
                new SfDeliveryEstimateSource
                {
                    WarehouseId = 7,
                    WarehouseName = "广州仓",
                    Address = "广东省广州市海珠区琶洲街道2号",
                },
            },
        });

        var product = Assert.Single(Assert.Single(result.Warehouses).Products);
        Assert.Equal("上海", result.Destination.Province);
        Assert.Equal("上海", result.Destination.City);
        Assert.Equal("青浦区", result.Destination.District);
        Assert.Equal("广州仓", result.Warehouses[0].WarehouseName);
        Assert.Equal("顺丰特惠", product.BusinessTypeDesc);
        Assert.Equal(12.5m, product.Fee);
        Assert.Equal(2, product.DeliveryDays);
        Assert.Equal(new DateTime(2026, 8, 19, 23, 59, 59), product.PlannedDeliveryTime);
        Assert.Equal(new DateTime(2026, 8, 19, 19, 59, 59), product.DeliveryTime);
        Assert.Equal(new DateTime(2026, 8, 17, 18, 59, 59), product.LatestShipTime);
        Assert.True(handler.RequestedConsignedTimes.Count >= 3);
        Assert.Contains("EXP_RECE_QUERY_DELIVERTM", handler.ServiceRequestBody);
        Assert.Contains("weight%22%3A2.5", handler.ServiceRequestBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%5Cu4E0A%5Cu6D77%5Cu5E02", handler.ServiceRequestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%5Cu5B99%5Cu4E5F", handler.ServiceRequestBody, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SfEstimateHandler : HttpMessageHandler
    {
        public string ServiceRequestBody { get; private set; } = string.Empty;
        public List<DateTime> RequestedConsignedTimes { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.Contains("accessToken", StringComparison.OrdinalIgnoreCase) == true)
            {
                return JsonResponse("{\"apiResultCode\":\"A1000\",\"accessToken\":\"token\",\"expiresIn\":7200}");
            }

            ServiceRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var consignedTime = ReadConsignedTime(ServiceRequestBody);
            RequestedConsignedTimes.Add(consignedTime);
            var deliveryTime = consignedTime.AddHours(25);
            var deliveryText = deliveryTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var business = JsonSerializer.Serialize(new
            {
                success = true,
                errorCode = "S0000",
                errorMsg = (string?)null,
                msgData = new
                {
                    deliverTmDto = new[]
                    {
                        new
                        {
                            businessType = "2",
                            businessTypeDesc = "顺丰特惠",
                            deliverTime = $"{deliveryText},{deliveryText}",
                            fee = 12.5m,
                            searchPrice = "1",
                            closeTime = "17:00:00",
                        },
                        new
                        {
                            businessType = "113",
                            businessTypeDesc = "便利时效",
                            deliverTime = $"{deliveryText},{deliveryText}",
                            fee = 20m,
                            searchPrice = "1",
                            closeTime = "17:00:00",
                        },
                    },
                },
            });
            return JsonResponse($"{{\"apiResultCode\":\"A1000\",\"apiResultData\":{JsonSerializerForString(business)}}}");
        }

        private static DateTime ReadConsignedTime(string body)
        {
            var encodedMsgData = body
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Single(part => part.StartsWith("msgData=", StringComparison.Ordinal))
                ["msgData=".Length..];
            var msgData = Uri.UnescapeDataString(encodedMsgData.Replace('+', ' '));
            using var document = JsonDocument.Parse(msgData);
            return DateTime.Parse(
                document.RootElement.GetProperty("consignedTime").GetString()!,
                CultureInfo.InvariantCulture);
        }

        private static HttpResponseMessage JsonResponse(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };

        private static string JsonSerializerForString(string value) =>
            System.Text.Json.JsonSerializer.Serialize(value);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => NoopDisposable.Instance;

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }
}

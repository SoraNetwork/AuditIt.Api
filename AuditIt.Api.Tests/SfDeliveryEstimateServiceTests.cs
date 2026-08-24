using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
            DestinationAddress = "广东省广州市越秀区北京街道西湖路38号",
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
        Assert.Equal("广东省", result.Destination.Province);
        Assert.Equal("广州市", result.Destination.City);
        Assert.Equal("越秀区", result.Destination.District);
        Assert.Equal("广州仓", result.Warehouses[0].WarehouseName);
        Assert.Equal("顺丰特惠", product.BusinessTypeDesc);
        Assert.Equal(12.5m, product.Fee);
        Assert.Equal(new DateTime(2026, 8, 19, 23, 59, 59), product.PlannedDeliveryTime);
        Assert.Equal(new DateTime(2026, 8, 18, 4, 59, 59), product.LatestShipTime);
        Assert.Contains("EXP_RECE_QUERY_DELIVERTM", handler.ServiceRequestBody);
        Assert.Contains("weight%22%3A2.5", handler.ServiceRequestBody, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SfEstimateHandler : HttpMessageHandler
    {
        public string ServiceRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.Contains("accessToken", StringComparison.OrdinalIgnoreCase) == true)
            {
                return JsonResponse("{\"apiResultCode\":\"A1000\",\"accessToken\":\"token\",\"expiresIn\":7200}");
            }

            ServiceRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var business = "{\"success\":true,\"errorCode\":\"S0000\",\"errorMsg\":null,\"msgData\":{\"deliverTmDto\":[{\"businessType\":\"2\",\"businessTypeDesc\":\"顺丰特惠\",\"deliverTime\":\"2026-08-19 12:00:00,2026-08-08 12:00:00\",\"fee\":12.5,\"searchPrice\":\"1\",\"closeTime\":\"17:00:00\"}]}}";
            return JsonResponse($"{{\"apiResultCode\":\"A1000\",\"apiResultData\":{JsonSerializerForString(business)}}}");
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

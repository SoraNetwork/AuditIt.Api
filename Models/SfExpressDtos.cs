using System.Text.Json.Serialization;

namespace AuditIt.Api.Models
{
    public class SfExpressOptions
    {
        public string PartnerId { get; set; } = string.Empty;
        public string Secret { get; set; } = string.Empty;
        public string AccessTokenUrl { get; set; } = "https://bspgw.sf-express.com/oauth2/accessToken";
        public string ServiceUrl { get; set; } = "https://bspgw.sf-express.com/std/service";
        public string Language { get; set; } = "zh-CN";
        public int CacheMinutes { get; set; } = 120;
        public bool DailySyncEnabled { get; set; } = true;
        public int DailySyncHourLocal { get; set; } = 9;
        public int DailySyncMinuteLocal { get; set; } = 0;
        public string TimeZoneId { get; set; } = "China Standard Time";
    }

    public class SfRouteQueryItem
    {
        public int ShipmentId { get; set; }
        public string TrackingNumber { get; set; } = string.Empty;
        public string CheckPhoneNo { get; set; } = string.Empty;
    }

    public class SfRouteQueryResult
    {
        public int ShipmentId { get; set; }
        public string TrackingNumber { get; set; } = string.Empty;
        public string CheckPhoneNo { get; set; } = string.Empty;
        public bool Queryable { get; set; } = true;
        public bool FromCache { get; set; }
        public DateTime QueriedAt { get; set; }
        public string ServiceCode { get; set; } = SfExpressConstants.RouteServiceCode;
        public int TrackingType { get; set; } = 1;
        public string MethodType { get; set; } = "1";
        public string? Error { get; set; }
        public bool HasException { get; set; }
        public string? ExceptionMessage { get; set; }
        public List<SfRouteNodeDto> Routes { get; set; } = new();
        public DateTime? DeliveredAt { get; set; }
    }

    public class SfShipmentRouteDto
    {
        public int ShipmentId { get; set; }
        public string TrackingNumber { get; set; } = string.Empty;
        public string CheckPhoneNo { get; set; } = string.Empty;
        public bool Queryable { get; set; } = true;
        public bool FromCache { get; set; }
        public DateTime? QueriedAt { get; set; }
        public string ServiceCode { get; set; } = SfExpressConstants.RouteServiceCode;
        public int TrackingType { get; set; } = 1;
        public string MethodType { get; set; } = "1";
        public string? Error { get; set; }
        public DateTime? DeliveredAt { get; set; }
        public bool AutoDelivered { get; set; }
        public bool HasException { get; set; }
        public string? ExceptionMessage { get; set; }
        public List<SfRouteNodeDto> Routes { get; set; } = new();
    }

    public class SfRouteNodeDto
    {
        [JsonPropertyName("acceptTime")]
        public string? AcceptTime { get; set; }

        [JsonPropertyName("acceptAddress")]
        public string? AcceptAddress { get; set; }

        [JsonPropertyName("remark")]
        public string? Remark { get; set; }

        [JsonPropertyName("opCode")]
        public string? OpCode { get; set; }

        [JsonPropertyName("firstStatusCode")]
        public string? FirstStatusCode { get; set; }

        [JsonPropertyName("firstStatusName")]
        public string? FirstStatusName { get; set; }

        [JsonPropertyName("secondaryStatusCode")]
        public string? SecondaryStatusCode { get; set; }

        [JsonPropertyName("secondaryStatusName")]
        public string? SecondaryStatusName { get; set; }
    }

    public class SfRouteSyncResultDto
    {
        public RentalDto? Rental { get; set; }
        public List<SfShipmentRouteDto> Shipments { get; set; } = new();
    }

    public static class SfExpressConstants
    {
        public const string RouteServiceCode = "EXP_RECE_SEARCH_ROUTES";
    }

    internal class SfOAuthTokenResponse
    {
        [JsonPropertyName("apiResultCode")]
        public string? ApiResultCode { get; set; }

        [JsonPropertyName("apiErrorMsg")]
        public string? ApiErrorMsg { get; set; }

        [JsonPropertyName("accessToken")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expiresIn")]
        public int? ExpiresIn { get; set; }
    }

    internal class SfApiEnvelope
    {
        [JsonPropertyName("apiResultCode")]
        public string? ApiResultCode { get; set; }

        [JsonPropertyName("apiErrorMsg")]
        public string? ApiErrorMsg { get; set; }

        [JsonPropertyName("apiResultData")]
        public string? ApiResultData { get; set; }
    }

    internal class SfRouteApiResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("errorCode")]
        public string? ErrorCode { get; set; }

        [JsonPropertyName("errorMsg")]
        public string? ErrorMsg { get; set; }

        [JsonPropertyName("msgData")]
        public SfRouteMsgData? MsgData { get; set; }
    }

    internal class SfRouteMsgData
    {
        [JsonPropertyName("routeResps")]
        public List<SfRouteResponse> RouteResps { get; set; } = new();
    }

    internal class SfRouteResponse
    {
        [JsonPropertyName("mailNo")]
        public string? MailNo { get; set; }

        [JsonPropertyName("routes")]
        public List<SfRouteNodeDto> Routes { get; set; } = new();
    }
}

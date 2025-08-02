using System.Text.Json.Serialization;

namespace AuditIt.Api.Models
{
    public class DingTalkUserResponse
    {
        [JsonPropertyName("request_id")]
        public string RequestId { get; set; }

        [JsonPropertyName("errcode")]
        public int ErrorCode { get; set; }

        [JsonPropertyName("errmsg")]
        public string ErrorMessage { get; set; }

        [JsonPropertyName("result")]
        public DingTalkUserInfo Result { get; set; }
    }

    public class DingTalkUserInfo
    {
        [JsonPropertyName("userid")]
        public string UserId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("unionid")]
        public string UnionId { get; set; }

        [JsonPropertyName("associated_unionid")]
        public string AssociatedUnionId { get; set; }

        [JsonPropertyName("device_id")]
        public string DeviceId { get; set; }

        [JsonPropertyName("sys")]
        public bool IsAdmin { get; set; }

        [JsonPropertyName("sys_level")]
        public int AdminLevel { get; set; }
    }
}
using System.Text.Json.Serialization;

namespace AuditIt.Api.Models
{
    public class DingTalkSnsResponse
    {
        [JsonPropertyName("errcode")]
        public int ErrorCode { get; set; }

        [JsonPropertyName("errmsg")]
        public string ErrorMessage { get; set; }

        [JsonPropertyName("user_info")]
        public SnsUserInfo UserInfo { get; set; }
    }

    public class SnsUserInfo
    {
        [JsonPropertyName("nick")]
        public string Nick { get; set; }

        [JsonPropertyName("unionid")]
        public string UnionId { get; set; }

        [JsonPropertyName("openid")]
        public string OpenId { get; set; }

        [JsonPropertyName("main_org_auth_high_level")]
        public bool IsAdmin { get; set; }
    }
}

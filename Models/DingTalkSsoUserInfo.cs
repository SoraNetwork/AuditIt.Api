using System.Text.Json.Serialization;

namespace AuditIt.Api.Models
{
    public class DingTalkSsoUserInfo
    {
        [JsonPropertyName("nick")]
        public string Nick { get; set; }

        [JsonPropertyName("unionId")]
        public string UnionId { get; set; }

        [JsonPropertyName("openId")]
        public string OpenId { get; set; }

        [JsonPropertyName("mobile")]
        public string Mobile { get; set; }
        
        [JsonPropertyName("avatarUrl")]
        public string AvatarUrl { get; set; }
    }
}
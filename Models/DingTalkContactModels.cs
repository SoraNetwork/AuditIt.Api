using System.Text.Json.Serialization;

namespace AuditIt.Api.Models
{
    // Corresponds to the response from /v1.0/contact/users/me
    public class DingTalkContactUser
    {
        [JsonPropertyName("nick")]
        public string Nick { get; set; }

        [JsonPropertyName("avatarUrl")]
        public string AvatarUrl { get; set; }

        [JsonPropertyName("mobile")]
        public string Mobile { get; set; }

        [JsonPropertyName("openId")]
        public string OpenId { get; set; }

        [JsonPropertyName("unionId")]
        public string UnionId { get; set; }

        [JsonPropertyName("email")]
        public string Email { get; set; }

        [JsonPropertyName("stateCode")]
        public string StateCode { get; set; }
    }
}

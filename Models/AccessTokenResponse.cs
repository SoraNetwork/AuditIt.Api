using System.Text.Json.Serialization;

namespace AuditIt.Api.Models
{
    public class AccessTokenResponse
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; }

        [JsonPropertyName("expireIn")]
        public long ExpireIn { get; set; }
    }
}

using System.Text.Json.Serialization;

namespace AuditIt.Api.Models
{
    // Corresponds to the response from /v1.0/oauth2/userAccessToken
    public class UserAccessTokenResponse
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; }

        [JsonPropertyName("refreshToken")]
        public string RefreshToken { get; set; }

        [JsonPropertyName("expireIn")]
        public long ExpireIn { get; set; }

        [JsonPropertyName("corpId")]
        public string CorpId { get; set; }
    }
}

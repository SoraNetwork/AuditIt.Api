using System.Text.Json.Serialization;

namespace AuditIt.Api.Models
{
    public class DingTalkSsoUserInfo
    {
        [JsonPropertyName("corpId")]
        public string CorpId { get; set; }

        [JsonPropertyName("corpName")]
        public string CorpName { get; set; }

        [JsonPropertyName("userId")]
        public string UserId { get; set; }

        [JsonPropertyName("email")]
        public string Email { get; set; }

        [JsonPropertyName("userName")]
        public string UserName { get; set; }

        [JsonPropertyName("avatar")]
        public string Avatar { get; set; }

        [JsonPropertyName("isAdmin")]
        public bool IsAdmin { get; set; }
    }
}

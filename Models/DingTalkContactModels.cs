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

    public class DingTalkApiResponse<T>
    {
        [JsonPropertyName("errcode")]
        public int ErrorCode { get; set; }

        [JsonPropertyName("errmsg")]
        public string? ErrorMessage { get; set; }

        [JsonPropertyName("result")]
        public T? Result { get; set; }
    }

    public class DingTalkDepartment
    {
        [JsonPropertyName("dept_id")]
        public long DeptId { get; set; }

        [JsonPropertyName("parent_id")]
        public long ParentId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    public class DingTalkDepartmentUserListResult
    {
        [JsonPropertyName("userid_list")]
        public List<string> UserIds { get; set; } = new();
    }

    public class DingTalkUserDetail
    {
        [JsonPropertyName("userid")]
        public string UserId { get; set; } = string.Empty;

        [JsonPropertyName("unionid")]
        public string? UnionId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("mobile")]
        public string? Mobile { get; set; }

        [JsonPropertyName("job_number")]
        public string? JobNumber { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("active")]
        public bool? Active { get; set; }
    }

    public class DingTalkWorkNoticeResponse
    {
        [JsonPropertyName("errcode")]
        public int ErrorCode { get; set; }

        [JsonPropertyName("errmsg")]
        public string? ErrorMessage { get; set; }

        [JsonPropertyName("task_id")]
        public long? TaskId { get; set; }
    }
}

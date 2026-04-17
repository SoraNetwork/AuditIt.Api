using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace AuditIt.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly IDingTalkService _dingTalkService;
        private readonly IIdentityService _identityService;

        public AuthController(IConfiguration configuration, IDingTalkService dingTalkService, IIdentityService identityService)
        {
            _configuration = configuration;
            _dingTalkService = dingTalkService;
            _identityService = identityService;
        }

        [HttpPost("dingtalk-login")]
        public async Task<IActionResult> DingTalkLogin([FromBody] DingTalkLoginRequest request)
        {
            if (string.IsNullOrEmpty(request.Code))
                return BadRequest("免登授权码不能为空");

            try
            {
                var dingTalkUser = await _dingTalkService.GetLegacyUserInfoByCodeAsync(request.Code);
                return await BuildLoginResponseAsync(dingTalkUser.Name, dingTalkUser.UserId);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"通过免登码获取钉钉用户信息失败: {ex.Message}");
            }
        }

        [HttpPost("dingtalk-sso-login")]
        public async Task<IActionResult> DingTalkSsoLogin([FromBody] DingTalkLoginRequest request)
        {
            if (string.IsNullOrEmpty(request.Code))
                return BadRequest("SSO 授权码不能为空");

            try
            {
                var dingTalkUser = await _dingTalkService.GetSsoUserInfoByCodeAsync(request.Code);
                return await BuildLoginResponseAsync(dingTalkUser.Nick, dingTalkUser.UnionId);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"通过 SSO 授权码获取钉钉用户信息失败: {ex.Message}");
            }
        }

        private async Task<IActionResult> BuildLoginResponseAsync(string? name, string? dingTalkId)
        {
            if (string.IsNullOrWhiteSpace(name))
                return StatusCode(500, "钉钉用户未携带姓名，无法登录。");

            var result = await _identityService.LoginUpsertAsync(name, dingTalkId);
            if (result == null)
                return StatusCode(403, "账号已离职，禁止登录。");

            var (user, permissions) = result.Value;
            var token = GenerateJwtToken(user, permissions);

            return Ok(new
            {
                Token = token,
                User = new
                {
                    user.Id,
                    user.Name,
                    user.Status,
                    LastLoginAt = user.LastLoginAt
                },
                Permissions = permissions
            });
        }

        private string GenerateJwtToken(User user, IReadOnlyList<string> permissions)
        {
            var key = _configuration["Jwt:Key"] ?? string.Empty;
            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.Name),
                new Claim("userId", user.Id.ToString()),
                new Claim("dingTalkId", user.LastDingTalkId ?? user.DingTalkId ?? string.Empty),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };
            foreach (var p in permissions)
                claims.Add(new Claim(PermissionClaimType.Permission, p));

            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddHours(72),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }

    public class DingTalkLoginRequest
    {
        public string? Code { get; set; }
    }
}

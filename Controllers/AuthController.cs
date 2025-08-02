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

        public AuthController(IConfiguration configuration, IDingTalkService dingTalkService)
        {
            _configuration = configuration;
            _dingTalkService = dingTalkService;
        }

        [HttpPost("dingtalk-login")]
        public async Task<IActionResult> DingTalkLogin([FromBody] DingTalkLoginRequest request)
        {
            if (string.IsNullOrEmpty(request.Code))
            {
                return BadRequest("免登授权码不能为空");
            }

            try
            {
                var dingTalkUser = await _dingTalkService.GetUserInfoByCodeAsync(request.Code);

                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Name = dingTalkUser.Name,
                    DingTalkId = dingTalkUser.UserId,
                };

                var token = GenerateJwtToken(user);

                return Ok(new { Token = token, User = user });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"获取钉钉用户信息失败: {ex.Message}");
            }
            
        }

        private string GenerateJwtToken(User user)
        {
            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]));
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Name),
                new Claim("dingTalkId", user.DingTalkId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.Now.AddMinutes(120),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }

    public class DingTalkLoginRequest
    {
        public string Code { get; set; }
    }
}

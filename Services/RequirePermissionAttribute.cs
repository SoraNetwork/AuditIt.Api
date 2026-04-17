using AuditIt.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AuditIt.Api.Services
{
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
    public class RequirePermissionAttribute : Attribute, IAuthorizationFilter
    {
        private readonly string[] _required;

        public RequirePermissionAttribute(params string[] required)
        {
            _required = required ?? Array.Empty<string>();
        }

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var user = context.HttpContext.User;
            if (user?.Identity == null || !user.Identity.IsAuthenticated)
            {
                context.Result = new UnauthorizedResult();
                return;
            }

            if (_required.Length == 0) return;

            var held = user.FindAll(PermissionClaimType.Permission).Select(c => c.Value).ToHashSet();
            foreach (var need in _required)
            {
                if (!held.Contains(need))
                {
                    context.Result = new ObjectResult(new { error = $"缺少权限：{need}" })
                    {
                        StatusCode = StatusCodes.Status403Forbidden
                    };
                    return;
                }
            }
        }
    }

    public static class ClaimsPrincipalExtensions
    {
        public static bool HasPermission(this System.Security.Claims.ClaimsPrincipal user, string code)
        {
            return user.FindAll(PermissionClaimType.Permission).Any(c => c.Value == code);
        }
    }
}

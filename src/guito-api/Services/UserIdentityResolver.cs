using System.Security.Claims;
using GuitoApi.Model;

namespace GuitoApi.Services
{
    public class UserIdentityResolver : IUserIdentityResolver
    {
        // These fallback values predate the conventions sweep and stay: they are what
        // already got written into the Expenses sheet creator column for agent-path
        // requests (no human email claim present), so changing them alters sheet data.
        private const string FallbackUserName = "Meireles";
        private const string FallbackUserEmail = "xunga.meireles@gmail.com";

        private readonly IHttpContextAccessor _httpContextAccessor;

        public UserIdentityResolver(IHttpContextAccessor httpContextAccessor) =>
            _httpContextAccessor = httpContextAccessor;

        public string GetEmail() => ResolveUserIdentity().Email;

        public UserIdentity ResolveUserIdentity()
        {
            if (_httpContextAccessor.HttpContext is null)
                throw new InvalidOperationException("HttpContext is not available");

            var userClaims = _httpContextAccessor.HttpContext.User.Claims.ToList();

            return new UserIdentity
            {
                Name = userClaims.FirstOrDefault(x => x.Type == ClaimTypes.Name)?.Value ?? FallbackUserName,
                Email = userClaims.FirstOrDefault(x => x.Type == ClaimTypes.Email)?.Value ?? FallbackUserEmail
            };
        }
    }
}